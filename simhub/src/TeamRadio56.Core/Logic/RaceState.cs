using System;
using System.Collections.Generic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>state.py LapRecord 포팅 — 완료한 랩 하나의 기록.</summary>
    public sealed class LapRecord
    {
        public int LapNumber;          // 완료한 랩 번호 (1부터)
        public double LapTime;
        public double S1;
        public double S2;              // 구간값 (원시는 S1+S2 누적)
        public double S3;
        public int Place;              // 전체 순위 (멀티클래스 통합)
        public int ClassPlace;         // 클래스 내 순위 — 멀티클래스의 진짜 순위
        public double FuelLeft;
        public double FuelUsed;        // 이 랩에서 쓴 연료 (모르면/급유 랩은 -1)
        public double GapAhead;        // 랩 완료 시점 동클래스 앞차 갭 (없으면 -1)
        public double GapBehind;
        public double[] TyreWear = new double[0];     // FL FR RL RR 남은 수명 비율
        public double[] TyreTemps = new double[0];    // 카커스 온도 C
        public double[] BrakeTemps = new double[0];
        public bool InPits;            // 이 랩에 피트를 지났는가
        public double TrackTemp;
        public double Raining;
        public bool Valid;             // 분석(평균 등)에 쓸 수 있는 랩인가
    }

    /// <summary>
    /// state.py SessionState 포팅 — 세션 상태 + 랩 히스토리.
    /// 분석기는 "지금 값"이 아니라 여기 축적된 추세를 본다.
    /// (JSON 저장은 아직 파이썬 엔진 담당 — 이식 마지막 단계.)
    /// </summary>
    public sealed class RaceState
    {
        public const int RaceSessionMin = 10;   // mSession 10-13 = race

        public int? SessionType;
        public string Track = "";
        public double TrackLen;
        public readonly List<LapRecord> Laps = new List<LapRecord>();
        public string PlayerClass = "";
        public string DriverName = "";
        public int TotalVehicles;
        public int ClassVehicles;
        public readonly List<string> Narrative = new List<string>();
        public readonly Dictionary<string, string> Issues =
            new Dictionary<string, string>();
        public int StintStartLap;

        private int? _lastTotalLaps;
        private bool _pitSeenThisLap;
        private double? _lastFuel;
        private double _lastSessionEt = -1.0;

        public bool IsRace
        {
            get { return SessionType.HasValue && SessionType.Value >= RaceSessionMin; }
        }

        public void Reset()
        {
            SessionType = null;
            Track = "";
            TrackLen = 0.0;
            Laps.Clear();
            PlayerClass = "";
            DriverName = "";
            TotalVehicles = 0;
            ClassVehicles = 0;
            Narrative.Clear();
            Issues.Clear();
            StintStartLap = 0;
            _lastTotalLaps = null;
            _pitSeenThisLap = false;
            _lastFuel = null;
            _lastSessionEt = -1.0;
        }

        public List<LapRecord> RecentLaps(int n = 3, bool validOnly = true)
        {
            var pool = new List<LapRecord>();
            foreach (LapRecord l in Laps)
            {
                if (!validOnly || l.Valid)
                    pool.Add(l);
            }
            int start = Math.Max(pool.Count - n, 0);
            return pool.GetRange(start, pool.Count - start);
        }

        /// <summary>최근 유효 랩들의 중앙값 — '평소 페이스'. 없으면 null.</summary>
        public double? BaselineLapTime(int n = 5)
        {
            var times = new List<double>();
            foreach (LapRecord l in RecentLaps(n))
            {
                if (l.LapTime > 0)
                    times.Add(l.LapTime);
            }
            if (times.Count == 0)
                return null;
            times.Sort();
            return times[times.Count / 2];
        }

        public void AddNarrative(string text)
        {
            Narrative.Add(text);
            while (Narrative.Count > 60)
                Narrative.RemoveAt(0);
        }

        public void SetIssue(string key, string text)
        {
            string cur;
            if (!Issues.TryGetValue(key, out cur) || cur != text)
                Issues[key] = text;
        }

        public void ClearIssue(string key)
        {
            Issues.Remove(key);
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// 매 폴링마다 호출. 랩 완료 순간이면 LapRecord 반환, 아니면 null.
        /// 세션 전환도 여기서 감지해 자동 reset한다.
        /// </summary>
        public LapRecord Update(Snapshot snap)
        {
            VehicleInfo me = snap.PlayerScoring();
            if (me == null)
                return null;
            SessionInfo ses = snap.Session;

            // 세션 전환 감지: 세션 타입/트랙 변경, 또는 ET가 크게 뒤로 감
            if (SessionType.HasValue
                && (ses.SessionType != SessionType.Value
                    || ses.Track != Track
                    || ses.CurrentEt < _lastSessionEt - 30))
            {
                Reset();
            }

            if (!SessionType.HasValue)
            {
                SessionType = ses.SessionType;
                Track = ses.Track;
                TrackLen = ses.TrackLength;
                DriverName = me.Driver;
                PlayerClass = me.Class;
                TotalVehicles = ses.NumVehicles;
                int classCount = 0;
                foreach (VehicleInfo v in snap.Vehicles)
                {
                    if (v.Class == PlayerClass)
                        classCount++;
                }
                ClassVehicles = classCount;
                _lastTotalLaps = me.TotalLaps;
            }

            _lastSessionEt = ses.CurrentEt;

            // 피트 통과 추적 (이번 랩에 피트에 있었는지)
            if (me.InPits || (snap.Player != null && snap.Player.InPitLane))
                _pitSeenThisLap = true;

            LapRecord completed = null;
            if (_lastTotalLaps.HasValue && me.TotalLaps > _lastTotalLaps.Value)
                completed = OnLapComplete(snap, me);
            _lastTotalLaps = me.TotalLaps;
            return completed;
        }

        private LapRecord OnLapComplete(Snapshot snap, VehicleInfo me)
        {
            double lapTime = me.LastLap;
            double? fuelNow = snap.Player != null ? snap.Player.Fuel : (double?)null;

            double fuelUsed = -1.0;   // -1 = 알 수 없음(첫 랩) 또는 급유 랩
            if (fuelNow.HasValue && _lastFuel.HasValue)
            {
                double delta = _lastFuel.Value - fuelNow.Value;
                if (delta >= 0)
                    fuelUsed = Math.Round(delta, 3);
            }
            _lastFuel = fuelNow;

            double gapAhead, gapBehind;
            SameClassGaps(snap, me, out gapAhead, out gapBehind);

            WheelInfo[] wheels = snap.Player != null && snap.Player.Wheels != null
                ? snap.Player.Wheels : new WheelInfo[0];
            bool pitLap = _pitSeenThisLap;
            _pitSeenThisLap = false;
            if (pitLap)
                StintStartLap = me.TotalLaps + 1;

            var wear = new double[wheels.Length];
            var temps = new double[wheels.Length];
            var brakes = new double[wheels.Length];
            for (int i = 0; i < wheels.Length; i++)
            {
                wear[i] = wheels[i].Wear;
                temps[i] = wheels[i].CarcassTemp;
                brakes[i] = wheels[i].BrakeTemp;
            }

            var rec = new LapRecord
            {
                LapNumber = me.TotalLaps,
                LapTime = lapTime,
                S1 = Math.Round(me.LastS1, 3),
                S2 = me.LastS2 > 0 ? Math.Round(me.LastS2 - me.LastS1, 3) : 0.0,
                S3 = me.LastS2 > 0 ? Math.Round(lapTime - me.LastS2, 3) : 0.0,
                Place = me.Place,
                ClassPlace = ClassPlaceOf(snap, me),
                FuelLeft = fuelNow.HasValue ? Math.Round(fuelNow.Value, 2) : -1.0,
                FuelUsed = fuelUsed,
                GapAhead = gapAhead,
                GapBehind = gapBehind,
                TyreWear = wear,
                TyreTemps = temps,
                BrakeTemps = brakes,
                InPits = pitLap,
                TrackTemp = snap.Session.TrackTemp,
                Raining = snap.Session.Raining,
                Valid = lapTime > 0 && !pitLap,
            };
            Laps.Add(rec);
            return rec;
        }

        /// <summary>클래스 내 순위 (1-based).</summary>
        public static int ClassPlaceOf(Snapshot snap, VehicleInfo me)
        {
            int ahead = 0;
            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.Class == me.Class && v.Place > 0 && v.Place < me.Place)
                    ahead++;
            }
            return 1 + ahead;
        }

        /// <summary>
        /// 같은 클래스의 바로 앞/뒤 차와의 시간 갭 — mTimeBehindNext를 순위를
        /// 따라 같은 클래스 차를 만날 때까지 누적. 없으면 -1.
        /// </summary>
        public static void SameClassGaps(Snapshot snap, VehicleInfo me,
                                         out double ahead, out double behind)
        {
            var byPlace = new Dictionary<int, VehicleInfo>();
            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.Place > 0 && v.FinishStatus == 0)
                    byPlace[v.Place] = v;
            }
            ahead = -1.0;
            behind = -1.0;

            // 앞쪽: 내 갭부터 시작해 같은 클래스 차를 만날 때까지 위로 누적
            double total = 0.0;
            VehicleInfo cur = me;
            int p = me.Place - 1;
            while (p >= 1)
            {
                total += Math.Max(cur.TimeBehindNext, 0.0);
                VehicleInfo nxt;
                if (!byPlace.TryGetValue(p, out nxt))
                    break;
                if (nxt.Class == me.Class)
                {
                    ahead = Math.Round(total, 3);
                    break;
                }
                cur = nxt;
                p--;
            }

            // 뒤쪽: 아래 순위 차들의 갭을 같은 클래스 차를 만날 때까지 누적
            total = 0.0;
            p = me.Place + 1;
            VehicleInfo below;
            while (byPlace.TryGetValue(p, out below))
            {
                total += Math.Max(below.TimeBehindNext, 0.0);
                if (below.Class == me.Class)
                {
                    behind = Math.Round(total, 3);
                    break;
                }
                p++;
            }
        }
    }
}
