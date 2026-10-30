using System;
using System.Collections.Generic;
using System.Globalization;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/fuel.py 포팅 — 랩 완료 시 연료 분석.
    /// 최근 3랩 평균 소모량으로 남은 랩 수를 계산하고, 레이스 잔여 랩과
    /// 비교해 피트 윈도우를 추정한다.
    /// </summary>
    public sealed class FuelAnalyzer
    {
        private readonly double _warnLaps;
        private readonly double _criticalLaps;
        private readonly double _saveDelta;

        public FuelAnalyzer(double warnLaps = 3.0, double criticalLaps = 1.5,
                            double saveDelta = 0.1)
        {
            _warnLaps = warnLaps;
            _criticalLaps = criticalLaps;
            _saveDelta = saveDelta;
        }

        /// <summary>가공된 연료 상태 요약. 분석 불가(데이터 부족)면 null.</summary>
        public Dictionary<string, object> Status(RaceState state, Snapshot snap)
        {
            if (snap.Player == null)
                return null;
            double fuelNow = snap.Player.Fuel;

            var burns = new List<double>();
            foreach (LapRecord l in state.RecentLaps(3))
            {
                if (l.FuelUsed >= 0.05)
                    burns.Add(l.FuelUsed);
            }
            if (burns.Count == 0)
                return null;
            double avgBurn = 0;
            foreach (double b in burns)
                avgBurn += b;
            avgBurn /= burns.Count;
            if (avgBurn <= 0.01)
                return null;
            double fuelLaps = fuelNow / avgBurn;

            int? raceLapsLeft = RaceLapsLeft(state);
            bool pitNeeded = raceLapsLeft.HasValue && fuelLaps < raceLapsLeft.Value;

            var st = new Dictionary<string, object>
            {
                { "fuel_l", Math.Round(fuelNow, 1) },
                { "burn_per_lap", Math.Round(avgBurn, 2) },
                { "fuel_laps", Math.Round(fuelLaps, 1) },
                { "race_laps_left", raceLapsLeft.HasValue ? (object)raceLapsLeft.Value : null },
                { "pit_needed", pitNeeded },
                // 안전 마진 1랩을 빼고 이 랩 안에는 들어와야 함
                { "pit_window_laps",
                  pitNeeded ? (object)Math.Max((int)fuelLaps - 1, 0) : null },
            };
            return st;
        }

        /// <summary>
        /// 남은 레이스 랩(지금 랩 포함, 0=체커 받음) — RaceEnd가 이번 라인 통과에서 계산한 값.
        /// </summary>
        private static int? RaceLapsLeft(RaceState state)
        {
            return state.IsRace ? state.RaceEnd.LapsLeft : null;
        }

        public Dictionary<string, object> OnLap(RaceState state, Snapshot snap,
                                                EventBus bus)
        {
            Dictionary<string, object> st = Status(state, snap);
            if (st == null)
                return null;
            object left = st["race_laps_left"];
            if (left != null && (int)left == 0)
                return st;                   // 체커 받은 뒤 연료 콜 무의미

            double fuelLaps = (double)st["fuel_laps"];
            if (fuelLaps <= _criticalLaps)
            {
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.FuelCritical,
                    Priority = Priority.Critical,
                    Data = st,
                });
            }
            else if (fuelLaps <= _warnLaps)
            {
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.FuelWarning,
                    Priority = Priority.High,
                    Data = st,
                });
            }
            // 피트 윈도우 마지막 랩 도달 → 박스 콜
            object pw = st["pit_window_laps"];
            if ((bool)st["pit_needed"] && pw != null && (int)pw <= 1)
            {
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.PitCall,
                    Priority = Priority.Critical,
                    Data = st,
                });
            }
            SaveCoaching(st, bus);
            return st;
        }

        private void SaveCoaching(Dictionary<string, object> st, EventBus bus)
        {
            // 노피트 경계 상황 (연료 랩 < 잔여 랩 ≤ 연료 랩+2)에서 오버 소모 시
            object leftObj = st["race_laps_left"];
            if (leftObj == null)
                return;
            int left = (int)leftObj;
            if (left <= 0)
                return;
            double fuelLaps = (double)st["fuel_laps"];
            if (!(fuelLaps < left && left <= fuelLaps + 2.0))
                return;
            double target = (double)st["fuel_l"] / left;
            double delta = (double)st["burn_per_lap"] - target;
            if (delta < _saveDelta)
                return;
            var ev = new RadioEvent
            {
                Type = EventTypes.FuelSave,
                Priority = Priority.Normal,
                Message = Messages.Get("fuel_save",
                    "target", target, "delta", delta),
            };
            ev.Data["target"] = Math.Round(target, 2);
            ev.Data["delta"] = Math.Round(delta, 2);
            ev.Data["laps_left"] = left;
            bus.Push(ev);
        }
    }
}
