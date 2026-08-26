using System;
using System.Collections.Generic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// 오버레이용 실시간 상태 — SimHub 프로퍼티로 노출돼 Dash Studio
    /// 오버레이의 데이터원이 된다 (무전 자막/스포터 바/클래스 갭).
    ///
    /// 엔진 모드와 무관하게 공유 메모리 스냅샷에서 직접 계산한다:
    /// 스포터 표시는 5Hz 즉답이 필요해 1초 주기 상태 파일로는 늦고,
    /// 판단 로직(트래픽 분석기)과 달리 시각화는 상태 머신이 필요 없다.
    /// 프라이빗 세션의 타이밍 전용 유령은 트래픽과 같은 좌표 이동
    /// 필터로 걸러낸다.
    /// </summary>
    public sealed class OverlayState
    {
        private const double MovedMinM = 15.0;
        private const double SideLatMin = 1.2;

        public bool SpotterLeft { get; private set; }
        public bool SpotterRight { get; private set; }
        /// <summary>물리적으로 존재하는 가장 가까운 차와의 트랙 거리 (m, 없으면 -1).</summary>
        public double NearestAheadM { get; private set; } = -1;
        public double NearestBehindM { get; private set; } = -1;
        /// <summary>동클래스 앞/뒤차와의 시간 갭 (초, 없으면 -1).</summary>
        public double GapAheadSec { get; private set; } = -1;
        public double GapBehindSec { get; private set; } = -1;
        public int Position { get; private set; }
        public int ClassPosition { get; private set; }
        public bool InSession { get; private set; }

        private sealed class Seen
        {
            public double[] FirstPos;
            public bool Moved;
        }

        private readonly Dictionary<int, Seen> _seen = new Dictionary<int, Seen>();

        public void Reset()
        {
            _seen.Clear();
            Clear();
        }

        private void Clear()
        {
            SpotterLeft = false;
            SpotterRight = false;
            NearestAheadM = -1;
            NearestBehindM = -1;
            GapAheadSec = -1;
            GapBehindSec = -1;
            Position = 0;
            ClassPosition = 0;
            InSession = false;
        }

        public void Update(Snapshot snap, double alongsideM, bool sideInvert)
        {
            Clear();
            if (snap == null || !snap.InSession || snap.Session == null)
                return;
            VehicleInfo me = snap.PlayerScoring();
            double trackLen = snap.Session.TrackLength;
            if (me == null || trackLen <= 0)
                return;

            InSession = true;
            Position = me.Place;
            ClassPosition = RaceState.ClassPlaceOf(snap, me);

            double gapAhead, gapBehind;
            RaceState.SameClassGaps(snap, me, out gapAhead, out gapBehind);
            GapAheadSec = gapAhead;
            GapBehindSec = gapBehind;

            var alive = new HashSet<int>();
            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.IsPlayer)
                    continue;
                alive.Add(v.Id);
                if (v.InPits || v.InGarage || v.FinishStatus != 0)
                    continue;
                if (!HasMoved(v))
                    continue;    // 미스폰/타이밍 전용 유령 (프라이빗 세션)

                double gap = TrafficAnalyzer.WrapGap(v.LapDist - me.LapDist, trackLen);
                if (gap > 0)
                {
                    if (NearestAheadM < 0 || gap < NearestAheadM)
                        NearestAheadM = Math.Round(gap, 1);
                }
                else if (gap < 0)
                {
                    if (NearestBehindM < 0 || -gap < NearestBehindM)
                        NearestBehindM = Math.Round(-gap, 1);
                }

                if (Math.Abs(gap) <= alongsideM)
                {
                    double latDiff = v.PathLateral - me.PathLateral;
                    if (Math.Abs(latDiff) >= SideLatMin)
                    {
                        bool left = latDiff < 0;
                        if (sideInvert)
                            left = !left;
                        if (left)
                            SpotterLeft = true;
                        else
                            SpotterRight = true;
                    }
                    else
                    {
                        // 횡간격 애매 = 정면 겹침 — 양쪽 다 켜서 경고
                        SpotterLeft = true;
                        SpotterRight = true;
                    }
                }
            }

            // 사라진 차 정리 (세션 전환/피트아웃 재사용 대비)
            var gone = new List<int>();
            foreach (int id in _seen.Keys)
            {
                if (!alive.Contains(id))
                    gone.Add(id);
            }
            foreach (int id in gone)
                _seen.Remove(id);
        }

        private bool HasMoved(VehicleInfo v)
        {
            double[] pos = v.Pos != null && v.Pos.Length >= 3 ? v.Pos : null;
            Seen s;
            if (!_seen.TryGetValue(v.Id, out s))
            {
                s = new Seen { FirstPos = pos != null ? (double[])pos.Clone() : null };
                _seen[v.Id] = s;
                return false;
            }
            if (s.Moved)
                return true;
            if (s.FirstPos == null || pos == null)
            {
                s.Moved = true;    // 좌표 없는 데이터 — 필터 불가, 통과
                return true;
            }
            double dx = pos[0] - s.FirstPos[0];
            double dy = pos[1] - s.FirstPos[1];
            double dz = pos[2] - s.FirstPos[2];
            if (dx * dx + dy * dy + dz * dz > MovedMinM * MovedMinM)
                s.Moved = true;
            return s.Moved;
        }
    }
}
