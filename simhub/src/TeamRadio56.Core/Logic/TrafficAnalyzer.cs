using System;
using System.Collections.Generic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>트래픽 분석기 설정 (config.py DEFAULTS.thresholds의 해당 항목).</summary>
    public sealed class TrafficSettings
    {
        public double ProximityM = 50.0;
        public double AlongsideM = 4.6;
        public double EtaWarnSec = 10.0;
        public bool RaceOnly = true;
        public bool SideInvert = false;
        public double StartSpotterSec = 45.0;
    }

    /// <summary>
    /// analyzers/traffic.py 포팅 — 차량별 상태 머신 (5Hz 매 틱 호출).
    ///
    /// 각 상대 차량을 ID로 추적하고, 상태가 "전이될 때만" 발화한다.
    /// 동시에 여러 대가 관련되면 한 문장으로 종합. 스타트 직후에는
    /// 스포터 모드(좌우 점유 콜만). 의미 동작은 파이썬과 1:1이어야 하며,
    /// tests/TeamRadio56.Replay가 리플레이로 파이썬과 대조 검증한다.
    /// </summary>
    public sealed class TrafficAnalyzer
    {
        // 상태 상수
        private const string Far = "far";
        private const string Approaching = "approaching";
        private const string NearbyBehind = "nearby_behind";
        private const string NearbyAhead = "nearby_ahead";
        private const string Alongside = "alongside";

        private static int ThreatRank(string state)
        {
            switch (state)
            {
                case Alongside: return 3;
                case NearbyBehind: return 2;
                case Approaching: return 1;
                default: return 0;   // NearbyAhead, Far
            }
        }

        private const double MinClosingMs = 2.0;      // m/s — 이 이상 좁혀질 때만 '접근'
        private const double CallLatencySec = 0.5;    // 판정~재생 지연의 리드 보정
        private const double AlongsideLeadMaxM = 5.0; // 리드 보정 상한
        private const double SideLatMin = 1.2;        // 좌우 판정 최소 횡간격 (m)
        private const double StateReannounceSec = 45.0;
        private const double StoppedSpeedMs = 12.0;   // 정지/서행 후보 (43km/h)
        private const double StoppedPersistSec = 4.0;
        private const double MyRacingSpeedMs = 25.0;
        private const double HazardAheadM = 250.0;
        // 이동 필터 — 미스폰/관전 슬롯(좌표 고정)을 걸러낸다.
        // 주의(실차 확인): LMU는 프라이빗 세션에서도 좌표를 스트리밍하므로
        // 프라이빗 유령은 RaceOnly(기본 켜짐)가 담당한다.
        private const double MovedMinM = 15.0;
        private const double SpotClearHoldSec = 1.2;  // 깜빡임 방지

        /// <summary>lapDist 차이를 [-L/2, L/2) 부호 있는 거리로 보정. +면 내 앞.</summary>
        public static double WrapGap(double deltaM, double trackLen)
        {
            double half = trackLen / 2.0;
            double m = (deltaM + half) % trackLen;
            if (m < 0)
                m += trackLen;    // 파이썬 %는 항상 양수 — C#은 부호 보정 필요
            return m - half;
        }

        // 클래스 서열 — LMU는 mEstimatedLapTime이 전 차량 동일값이라
        // 클래스 이름 서열로 랩핑 트래픽을 판정한다. 숫자가 클수록 빠름.
        private static readonly KeyValuePair<string, int>[] ClassRanks =
        {
            new KeyValuePair<string, int>("hyper", 4),
            new KeyValuePair<string, int>("lmh", 4),
            new KeyValuePair<string, int>("lmdh", 4),
            new KeyValuePair<string, int>("lmp2", 3),
            new KeyValuePair<string, int>("gte", 2),
            new KeyValuePair<string, int>("gt3", 1),
        };

        public static int ClassRank(string cls)
        {
            string c = (cls ?? "").ToLowerInvariant();
            foreach (KeyValuePair<string, int> kv in ClassRanks)
            {
                if (c.Contains(kv.Key))
                    return kv.Value;
            }
            return 0;   // 서열 불명
        }

        /// <summary>클래스 표기 — Messages.ClassDisplay 위임 (기존 호출부 호환).</summary>
        public static string ClassName(string cls)
        {
            return Messages.ClassDisplay(cls);
        }

        private sealed class CarTrack
        {
            public int Cid;
            public string Cls;
            public string Driver;
            public string State = Far;
            public double GapM;
            public double? Rate;          // dgap/dt EMA (m/s)
            public double? SpeedEst;      // 상대 절대속도 추정 (m/s)
            public string Side;           // left | right | null
            public bool Faster;
            public double LapDelta;
            public bool Lapping;
            public bool Backmarker;
            public double? SlowSince;
            public double[] FirstPos;      // 첫 관측 월드 좌표 (실존 필터 기준)
            public double? FirstLapDist;   // pos 없는 데이터용 폴백 기준
            public int FirstTotalLaps;
            public bool Moved;             // 관측 이후 물리적으로 움직인 적이 있는가
            public bool Seen;
            public double LastSampleT;
            public readonly Dictionary<string, double> Announced =
                new Dictionary<string, double>();
            public bool Engaged;
            public long Seq;              // 삽입 순서 — 파이썬 dict 순회 순서 재현
        }

        private readonly TrafficSettings _cfg;
        private readonly Dictionary<int, CarTrack> _tracks = new Dictionary<int, CarTrack>();
        private readonly Dictionary<int, double> _hazardAnnounced = new Dictionary<int, double>();
        private double? _greenT;
        private int? _prevPhase;
        private long _nextSeq;
        private Dictionary<string, bool> _spot;    // 스포터: 발화된 좌우 점유 상태
        private readonly Dictionary<string, double?> _spotClearSince =
            new Dictionary<string, double?> { { "left", null }, { "right", null } };

        public TrafficAnalyzer(TrafficSettings cfg)
        {
            _cfg = cfg ?? new TrafficSettings();
        }

        public void Reset()
        {
            _tracks.Clear();
            _hazardAnnounced.Clear();
            _greenT = null;
            _prevPhase = null;
            _spot = null;
            _spotClearSince["left"] = null;
            _spotClearSince["right"] = null;
        }

        // -- 외부 조회 (브리지 유효성 검사 등에 사용) -------------------------

        public string CarState(int cid)
        {
            CarTrack t;
            return _tracks.TryGetValue(cid, out t) ? t.State : Far;
        }

        /// <summary>동클래스 같은 랩 차량과 근접 경쟁 중인가.</summary>
        public bool InBattle()
        {
            foreach (CarTrack t in OrderedTracks())
            {
                if ((t.State == NearbyBehind || t.State == NearbyAhead || t.State == Alongside)
                    && !t.Faster && !t.Backmarker)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>파이썬 dict 순회 순서(삽입 순) 재현.</summary>
        private List<CarTrack> OrderedTracks()
        {
            var list = new List<CarTrack>(_tracks.Values);
            list.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            return list;
        }

        // -- 메인 틱 -----------------------------------------------------------

        public void OnTick(RaceState state, Snapshot snap, EventBus bus)
        {
            VehicleInfo me = snap.PlayerScoring();
            if (me == null || me.InPits || me.InGarage)
            {
                _tracks.Clear();
                return;
            }
            int phase = snap.Session.GamePhase;
            // 레이스 스타트(그린 전이) 감지 → 혼전 정숙 구간 시작
            if ((_prevPhase == 3 || _prevPhase == 4) && phase == 5 && state.IsRace)
                _greenT = snap.T;
            _prevPhase = phase;
            if (phase != 5 && phase != 6)
                return;
            if (_cfg.RaceOnly && !state.IsRace)
                return;
            double trackLen = snap.Session.TrackLength;
            if (trackLen <= 0)
                return;
            double now = snap.T;
            bool spotterMode = _greenT.HasValue && now - _greenT.Value < _cfg.StartSpotterSec;
            double mySpeed = (snap.Player != null ? snap.Player.SpeedKmh : 0.0) / 3.6;

            var transitions = new List<KeyValuePair<CarTrack, string>>();
            var seen = new HashSet<int>();

            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.IsPlayer || v.InPits || v.InGarage || v.FinishStatus != 0)
                    continue;
                seen.Add(v.Id);
                CarTrack t = UpdateTrack(v, me, trackLen, now, mySpeed);
                // 한 번도 움직인 적 없는 엔트리 = 미스폰/관전 슬롯 — 무시
                // (프라이빗 연습/퀄리 유령 콜의 주범)
                if (!t.Moved)
                    continue;
                if (IsStopped(t, mySpeed, now))
                {
                    // 정지 차량 위험 안내는 레이스에서만 (연습/퀄리는 게임이
                    // 고스트 처리하므로 소음)
                    if (state.IsRace)
                        CheckStoppedHazard(t, now, bus);
                    if (t.State != Far)
                        t.State = Far;    // 조용히 리셋 (후속 멘트 없이)
                    continue;
                }
                string newState = Classify(t);
                if (!t.Seen)
                {
                    // 첫 관측: 현재 상태로 조용히 진입
                    t.Seen = true;
                    t.State = newState;
                    continue;
                }
                if (newState != t.State)
                {
                    string old = t.State;
                    t.State = newState;
                    transitions.Add(new KeyValuePair<CarTrack, string>(t, old));
                }
            }
            // 사라진 차량(피트인 등) 정리
            var gone = new List<int>();
            foreach (int cid in _tracks.Keys)
            {
                if (!seen.Contains(cid))
                    gone.Add(cid);
            }
            foreach (int cid in gone)
                _tracks.Remove(cid);

            if (spotterMode)
            {
                SpotterTick(now, bus);
            }
            else if (transitions.Count > 0)
            {
                _spot = null;    // 스포터 모드 종료 → 기준 리셋
                Emit(transitions, now, bus);
            }

            // 동클래스 배틀 여부 (LLM 문맥 연속성용)
            CarTrack battler = null;
            foreach (CarTrack t in OrderedTracks())
            {
                if ((t.State == NearbyBehind || t.State == Alongside)
                    && !t.Faster && !t.Backmarker)
                {
                    battler = t;
                    break;
                }
            }
            if (battler != null)
                state.SetIssue("battle", battler.Cls + " (" + battler.Driver + ")와 포지션 배틀 중");
            else
                state.ClearIssue("battle");
        }

        // -- 스포터 모드 (스타트 혼전) ------------------------------------------

        private void SpotterTick(double now, EventBus bus)
        {
            var occ = new Dictionary<string, bool> { { "left", false }, { "right", false } };
            foreach (CarTrack t in OrderedTracks())
            {
                if (t.State == Alongside && t.Side != null && occ.ContainsKey(t.Side))
                    occ[t.Side] = true;
            }

            if (_spot == null)
            {
                _spot = new Dictionary<string, bool>(occ);
                return;
            }

            // 양쪽 동시 점유로 전이 → 한 번에 "양쪽" 콜
            if (occ["left"] && occ["right"] && !(_spot["left"] && _spot["right"]))
            {
                var both = new RadioEvent
                {
                    Type = EventTypes.Spotter,
                    Priority = Priority.Critical,
                    DedupKey = "spot_both",
                    Tone = "urgent",
                    Ttl = 2.5,
                };
                both.Data["pool"] = "alongside_both";
                if (bus.Push(both))
                {
                    _spot["left"] = true;
                    _spot["right"] = true;
                    _spotClearSince["left"] = null;
                    _spotClearSince["right"] = null;
                }
                return;
            }

            foreach (string side in new[] { "left", "right" })
            {
                if (occ[side])
                {
                    _spotClearSince[side] = null;
                    if (!_spot[side])
                    {
                        var ev = new RadioEvent
                        {
                            Type = EventTypes.Spotter,
                            Priority = Priority.Critical,
                            DedupKey = "spot_" + side,
                            Tone = "urgent",
                            Ttl = 2.5,
                        };
                        ev.Data["pool"] = "alongside_" + side;
                        if (bus.Push(ev))
                            _spot[side] = true;
                    }
                }
                else if (_spot[side])
                {
                    double? since = _spotClearSince[side];
                    if (!since.HasValue)
                    {
                        _spotClearSince[side] = now;
                    }
                    else if (now - since.Value >= SpotClearHoldSec)
                    {
                        var ev = new RadioEvent
                        {
                            Type = EventTypes.Spotter,
                            Priority = Priority.High,
                            DedupKey = "spotclr_" + side,
                            Tone = "casual",
                            Ttl = 3.0,
                        };
                        ev.Data["pool"] = "side_clear";
                        ev.Data["side"] = side;
                        if (bus.Push(ev))
                        {
                            _spot[side] = false;
                            _spotClearSince[side] = null;
                        }
                    }
                }
            }
        }

        private static bool IsStopped(CarTrack t, double mySpeed, double now)
        {
            return t.SlowSince.HasValue && mySpeed >= MyRacingSpeedMs
                && now - t.SlowSince.Value >= StoppedPersistSec;
        }

        private void CheckStoppedHazard(CarTrack t, double now, EventBus bus)
        {
            if (!(t.GapM > 0 && t.GapM <= HazardAheadM))
            {
                if (t.GapM < 0)
                    _hazardAnnounced.Remove(t.Cid);   // 지나가면 재안내 허용
                return;
            }
            double last;
            if (_hazardAnnounced.TryGetValue(t.Cid, out last) && now - last < 90.0)
                return;
            _hazardAnnounced[t.Cid] = now;
            bus.Push(new RadioEvent
            {
                Type = EventTypes.TrafficUpdate,
                Priority = Priority.High,
                Message = Messages.Get("stopped_hazard"),
                DedupKey = "hazard_" + t.Cid,
                Ttl = 8.0,
                Tone = "urgent",
            });
        }

        private CarTrack UpdateTrack(VehicleInfo v, VehicleInfo me,
                                     double trackLen, double now, double mySpeed)
        {
            CarTrack t;
            if (!_tracks.TryGetValue(v.Id, out t))
            {
                t = new CarTrack { Cid = v.Id, Cls = v.Class, Driver = v.Driver, Seq = _nextSeq++ };
                _tracks[v.Id] = t;
            }
            // 실존 필터: 월드 좌표가 실제로 움직인 적이 있는가. lap_dist는
            // 타이밍 전용 엔트리도 갱신되므로 기준이 못 된다.
            double[] pos = v.Pos != null && v.Pos.Length >= 3 ? v.Pos : null;
            if (!t.FirstLapDist.HasValue)
            {
                t.FirstLapDist = v.LapDist;
                t.FirstTotalLaps = v.TotalLaps;
                t.FirstPos = pos != null ? (double[])pos.Clone() : null;
            }
            else if (!t.Moved)
            {
                if (t.FirstPos != null && pos != null)
                {
                    double dx = pos[0] - t.FirstPos[0];
                    double dy = pos[1] - t.FirstPos[1];
                    double dz = pos[2] - t.FirstPos[2];
                    if (dx * dx + dy * dy + dz * dz > MovedMinM * MovedMinM)
                        t.Moved = true;
                }
                else if (Math.Abs(v.LapDist - t.FirstLapDist.Value) > MovedMinM
                         || v.TotalLaps != t.FirstTotalLaps)
                {
                    t.Moved = true;    // pos 없는 데이터(구버전 녹화) 폴백
                }
            }

            double gapM = WrapGap(v.LapDist - me.LapDist, trackLen);
            double dt = now - t.LastSampleT;
            if (t.LastSampleT > 0 && dt > 0.01 && dt <= 3.0)
            {
                double inst = WrapGap(gapM - t.GapM, trackLen) / dt;
                t.Rate = t.Rate.HasValue ? 0.6 * t.Rate.Value + 0.4 * inst : inst;
            }
            else if (dt > 3.0)
            {
                t.Rate = null;    // 오래된 샘플로 미분하지 않는다
            }
            t.SpeedEst = t.Rate.HasValue ? mySpeed + t.Rate.Value : (double?)null;
            if (t.SpeedEst.HasValue && t.SpeedEst.Value < StoppedSpeedMs)
            {
                if (!t.SlowSince.HasValue)
                    t.SlowSince = now;
            }
            else
            {
                t.SlowSince = null;
            }
            t.GapM = gapM;
            t.LastSampleT = now;
            // 랩 진행도 차이 — 랩핑/백마커 판정 기준
            double myProg = me.TotalLaps + me.LapDist / trackLen;
            double theirProg = v.TotalLaps + v.LapDist / trackLen;
            t.LapDelta = theirProg - myProg;

            int mine = ClassRank(me.Class);
            int theirs = ClassRank(v.Class);
            if (v.Class == me.Class)
            {
                t.Lapping = t.LapDelta >= 0.9;
                t.Faster = t.Lapping;
            }
            else
            {
                t.Lapping = false;
                if (mine > 0 && theirs > 0)
                    t.Faster = theirs > mine;
                else
                    t.Faster = true;   // 서열 불명 클래스 → 접근 예고 대상으로 취급
            }
            t.Backmarker = t.LapDelta <= -0.9 || (theirs > 0 && theirs < mine);

            // 좌우 판정: 나란할 때만 의미 있음
            double lat = v.PathLateral;
            double myLat = me.PathLateral;
            if (Math.Abs(lat - myLat) >= SideLatMin)
            {
                string side = lat < myLat ? "left" : "right";
                if (_cfg.SideInvert)
                    side = side == "left" ? "right" : "left";
                t.Side = side;
            }
            else
            {
                t.Side = null;
            }
            return t;
        }

        private string Classify(CarTrack t)
        {
            double g = t.GapM;
            // 나란히: 실제 차체 오버랩 기준 + 접근 속도만큼 리드 보정
            double lead = Math.Min(Math.Abs(t.Rate ?? 0.0) * CallLatencySec, AlongsideLeadMaxM);
            if (Math.Abs(g) <= _cfg.AlongsideM + lead)
                return Alongside;
            if (-_cfg.ProximityM <= g && g < 0)
                return NearbyBehind;
            if (0 < g && g <= _cfg.ProximityM)
                return NearbyAhead;
            if (g < 0 && t.Rate.HasValue && t.Rate.Value >= MinClosingMs)
            {
                double eta = -g / t.Rate.Value;
                if (eta <= _cfg.EtaWarnSec && t.Faster)
                    return Approaching;
            }
            return Far;
        }

        // -- 발화 -------------------------------------------------------------

        private void Emit(List<KeyValuePair<CarTrack, string>> transitions,
                          double now, EventBus bus)
        {
            var speak = new List<KeyValuePair<CarTrack, string>>();
            foreach (KeyValuePair<CarTrack, string> pair in transitions)
            {
                if (WorthAnnouncing(pair.Key, pair.Value, now))
                    speak.Add(pair);
            }
            if (speak.Count == 0)
                return;

            var active = new List<CarTrack>();
            foreach (CarTrack t in OrderedTracks())
            {
                if (ThreatRank(t.State) >= 1)
                    active.Add(t);
            }

            if (active.Count >= 2)
            {
                EmitMulti(active, now, bus);
                return;
            }
            // 파이썬 max(): 첫 최대값 선택
            KeyValuePair<CarTrack, string> best = speak[0];
            for (int i = 1; i < speak.Count; i++)
            {
                if (ThreatRank(speak[i].Key.State) > ThreatRank(best.Key.State))
                    best = speak[i];
            }
            EmitSingle(best.Key, best.Value, now, bus);
        }

        private static bool WorthAnnouncing(CarTrack t, string old, double now)
        {
            double last;
            if (t.Announced.TryGetValue(t.State, out last)
                && now - last < StateReannounceSec)
            {
                return false;
            }
            if (t.State == Alongside)
                return true;
            if (t.State == NearbyBehind)
                return old == Far || old == Approaching;    // 뒤에서 붙은 경우만
            if (t.State == Approaching)
                return old == Far;
            if (t.State == NearbyAhead)
            {
                if (old == Alongside && t.Engaged)          // 추월 완료 서사 마무리
                    return true;
                return t.Backmarker && old == Far;          // 전방 백마커 예고
            }
            if (t.State == Far)
            {
                // 배틀하던 차가 떨어짐 → 서사가 있던 경우만 마무리 멘트
                return (old == NearbyBehind || old == Alongside) && t.Engaged && !t.Faster;
            }
            return false;
        }

        private static void Mark(CarTrack t, double now)
        {
            t.Announced[t.State] = now;
            t.Engaged = true;
        }

        /// <summary>브리지(LLM 후속)용 관계 설명.</summary>
        private static string RelContext(CarTrack t)
        {
            if (t.Lapping)
                return "랩을 앞선 동클래스 리더가 랩 돌리러 온 상황 (블루 플래그, 양보 대상)";
            if (t.Faster)
                return "랩핑하러 온 상위 클래스 (무리해서 막을 필요 없음)";
            if (t.Backmarker)
                return "내가 랩 돌리는 백마커 (배틀 아님, 안전하게 추월)";
            return "동클래스 같은 랩 포지션 배틀 상황";
        }

        private void EmitSingle(CarTrack t, string old, double now, EventBus bus)
        {
            Mark(t, now);
            string tone = (t.State == Alongside || InBattle()) ? "urgent" : "casual";
            int cid = t.Cid;

            if (t.State == Alongside)
            {
                string pool = t.Side == "left" ? "alongside_left"
                    : t.Side == "right" ? "alongside_right" : "alongside";
                var ev = new RadioEvent
                {
                    Type = EventTypes.TrafficClose,
                    Priority = Priority.Critical,
                    DedupKey = "along_" + cid,
                    Ttl = 3.0,
                    Tone = "urgent",
                    BridgeTopic = t.Cls + " 차량(" + t.Driver + ")이 지금 옆에 나란히 있다. "
                                  + RelContext(t),
                    ValidFn = () =>
                    {
                        string s = CarState(cid);
                        return s == Alongside || s == NearbyBehind;
                    },
                };
                ev.Data["pool"] = pool;
                ev.Data["cls"] = t.Cls;
                ev.Data["driver"] = t.Driver;
                bus.Push(ev);
            }
            else if (t.State == NearbyBehind)
            {
                var ev = new RadioEvent
                {
                    Type = EventTypes.TrafficClose,
                    Priority = Priority.Critical,
                    DedupKey = "near_" + cid,
                    Ttl = 4.0,
                    Tone = tone,
                    BridgeTopic = t.Cls + " 차량이 뒤 50m 안에 붙었다. " + RelContext(t),
                    ValidFn = () =>
                    {
                        string s = CarState(cid);
                        return s == NearbyBehind || s == Alongside;
                    },
                };
                ev.Data["pool"] = "nearby_behind";
                ev.Data["cls"] = t.Cls;
                ev.Data["driver"] = t.Driver;
                bus.Push(ev);
            }
            else if (t.State == Approaching)
            {
                int eta = t.Rate.HasValue && t.Rate.Value != 0
                    ? Math.Max((int)Math.Round(-t.GapM / t.Rate.Value,
                                               MidpointRounding.ToEven), 1)
                    : 4;
                var ev = new RadioEvent
                {
                    Type = EventTypes.TrafficApproach,
                    Priority = Priority.Critical,
                    DedupKey = "appr_" + cid,
                    Ttl = 5.0,
                    Tone = tone,
                };
                ev.Data["cls"] = t.Cls;
                ev.Data["gap_sec"] = Math.Min(eta, 6);
                bus.Push(ev);
            }
            else if (t.State == NearbyAhead)
            {
                if (old == Alongside)
                {
                    var ev = new RadioEvent
                    {
                        Type = EventTypes.TrafficUpdate,
                        Priority = Priority.High,
                        DedupKey = "passed_" + cid,
                        Ttl = 6.0,
                        Tone = "casual",
                        ValidFn = () => CarState(cid) == NearbyAhead,
                    };
                    ev.Data["pool"] = "pass_complete";
                    ev.Data["cls"] = t.Cls;
                    bus.Push(ev);
                }
                else
                {
                    var ev = new RadioEvent
                    {
                        Type = EventTypes.TrafficUpdate,
                        Priority = Priority.High,
                        DedupKey = "bm_" + cid,
                        Ttl = 8.0,
                        Tone = tone,
                        BridgeTopic = "전방에 백마커(" + t.Cls + ", 랩 차이 "
                                      + Math.Abs(t.LapDelta).ToString("F0")
                                      + "랩)를 잡았다. 무리 없는 추월 조언을 짧게.",
                        ValidFn = () =>
                        {
                            string s = CarState(cid);
                            return s == NearbyAhead || s == Alongside;
                        },
                    };
                    ev.Data["pool"] = "backmarker_ahead";
                    ev.Data["cls"] = t.Cls;
                    bus.Push(ev);
                }
            }
            else if (t.State == Far)
            {
                var ev = new RadioEvent
                {
                    Type = EventTypes.TrafficUpdate,
                    Priority = Priority.Normal,
                    DedupKey = "drop_" + cid,
                    Ttl = 10.0,
                    Tone = "casual",
                    ValidFn = () => CarState(cid) == Far,
                };
                ev.Data["pool"] = "dropped";
                ev.Data["cls"] = t.Cls;
                bus.Push(ev);
            }
        }

        // -- 다중 차량 종합 ------------------------------------------------------

        private void EmitMulti(List<CarTrack> active, double now, EventBus bus)
        {
            foreach (CarTrack t in active)
                Mark(t, now);
            string message = ComposeMulti(active);
            if (message == null)
                return;
            var ev = new RadioEvent
            {
                Type = EventTypes.TrafficMulti,
                Priority = Priority.Critical,
                Message = message,
                DedupKey = "multi",
                Ttl = 4.0,
                Tone = "urgent",
                BridgeTopic = "여러 대가 동시에 얽힌 트래픽 상황: " + message,
                ValidFn = () =>
                {
                    int n = 0;
                    foreach (CarTrack t in _tracks.Values)
                    {
                        if (ThreatRank(t.State) >= 1)
                            n++;
                    }
                    return n >= 2;
                },
            };
            bus.Push(ev);
        }

        private static string ComposeMulti(List<CarTrack> active)
        {
            // 같은 상태의 차량들을 한 절로 합치고, 위협도 순 상위 2개 절만
            var byState = new Dictionary<string, List<CarTrack>>();
            foreach (CarTrack t in active)
            {
                List<CarTrack> list;
                if (!byState.TryGetValue(t.State, out list))
                {
                    list = new List<CarTrack>();
                    byState[t.State] = list;
                }
                list.Add(t);
            }

            var clauses = new List<string>();
            foreach (string st in new[] { Alongside, NearbyBehind, Approaching })
            {
                List<CarTrack> cars;
                if (!byState.TryGetValue(st, out cars) || clauses.Count >= 2)
                    continue;
                if (st == Alongside)
                {
                    string key = cars[0].Side == "left" ? "multi_alongside_left"
                        : cars[0].Side == "right" ? "multi_alongside_right"
                        : "multi_alongside";
                    clauses.Add(Messages.Get(key,
                        "cls", Messages.ClassDisplay(cars[0].Cls)));
                }
                else if (st == NearbyBehind)
                {
                    clauses.Add(Messages.Get("multi_behind", "names", NamesOf(cars)));
                }
                else
                {
                    clauses.Add(Messages.Get("multi_closing", "names", NamesOf(cars)));
                }
            }
            if (clauses.Count == 0)
                return null;
            bool aheadFree = true;
            foreach (CarTrack t in active)
            {
                if (t.State == NearbyAhead)
                {
                    aheadFree = false;
                    break;
                }
            }
            string tail = (aheadFree && clauses.Count >= 2)
                ? Messages.Get("multi_ahead_clear") : "";
            return string.Join(". ", clauses) + "." + tail;
        }

        private static string NamesOf(List<CarTrack> cars)
        {
            // 파이썬 dict 삽입 순서 재현: 키 순서 리스트 + 카운트
            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (CarTrack c in cars)
            {
                string key = Messages.ClassDisplay(c.Cls);
                if (!counts.ContainsKey(key))
                {
                    counts[key] = 0;
                    order.Add(key);
                }
                counts[key]++;
            }
            var parts = new List<string>();
            foreach (string name in order)
            {
                int n = counts[name];
                if (n == 1)
                    parts.Add(Messages.Get("multi_one", "name", name));
                else if (n == 2)
                    parts.Add(Messages.Get("multi_two", "name", name));
                else
                    parts.Add(Messages.Get("multi_n", "name", name, "n", n));
            }
            return string.Join(", ", parts);
        }
    }
}
