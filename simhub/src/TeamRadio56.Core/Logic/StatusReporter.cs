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
                string text = Messages.Get("last_lap_report",
                    "t", Messages.FmtLaptime(lap.LapTime));
                if (isBest)
                    text += Messages.Get("best_lap_suffix");
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
                parts.Add(Messages.Get("status_pos", "p", lap.ClassPlace));
            var gaps = new List<string>();
            if (0 <= lap.GapAhead && lap.GapAhead <= 60)
                gaps.Add(Messages.Get("status_gap_ahead", "g", lap.GapAhead));
            if (0 <= lap.GapBehind && lap.GapBehind <= 60)
                gaps.Add(Messages.Get("status_gap_behind", "g", lap.GapBehind));
            if (gaps.Count > 0)
                parts.Add(Messages.Get("status_gaps", "gaps", string.Join(", ", gaps)));
            object fuelLaps;
            if (fuelStatus != null && fuelStatus.TryGetValue("fuel_laps", out fuelLaps)
                && fuelLaps != null)
            {
                parts.Add(Messages.Get("status_fuel",
                    "n", Messages.LapsText((double)fuelLaps)));
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
                    parts.Add(Messages.Get("status_tyres",
                        "n", Messages.LapsText(left.Value)));
                }
                else
                {
                    parts.Add(Messages.Get("status_tyres_good"));
                }
            }
            if (parts.Count == 0)
                return null;
            return string.Join(" ", parts);
        }
    }
}
