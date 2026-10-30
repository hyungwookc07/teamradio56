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
                string text = LaptimeText(lap.LapTime, isBest);
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
            double? fuel = null;
            object fuelLaps;
            if (fuelStatus != null && fuelStatus.TryGetValue("fuel_laps", out fuelLaps)
                && fuelLaps != null)
                fuel = (double)fuelLaps;
            bool hasTyre = false;
            double? left = null;
            object worstObj;
            if (tyreStatus != null && tyreStatus.TryGetValue("worst", out worstObj)
                && worstObj != null)
            {
                hasTyre = true;
                var worst = (Dictionary<string, object>)worstObj;
                object leftObj;
                if (worst.TryGetValue("laps_left", out leftObj) && leftObj != null)
                    left = (double)leftObj;
            }
            return StatusText(lap.ClassPlace, lap.GapAhead, lap.GapBehind, fuel, left, hasTyre);
        }

        /// <summary>랩타임 콜 — reporter.StatusReporter.laptime_text 미러.</summary>
        public static string LaptimeText(double lapTime, bool isBest)
        {
            string text = Messages.Get("last_lap_report", "t", Messages.FmtLaptime(lapTime));
            if (isBest)
                text += Messages.Get("best_lap_suffix");
            return text;
        }

        /// <summary>상황 리포트 한 문장 — 순수 조립 (reporter.StatusReporter.status_text 미러).</summary>
        public static string StatusText(int classPlace, double gapAhead, double gapBehind,
                                        double? fuelLaps, double? tyreLeft, bool hasTyre)
        {
            // P61 이상·연료 100랩 이상은 그 부분만 뺀다 (음성 팩 3단계 도메인)
            var parts = new List<string>();
            if (classPlace > 0 && classPlace <= SpeechDomain.ClassPosMax)
                parts.Add(Messages.Get("status_pos", "p", classPlace));
            var gaps = new List<string>();
            if (0 <= gapAhead && gapAhead <= 60)
            {
                gaps.Add(Messages.Get("status_gap_ahead",
                    "g", gapAhead, "gs", Messages.SecondsText(gapAhead)));
            }
            if (0 <= gapBehind && gapBehind <= 60)
            {
                gaps.Add(Messages.Get("status_gap_behind",
                    "g", gapBehind, "gs", Messages.SecondsText(gapBehind)));
            }
            if (gaps.Count > 0)
            {
                parts.Add(Messages.Get("status_gaps",
                    "gaps", string.Join(Messages.Get("status_gap_join"), gaps)));
            }
            if (fuelLaps.HasValue && SpeechDomain.StatusFuelOk(fuelLaps.Value))
                parts.Add(Messages.Get("status_fuel", "n", Messages.LapsText(fuelLaps.Value)));
            if (hasTyre)
            {
                if (tyreLeft.HasValue && tyreLeft.Value <= 20)
                    parts.Add(Messages.Get("status_tyres", "n", Messages.LapsText(tyreLeft.Value)));
                else
                    parts.Add(Messages.Get("status_tyres_good"));
            }
            if (parts.Count == 0)
                return null;
            return string.Join(" ", parts);
        }
    }
}
