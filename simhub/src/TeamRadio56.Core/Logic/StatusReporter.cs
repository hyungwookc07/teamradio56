using System;
using System.Collections.Generic;
using System.Globalization;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/reporter.py 포팅 — HUD 대체 정기 무전 (기본 꺼짐).
    /// 매 랩 랩타임 콜과 N랩마다 순위/갭/연료/타이어 종합 리포트.
    /// </summary>
    public sealed class StatusReporter
    {
        private readonly bool _laptimeOn;
        private readonly int _statusLaps;
        private int _lastStatusLap;

        public StatusReporter(bool laptimeEveryLap = false, int statusEveryLaps = 0)
        {
            _laptimeOn = laptimeEveryLap;
            _statusLaps = statusEveryLaps;
        }

        public void Reset()
        {
            _lastStatusLap = 0;
        }

        public bool Enabled
        {
            get { return _laptimeOn || _statusLaps > 0; }
        }

        private static string FmtLaptime(double sec)
        {
            double m = Math.Floor(sec / 60.0);
            double s = sec - m * 60.0;
            if (m >= 1)
            {
                // "2 01.8"은 TTS가 201.8로 읽는다 — "2 oh 1.8" (two oh one point eight)
                return ((long)m).ToString(CultureInfo.InvariantCulture) + " "
                       + (s < 10 ? "oh " : "")
                       + s.ToString("F1", CultureInfo.InvariantCulture);
            }
            return s.ToString("F1", CultureInfo.InvariantCulture);
        }

        public void OnLap(RaceState state, Snapshot snap, EventBus bus,
                          Dictionary<string, object> fuelStatus,
                          Dictionary<string, object> tyreStatus)
        {
            if (!Enabled || state.Laps.Count == 0)
                return;
            LapRecord lap = state.Laps[state.Laps.Count - 1];

            if (_laptimeOn && lap.Valid && lap.LapTime > 0)
            {
                double best = double.MaxValue;
                int validCount = 0;
                foreach (LapRecord l in state.Laps)
                {
                    if (l.Valid)
                    {
                        validCount++;
                        if (l.LapTime < best)
                            best = l.LapTime;
                    }
                }
                bool isBest = validCount >= 2 && lap.LapTime <= best;
                string text = "Last lap " + FmtLaptime(lap.LapTime) + ".";
                if (isBest)
                    text += " Best lap.";
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.LapTimeReport,
                    Priority = Priority.Normal,
                    Message = text,
                    DedupKey = "laptime_" + lap.LapNumber,
                    Ttl = 15.0,
                    Tone = "casual",
                });
            }

            if (_statusLaps > 0 && lap.LapNumber - _lastStatusLap >= _statusLaps)
            {
                string message = ComposeStatus(lap, fuelStatus, tyreStatus);
                if (message != null)
                {
                    bool ok = bus.Push(new RadioEvent
                    {
                        Type = EventTypes.StatusReport,
                        Priority = Priority.Normal,
                        Message = message,
                        DedupKey = "status_" + lap.LapNumber,
                        Ttl = 40.0,
                        Tone = "casual",
                    });
                    if (ok)
                        _lastStatusLap = lap.LapNumber;
                }
            }
        }

        private static string ComposeStatus(LapRecord lap,
                                            Dictionary<string, object> fuelStatus,
                                            Dictionary<string, object> tyreStatus)
        {
            var parts = new List<string>();
            if (lap.ClassPlace > 0)
                parts.Add("P" + lap.ClassPlace + ".");
            var gaps = new List<string>();
            if (0 <= lap.GapAhead && lap.GapAhead <= 60)
                gaps.Add("ahead " + lap.GapAhead.ToString("F1", CultureInfo.InvariantCulture));
            if (0 <= lap.GapBehind && lap.GapBehind <= 60)
                gaps.Add("behind " + lap.GapBehind.ToString("F1", CultureInfo.InvariantCulture));
            if (gaps.Count > 0)
                parts.Add("Gap " + string.Join(", ", gaps) + ".");
            object fuelLaps;
            if (fuelStatus != null && fuelStatus.TryGetValue("fuel_laps", out fuelLaps)
                && fuelLaps != null)
            {
                parts.Add("Fuel " + VoiceRenderer.LapsText((double)fuelLaps) + ".");
            }
            object worstObj;
            if (tyreStatus != null && tyreStatus.TryGetValue("worst", out worstObj)
                && worstObj != null)
            {
                var worst = (Dictionary<string, object>)worstObj;
                object leftObj;
                double? left = worst.TryGetValue("laps_left", out leftObj)
                    ? (double?)(double)leftObj : null;
                if (left.HasValue && left.Value <= 20)
                {
                    parts.Add("Tyres " + VoiceRenderer.LapsText(left.Value) + ".");
                }
                else
                {
                    parts.Add("Tyres good.");
                }
            }
            if (parts.Count == 0)
                return null;
            return string.Join(" ", parts);
        }
    }
}
