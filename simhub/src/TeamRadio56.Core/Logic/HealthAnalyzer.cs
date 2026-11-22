using System;
using System.Collections.Generic;
using System.Globalization;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/health.py 포팅 — 차량 컨디션 (파손/온도).
    ///
    /// 충격 감지 → 8초 뒤 자동 점검 리포트 → 페이스 관찰로 수리 판단.
    /// 상시: 휠 탈락, 슬로우 펑처, 프론트 윙 높이(속도 구간별), 얼라인 오프셋,
    /// 리어 불안정(드라이버 기준선 대비 빈도).
    /// </summary>
    public sealed class HealthAnalyzer
    {
        internal static readonly string[] DentZones =
        {
            "front", "front right", "right", "rear right",
            "rear", "rear left", "left", "front left",
        };
        private static readonly string[] WheelNames =
        {
            "front left", "front right", "rear left", "rear right",
        };

        private const double RepairPaceDelta = 0.5;
        private const double NoEffectDelta = 0.2;
        private const int ObserveLaps = 2;
        private const double ReportDelaySec = 8.0;
        private const double ImpactPressureDropKpa = 12.0;
        private const double SlowPunctureDropKpa = 25.0;

        // 프론트 윙 — 제동이 아닌 고속 샘플의 높이를 속도 구간별로 충격 전후 비교.
        // 근거와 규칙은 analyzers/health.py WING_* 주석.
        private const double WingSampleSpeedKmh = 150.0;
        private const double WingBinKmh = 10.0;          // 속도 구간 폭
        private const double WingDropM = 0.010;
        private const double WingDecelMax = 4.9;         // m/s² (0.5g) — 직전 두 틱 구간 감속 상한
        private const double WingTickGapMax = 0.5;       // 초 — 이보다 긴 틱 간격은 감속 아님
        private const int WingBaseSamples = 301;         // 충격 전 기준 버퍼 (~1랩)
        private const int WingPostSamples = 301;         // 충격 후 판정 버퍼 상한
        private const int WingBinMin = 5;                // 구간 하나에 필요한 샘플 (기준·충격 후 각각)
        private const int WingMinBins = 3;               // 판정에 필요한 맞춘 구간 수

        // 얼라인 — 곧게 가는(요레이트·횡속도 작은) 고속 샘플의 조향 중앙값을 충격 전후 비교.
        // 근거와 규칙은 analyzers/health.py STEER_* 주석.
        private const double SteerSampleSpeedKmh = 150.0;
        private const double SteerSampleMax = 0.20;      // 센서 튐 상한
        private const double SteerLatAccMax = 1.5;       // m/s² — |요레이트|×속도
        private const double SteerLatVelMax = 3.0;       // m/s — |횡속도| (슬라이드만 뺀다, 리어 토 게걸음은 포함)
        private const double SteerShiftMin = 0.03;
        private const double SteerShiftSevere = 0.08;
        private const int SteerBaseSamples = 101;        // 충격 전 기준 버퍼 (홀수)
        private const int SteerBaseMin = 25;             // 기준 최소 샘플 (직선 5초)
        private const int SteerJudgeSamples = 51;        // 충격 후 판정 버퍼 (홀수) — 찼을 때 판정

        private const double InstabWatchSec = 180.0;
        private const double InstabSpeedKmh = 80.0;
        private const double InstabYawRadS = 0.55;
        private const double InstabLatSlipMs = 4.0;
        private const int InstabMinCount = 3;
        private const double InstabGapSec = 3.0;

        private readonly double _waterWarn;
        private readonly double _oilWarn;
        private readonly double _brakeWarn;
        private readonly double _impactMag;

        private double _lastImpactEt;
        private byte[] _dents;
        private int? _impactLap;
        private double? _preImpactPace;
        private bool _detachedWarned;
        private double? _reportDue;
        private double[] _prePressures;
        private readonly HashSet<int> _wheelDetachedWarned = new HashSet<int>();
        private readonly double[] _pressureMax = new double[4];
        private readonly HashSet<int> _punctureWarned = new HashSet<int>();
        private bool _wasInPitlane;
        private double? _wingPrevT;               // 직전 틱 (시각, 속도) — 감속 계산
        private double _wingPrevV;
        private bool _wingPrevCalm;               // 직전 틱 구간이 제동이 아니었나
        private readonly List<KeyValuePair<int, double>> _wingPre = new List<KeyValuePair<int, double>>();
        private Dictionary<int, List<double>> _wingBase;   // 굳힌 기준 (구간 → 높이 목록)
        private int _wingBaseN;
        private List<KeyValuePair<int, double>> _wingPost; // 충격 후 판정 버퍼 (null = 판정 대기 없음)
        private double _wingImpactMag;
        private double? _wingPending;             // 판정 끝난 콜의 하락(m) — 쿨다운 재시도
        private bool _wingTyreLogged;
        private bool _wingWarned;
        private readonly List<double> _steerPre = new List<double>();   // 기준 전 직선 조향
        private double? _preImpactSteer;          // 기준 중앙값
        private int _steerBaseN;
        private List<double> _steerPost;          // 충격 후 판정 버퍼 (null = 판정 대기 없음)
        private double _alignImpactMag;
        private string _alignPending;             // 판정 끝난 콜 ("mild"/"severe") — 쿨다운 재시도
        private bool _alignTyreLogged;
        private bool _alignWarned;
        private string _lastImpactZone;
        private double _instabUntil;
        private double _instabArmedT;
        private int _instabRequired = InstabMinCount;
        private double _instabLastT;
        private bool _instabCalled;
        private readonly List<double> _slides = new List<double>();

        public HealthAnalyzer(double waterWarn = 105.0, double oilWarn = 115.0,
                              double brakeWarn = 700.0, double impactMag = 500.0)
        {
            _waterWarn = waterWarn;
            _oilWarn = oilWarn;
            _brakeWarn = brakeWarn;
            _impactMag = impactMag;
            Reset();
        }

        public void Reset()
        {
            _lastImpactEt = 0.0;
            _dents = null;
            _impactLap = null;
            _preImpactPace = null;
            _detachedWarned = false;
            _reportDue = null;
            _prePressures = null;
            _wheelDetachedWarned.Clear();
            for (int i = 0; i < 4; i++)
                _pressureMax[i] = 0.0;
            _punctureWarned.Clear();
            _wasInPitlane = false;
            _wingPrevT = null;
            _wingPrevV = 0.0;
            _wingPrevCalm = false;
            ResetWing();
            ResetAlignment();
            _lastImpactZone = null;
            _instabUntil = 0.0;
            _instabArmedT = 0.0;
            _instabRequired = InstabMinCount;
            _instabLastT = 0.0;
            _instabCalled = false;
            _slides.Clear();
        }

        /// <summary>파이썬 f-string과 같은 float 표기 ("10.0", "152.375").</summary>
        private static string PyFloat(double d)
        {
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                return ((long)d).ToString(CultureInfo.InvariantCulture) + ".0";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        // -- 5Hz 틱 -----------------------------------------------------------

        public void OnTick(RaceState state, Snapshot snap, EventBus bus)
        {
            PlayerInfo p = snap.Player;
            if (p == null)
                return;
            // 체커(또는 DNF) 뒤엔 휠 탈락 콜만 — 쿨다운 랩에서도 차를 잃을 수 있는 안전 문제다
            bool over = state.RaceOverNow(snap.PlayerScoring());

            WheelInfo[] wheels = p.Wheels ?? new WheelInfo[0];

            // 피트레인 진입 = 타이어 교체/수리 가능성 → 기준 리셋
            if (p.InPitLane)
            {
                if (!_wasInPitlane)
                {
                    for (int i = 0; i < 4; i++)
                        _pressureMax[i] = 0.0;
                    _punctureWarned.Clear();
                    _wheelDetachedWarned.Clear();
                    ResetWing();
                    ResetAlignment();
                    _instabUntil = 0.0;
                    _instabRequired = InstabMinCount;
                    _instabCalled = false;
                }
                _wasInPitlane = true;
            }
            else
            {
                _wasInPitlane = false;
            }

            if (!over)
            {
                TrackWing(p, snap.T, state, bus);
                TrackAlignment(p, state, bus);
            }

            double impactEt = p.LastImpactEt;
            if (impactEt != 0.0 && impactEt > _lastImpactEt + 0.5)
            {
                byte[] prevDents = _dents;
                _dents = p.DentSeverity != null ? (byte[])p.DentSeverity.Clone() : new byte[0];
                _lastImpactEt = impactEt;
                double mag = p.LastImpactMag;
                if (mag >= _impactMag && !over)
                {
                    int nw = Math.Min(wheels.Length, 4);
                    _prePressures = new double[nw];
                    for (int i = 0; i < nw; i++)
                        _prePressures[i] = wheels[i].Pressure;
                    WingOnImpact(mag);
                    AlignOnImpact(mag);
                    _reportDue = snap.T + ReportDelaySec;
                    OnImpact(mag, prevDents, snap.T, state, bus);
                }
            }

            if (over)
                _reportDue = null;           // 피니시 전 충격의 점검 리포트도 접는다
            // 충격 후 자동 점검 리포트 — 드라이버가 아니라 우리가 데이터로 확인
            if (_reportDue.HasValue && snap.T >= _reportDue.Value)
            {
                _reportDue = null;
                DamageReport(p, state, bus);
            }

            if (p.Detached && !_detachedWarned && over)
                _detachedWarned = true;      // 체커 뒤 — 차고로 들어가는 중이라 말하지 않는다
            // 차체 부품 탈락 — 직전 충격이 리어 쪽이면 리어 윙 가능성 특정
            if (p.Detached && !_detachedWarned)
            {
                _detachedWarned = true;
                string msg;
                if (_lastImpactZone != null && _lastImpactZone.Contains("rear"))
                {
                    msg = "Bodywork gone at the rear. Could be the wing. "
                          + "Careful next corner. If the rear goes, box.";
                    state.SetIssue("damage", "리어 부품 탈락 — 리어 윙 손상 가능성");
                }
                else
                {
                    msg = "Bodywork detached. Possible aero loss. Checking data.";
                    state.SetIssue("damage", "차체 부품 탈락 — 에어로 손상 의심");
                }
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.PartDetached,
                    Priority = Priority.Critical,
                    Message = msg,
                    DedupKey = "detached",
                    Tone = "urgent",
                    Ttl = 10.0,
                });
                ArmInstab(snap.T);
                if (!_reportDue.HasValue)
                    _reportDue = snap.T + ReportDelaySec;
            }

            // 휠 탈락 즉시 콜 (주행 불능급)
            for (int i = 0; i < Math.Min(wheels.Length, 4); i++)
            {
                if (wheels[i].Detached && !_wheelDetachedWarned.Contains(i))
                {
                    _wheelDetachedWarned.Add(i);
                    bus.Push(new RadioEvent
                    {
                        Type = EventTypes.WheelDamage,
                        Priority = Priority.Critical,
                        Message = Messages.Get("wheel_gone",
                            "wheel", Messages.WheelName(i)),
                        DedupKey = "wheel_det_" + i,
                        Tone = "urgent",
                        Ttl = 10.0,
                    });
                    state.SetIssue("damage", WheelNames[i] + " 휠 탈락 — 즉시 피트 필요");
                }
            }

            if (!over)
            {
                CheckSlowPuncture(p, wheels, state, bus);
                CheckInstability(p, snap.T, state, bus);
            }
        }

        private void OnImpact(double mag, byte[] prevDents, double now,
                              RaceState state, EventBus bus)
        {
            string zone = NewDentZone(prevDents, _dents);
            bool heavy = mag >= _impactMag * 4;
            // 파이썬은 존 이름을 한국어 조합으로 썼지만("리어 쪽"), 존 이름
            // 자체는 영문 유지 — 여기서는 판정에만 쓰므로 영문으로 통일한다.
            string where = zone != null ? zone + " 쪽" : "위치 불명";
            _lastImpactZone = zone;
            if (heavy || (zone != null && zone.Contains("rear")))
                ArmInstab(now);
            var ev = new RadioEvent
            {
                Type = EventTypes.Damage,
                Priority = Priority.Critical,
                DedupKey = "impact",
                Tone = "urgent",
                Ttl = 8.0,
                BridgeTopic = "방금 " + (heavy ? "큰 " : "") + "충격이 있었다 (" + where + "). "
                              + "우리가 지금 데이터(휠/공기압/보디)를 점검 중이고 곧 결과를 "
                              + "부를 예정. 드라이버는 페이스만 유지하면 된다고 안심시켜라. "
                              + "드라이버에게 확인을 시키지 마라.",
            };
            ev.Data["pool"] = "damage";
            ev.Data["zone"] = zone ?? "";
            ev.Data["mag"] = (int)Math.Round(mag, MidpointRounding.ToEven);
            bus.Push(ev);
            state.SetIssue("damage", "접촉 데미지 (" + where + ") — 페이스 영향 관찰 중");
            state.AddNarrative("(이벤트) 충격 감지 (" + where + ", 크기 "
                               + mag.ToString("F0", CultureInfo.InvariantCulture) + ")");
            _impactLap = state.Laps.Count;
            _preImpactPace = state.BaselineLapTime();
        }

        /// <summary>
        /// 충격 뒤 점검 리포트 문장 — 바퀴별 플래그(4)와 덴트 심각도(8)만 보는 순수 함수
        /// (공기압 비교 같은 상태는 호출자가 losing으로). health.damage_report_message 미러.
        /// </summary>
        public static string DamageReportMessage(bool[] detached, bool[] flat, bool[] losing,
                                                 byte[] dents, out bool needBox,
                                                 out List<string> problems)
        {
            problems = new List<string>();
            var det = new List<string>();
            var flats = new List<string>();
            for (int i = 0; i < Math.Min(detached.Length, 4); i++)
            {
                if (detached[i])
                    det.Add(Messages.WheelName(i));
                else if (i < flat.Length && flat[i])
                    flats.Add(Messages.WheelName(i));
            }
            if (det.Count > 0)
                problems.Add(Messages.Get("item_wheel_damage", "names", Messages.AndList(det)));
            if (flats.Count > 0)
                problems.Add(Messages.Get("item_puncture", "names", Messages.AndList(flats)));
            for (int i = 0; i < Math.Min(losing.Length, 4); i++)
            {
                if (losing[i])
                    problems.Add(Messages.Get("item_losing_pressure", "name", Messages.WheelName(i)));
            }
            var heavyZones = new List<string>();
            bool light = false;
            for (int i = 0; i < Math.Min(dents.Length, 8); i++)
            {
                if (dents[i] >= 2)
                    heavyZones.Add(Messages.ZoneDisplay(DentZones[i]));
                if (dents[i] == 1)
                    light = true;
            }
            if (heavyZones.Count > SpeechDomain.DentMaxZones)
                problems.Add(Messages.Get("item_heavy_body_all"));   // 존 조합은 3개까지만 팩에 있다
            else if (heavyZones.Count > 0)
                problems.Add(Messages.Get("item_heavy_body", "zones", Messages.AndList(heavyZones)));

            needBox = det.Count > 0 || flats.Count > 0;
            if (problems.Count > 0)
            {
                string advice = Messages.Get(needBox ? "report_advice_box" : "report_advice_keep");
                return Messages.Get("report_problems",
                    "problems", Messages.JoinProblems(problems), "advice", advice);
            }
            return Messages.Get(light ? "report_marks" : "report_clean");
        }

        /// <summary>프론트 윙 하락 콜 — 100mm 넘는 하락은 센서 튐이라 null.</summary>
        public static string WingMessage(double dropM)
        {
            if (!SpeechDomain.WingOk(dropM))
                return null;
            return Messages.Get("wing_down", "mm", dropM * 1000);
        }

        /// <summary>브레이크 평균 온도 콜 — 10도 단위 반올림, 1500도 넘으면 null.</summary>
        public static string BrakeMessage(double avg)
        {
            // 10도 단위 반올림 — 파이썬 int(math.floor(x / 10.0 + 0.5)) * 10과 동일
            int t = (int)Math.Floor(avg / 10.0 + 0.5) * 10;
            if (!SpeechDomain.BrakeOk(t))
                return null;
            return Messages.Get("brake_warn", "t", t);
        }

        /// <summary>데미지 페이스 손실 → 수리 권고 — 랩당 10초 이상이면 숫자 없이.</summary>
        public static string RepairMessage(double delta)
        {
            if (SpeechDomain.RepairBig(delta))
                return Messages.Get("repair_cost_big");
            return Messages.Get("repair_cost", "delta", delta);
        }

        private void DamageReport(PlayerInfo p, RaceState state, EventBus bus)
        {
            WheelInfo[] wheels = p.Wheels ?? new WheelInfo[0];
            int nw = Math.Min(wheels.Length, 4);

            var detached = new bool[nw];
            var flat = new bool[nw];
            var losing = new bool[nw];
            for (int i = 0; i < nw; i++)
            {
                detached[i] = wheels[i].Detached;
                flat[i] = wheels[i].Flat;
            }

            // 충격 전후 공기압 비교 — 서서히 새는 누출 조기 발견
            if (_prePressures != null)
            {
                for (int i = 0; i < nw; i++)
                {
                    if (_punctureWarned.Contains(i) || wheels[i].Flat || wheels[i].Detached)
                        continue;
                    if (i < _prePressures.Length && _prePressures[i] > 0
                        && _prePressures[i] - wheels[i].Pressure >= ImpactPressureDropKpa)
                    {
                        _punctureWarned.Add(i);
                        losing[i] = true;
                    }
                }
            }
            _prePressures = null;

            bool needBox;
            List<string> problems;
            string message = DamageReportMessage(detached, flat, losing,
                p.DentSeverity ?? new byte[0], out needBox, out problems);
            if (problems.Count > 0)
                state.SetIssue("damage", "점검 결과: " + string.Join(", ", problems));
            bus.Push(new RadioEvent
            {
                Type = EventTypes.DamageReport,
                Priority = Priority.High,
                Message = message,
                DedupKey = "dmg_report_" + PyFloat(_lastImpactEt),
                Ttl = 20.0,
                Tone = "casual",
            });
            state.AddNarrative("(점검) " + message);
        }

        /// <summary>정렬한 목록의 (Count−1)/2 번째 (짝수면 아래쪽 가운데) — 파이썬 low_median.</summary>
        internal static double LowMedian(List<double> samples)
        {
            var sorted = new List<double>(samples);
            sorted.Sort();
            return sorted[(sorted.Count - 1) / 2];
        }

        /// <summary>윙 높이 속도 구간 — floor((v − 150) / 10). 파이썬 wing_bin.</summary>
        internal static int WingBin(double speedKmh)
        {
            return (int)Math.Floor((speedKmh - WingSampleSpeedKmh) / WingBinKmh);
        }

        private static Dictionary<int, List<double>> WingBins(List<KeyValuePair<int, double>> samples)
        {
            var outp = new Dictionary<int, List<double>>();
            foreach (var kv in samples)
            {
                List<double> hs;
                if (!outp.TryGetValue(kv.Key, out hs))
                {
                    hs = new List<double>();
                    outp[kv.Key] = hs;
                }
                hs.Add(kv.Value);
            }
            return outp;
        }

        private static int WingFullBins(Dictionary<int, List<double>> bins)
        {
            int n = 0;
            foreach (var kv in bins)
            {
                if (kv.Value.Count >= WingBinMin)
                    n++;
            }
            return n;
        }

        /// <summary>
        /// 기준(구간별 높이) ↔ 충격 후 (구간, 높이) 목록 — 양쪽 다 WingBinMin개 이상인 구간이
        /// WingMinBins개 이상이면 하락(m)과 맞춘 구간(오름차순), 아니면 null. 파이썬 wing_compare.
        /// </summary>
        internal static double? WingCompare(Dictionary<int, List<double>> baseBins,
                                            List<KeyValuePair<int, double>> post,
                                            out List<int> matched)
        {
            Dictionary<int, List<double>> byPost = WingBins(post);
            matched = new List<int>();
            foreach (var kv in byPost)
            {
                List<double> pre;
                if (kv.Value.Count >= WingBinMin && baseBins.TryGetValue(kv.Key, out pre)
                    && pre.Count >= WingBinMin)
                {
                    matched.Add(kv.Key);
                }
            }
            if (matched.Count < WingMinBins)
                return null;
            matched.Sort();
            var drops = new List<double>();
            foreach (int b in matched)
                drops.Add(LowMedian(baseBins[b]) - LowMedian(byPost[b]));
            return LowMedian(drops);
        }

        private void ResetWing()
        {
            _wingPre.Clear();
            _wingBase = null;
            _wingBaseN = 0;
            _wingPost = null;
            _wingImpactMag = 0.0;
            _wingPending = null;
            _wingTyreLogged = false;
            _wingWarned = false;
        }

        /// <summary>
        /// 기준 임계 이상 충격 — 기준이 아직 없으면 굳히고, 판정 버퍼를 새로 연다.
        /// 기준이 모자라면(5샘플 이상 구간 3개 미만) 이 충격만 판정하지 않고 기준 버퍼를 이 충격
        /// 뒤부터 새로 모은다 (다음 충격이 기준을 굳힐 수 있게 — 스틴트 전체를 끄지 않는다).
        /// </summary>
        private void WingOnImpact(double mag)
        {
            if (_wingBase == null)
            {
                Dictionary<int, List<double>> bins = WingBins(_wingPre);
                int full = WingFullBins(bins);
                if (full < WingMinBins)
                {
                    FileLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "윙 판정 (충격 {0:F0}): 기준 없음 — 충격 전 고속 샘플 {1}개, {2}샘플 이상 "
                        + "구간 {3}개 < {4} → no-baseline (이 충격만, 기준 다시 모음)",
                        mag, _wingPre.Count, WingBinMin, full, WingMinBins));
                    _wingPre.Clear();
                    return;
                }
                _wingBase = bins;                    // 다중 충격 시 최초 기준 유지
                _wingBaseN = _wingPre.Count;
            }
            if (_wingWarned || _wingPending.HasValue)
                return;
            _wingPost = new List<KeyValuePair<int, double>>();   // 충격마다 새 버퍼
            _wingImpactMag = mag;
            _wingTyreLogged = false;
        }

        /// <summary>
        /// 직전 두 틱 구간 모두 감속 ≤ 0.5g(틱 간격 0~0.5초)인가 — 제동 다이브와 그 복원을 뺀다.
        /// 매 틱(체커 전) 갱신한다. 파이썬 _wing_calm.
        /// </summary>
        private bool WingCalm(double now, double speed)
        {
            bool calm = false;
            if (_wingPrevT.HasValue)
            {
                double dt = now - _wingPrevT.Value;
                if (dt > 0.0 && dt <= WingTickGapMax)
                    calm = (_wingPrevV - speed) / 3.6 / dt <= WingDecelMax;
            }
            bool steady = calm && _wingPrevCalm;
            _wingPrevT = now;
            _wingPrevV = speed;
            _wingPrevCalm = calm;
            return steady;
        }

        private static string SignedMm(double m)
        {
            double mm = m * 1000;
            return (mm >= 0 ? "+" : "") + mm.ToString("F1", CultureInfo.InvariantCulture);
        }

        private void TrackWing(PlayerInfo p, double now, RaceState state, EventBus bus)
        {
            double speed = p.SpeedKmh;
            bool steady = WingCalm(now, speed);
            if (_wingPending.HasValue)
            {
                // push가 쿨다운으로 거절되면 다음 틱에 재시도 (점검 리포트 직후 등)
                PushWing(_wingPending.Value, state, bus);
                return;
            }
            double h = p.FrontWingHeight;
            if (double.IsNaN(h) || double.IsInfinity(h) || h <= 0 || p.InPitLane
                || speed < WingSampleSpeedKmh || !steady)
            {
                return;
            }
            var sample = new KeyValuePair<int, double>(WingBin(speed), h);
            if (_wingBase == null)
            {
                _wingPre.Add(sample);                // 기준 전 — 건강한 기준 샘플
                if (_wingPre.Count > WingBaseSamples)
                    _wingPre.RemoveAt(0);
                return;
            }
            if (_wingPost == null || _wingWarned)
                return;
            _wingPost.Add(sample);
            if (_wingPost.Count > WingPostSamples)
                _wingPost.RemoveAt(0);
            if (_reportDue.HasValue)
                return;    // 그 충격의 점검 리포트가 먼저 (같은 쿨다운 — 윙 콜이 리포트를 밀어내지 않게)
            List<int> matched;
            double? res = WingCompare(_wingBase, _wingPost, out matched);
            if (!res.HasValue)
                return;
            double drop = res.Value;
            var names = new List<string>();
            foreach (int b in matched)
                names.Add(((int)(WingSampleSpeedKmh + b * WingBinKmh)).ToString(CultureInfo.InvariantCulture));
            string bins = string.Join(",", names);
            bool tyre = _punctureWarned.Count > 0;
            WheelInfo[] wheels = p.Wheels ?? new WheelInfo[0];
            for (int i = 0; i < Math.Min(wheels.Length, 4); i++)
            {
                if (wheels[i].Flat || wheels[i].Detached)
                    tyre = true;
            }
            if (tyre)
            {
                // 높이 하락은 타이어 탓 — 슬로우 펑처/점검 리포트가 부른다. 샘플은 계속 모은다.
                if (!_wingTyreLogged)
                {
                    _wingTyreLogged = true;
                    FileLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "윙 판정 (충격 {0:F0}): 맞춘 속도 구간 {1}개 [{2}]km/h · 기준 {3}샘플 · "
                        + "충격 후 {4}샘플 · 하락 {5}mm → hold-tyre (펑크/휠/공기압 누출)",
                        _wingImpactMag, matched.Count, bins, _wingBaseN, _wingPost.Count, SignedMm(drop)));
                }
                return;
            }
            bool call = drop >= WingDropM;
            FileLog.Info(string.Format(CultureInfo.InvariantCulture,
                "윙 판정 (충격 {0:F0}): 맞춘 속도 구간 {1}개 [{2}]km/h · 기준 {3}샘플 · "
                + "충격 후 {4}샘플 · 하락 {5}mm → {6}",
                _wingImpactMag, matched.Count, bins, _wingBaseN, _wingPost.Count, SignedMm(drop),
                call ? "call" : "ok"));
            _wingPost = null;                        // 이 충격의 판정은 끝 (다음 충격이 다시 연다)
            if (!call)
                return;
            _wingPending = drop;
            PushWing(drop, state, bus);
        }

        /// <summary>판정된 윙 콜 — 쿨다운으로 거절되면 대기로 남아 다음 틱에 다시 민다.</summary>
        private void PushWing(double drop, RaceState state, EventBus bus)
        {
            string text = WingMessage(drop);
            // 100mm 넘는 하락은 말하지 않지만 이슈·서사는 남기고 한 번만 판정한다
            if (text != null)
            {
                bool accepted = bus.Push(new RadioEvent
                {
                    Type = EventTypes.DamageReport,
                    Priority = Priority.High,
                    Message = text,
                    DedupKey = "wing_damage",
                    Ttl = 20.0,
                    Tone = "casual",
                });
                if (!accepted)
                    return;
            }
            _wingWarned = true;
            _wingPending = null;
            state.SetIssue("damage", "프론트 윙 손상 (높이 -"
                + (drop * 1000).ToString("F0", CultureInfo.InvariantCulture) + "mm)");
            state.AddNarrative("(점검) 프론트 윙 손상 감지");
        }

        /// <summary>정렬한 목록의 Count/2 번째 — 파이썬 steer_median과 같은 정의.</summary>
        internal static double SteerMedian(List<double> samples)
        {
            var sorted = new List<double>(samples);
            sorted.Sort();
            return sorted[sorted.Count / 2];
        }

        private void ResetAlignment()
        {
            _steerPre.Clear();
            _preImpactSteer = null;
            _steerBaseN = 0;
            _steerPost = null;
            _alignImpactMag = 0.0;
            _alignPending = null;
            _alignTyreLogged = false;
            _alignWarned = false;
        }

        private static string Signed(double d)
        {
            return (d >= 0 ? "+" : "") + d.ToString("F3", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 기준 임계 이상 충격 — 기준이 아직 없으면 굳히고, 판정 버퍼를 새로 연다.
        /// 기준 샘플이 모자라면 이 충격만 판정하지 않고 기준 버퍼를 이 충격 뒤부터 새로
        /// 모은다 (다음 충격이 기준을 굳힐 수 있게 — 스틴트 전체를 끄지 않는다).
        /// </summary>
        private void AlignOnImpact(double mag)
        {
            if (!_preImpactSteer.HasValue)
            {
                if (_steerPre.Count < SteerBaseMin)
                {
                    FileLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "얼라인 판정 (충격 {0:F0}): 기준 없음 — 충격 전 직선 샘플 {1}개 < {2} → "
                        + "no-baseline (이 충격만, 기준 다시 모음)",
                        mag, _steerPre.Count, SteerBaseMin));
                    _steerPre.Clear();
                    return;
                }
                _preImpactSteer = SteerMedian(_steerPre);  // 다중 충격 시 최초 기준 유지
                _steerBaseN = _steerPre.Count;
            }
            if (_alignWarned || _alignPending != null)
                return;
            _steerPost = new List<double>();     // 충격마다 새 버퍼 — 마지막 충격 뒤 상태로 판정
            _alignImpactMag = mag;
            _alignTyreLogged = false;
        }

        private void TrackAlignment(PlayerInfo p, RaceState state, EventBus bus)
        {
            if (_alignPending != null)
            {
                // push가 쿨다운으로 거절되면 다음 틱에 재시도 (점검 리포트 직후 등)
                PushAlignment(_alignPending, state, bus);
                return;
            }
            // 요레이트/횡속도/조향이 없는 리플레이(파이썬 None)는 샘플이 아니다
            if (!p.HasSteerData || p.InPitLane || p.SpeedKmh < SteerSampleSpeedKmh)
                return;
            double steer = p.Steering;
            double yaw = p.YawRate;
            double latVel = p.LatVel;
            if (double.IsNaN(steer) || double.IsInfinity(steer)
                || double.IsNaN(yaw) || double.IsInfinity(yaw)
                || double.IsNaN(latVel) || double.IsInfinity(latVel))
            {
                return;
            }
            if (Math.Abs(steer) > SteerSampleMax || Math.Abs(yaw) * (p.SpeedKmh / 3.6) > SteerLatAccMax
                || Math.Abs(latVel) > SteerLatVelMax)
            {
                return;    // 코너·슬라이드 — 곧게 가는 중이 아니다
            }

            if (!_preImpactSteer.HasValue)
            {
                _steerPre.Add(steer);            // 기준 전 — 건강한 기준 샘플
                if (_steerPre.Count > SteerBaseSamples)
                    _steerPre.RemoveAt(0);
                return;
            }
            if (_steerPost == null || _alignWarned)
                return;
            _steerPost.Add(steer);
            if (_steerPost.Count > SteerJudgeSamples)
                _steerPost.RemoveAt(0);
            if (_steerPost.Count < SteerJudgeSamples)
                return;
            bool tyre = _punctureWarned.Count > 0;
            WheelInfo[] wheels = p.Wheels ?? new WheelInfo[0];
            for (int i = 0; i < Math.Min(wheels.Length, 4); i++)
            {
                if (wheels[i].Flat || wheels[i].Detached)
                    tyre = true;
            }
            double post = SteerMedian(_steerPost);
            double shift = post - _preImpactSteer.Value;
            if (tyre)
            {
                // 쏠림은 타이어 탓 — 슬로우 펑처/점검 리포트가 부른다. 샘플은 계속 모은다.
                if (!_alignTyreLogged)
                {
                    _alignTyreLogged = true;
                    FileLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "얼라인 판정 (충격 {0:F0}): 기준 {1} ({2}샘플) · 충격 후 {3} ({4}샘플) · "
                        + "변화 {5} → hold-tyre (펑크/휠/공기압 누출)",
                        _alignImpactMag, Signed(_preImpactSteer.Value), _steerBaseN,
                        Signed(post), _steerPost.Count, Signed(shift)));
                }
                return;
            }
            string decision = Math.Abs(shift) >= SteerShiftSevere ? "severe"
                : Math.Abs(shift) >= SteerShiftMin ? "mild" : null;
            FileLog.Info(string.Format(CultureInfo.InvariantCulture,
                "얼라인 판정 (충격 {0:F0}): 기준 {1} ({2}샘플) · 충격 후 {3} ({4}샘플) · 변화 {5} → {6}",
                _alignImpactMag, Signed(_preImpactSteer.Value), _steerBaseN,
                Signed(post), _steerPost.Count, Signed(shift),
                decision != null ? "call-" + decision : "ok"));
            _steerPost = null;                   // 이 충격의 판정은 끝 (다음 충격이 다시 연다)
            if (decision == null)
                return;
            _alignPending = decision;
            PushAlignment(decision, state, bus);
        }

        /// <summary>판정된 얼라인 콜 — 쿨다운으로 거절되면 대기로 남아 다음 틱에 다시 민다.</summary>
        private void PushAlignment(string decision, RaceState state, EventBus bus)
        {
            if (decision == "severe")
            {
                // 심각 단계: 주행 자체가 힘든 수준 → 페이스 관찰 없이 즉시 박스 권고
                bool ok = bus.Push(new RadioEvent
                {
                    Type = EventTypes.DamageReport,
                    Priority = Priority.Critical,
                    Message = Messages.Get("align_severe"),
                    DedupKey = "align_severe",
                    Tone = "urgent",
                    Ttl = 15.0,
                });
                if (!ok)
                    return;
                state.SetIssue("damage", "얼라인 심각 손상 — 즉시 수리 권장");
                state.AddNarrative("(점검) 얼라인 심각 손상 → 박스 콜");
            }
            else
            {
                bool accepted = bus.Push(new RadioEvent
                {
                    Type = EventTypes.DamageReport,
                    Priority = Priority.High,
                    Message = Messages.Get("align_mild"),
                    DedupKey = "align_damage",
                    Ttl = 20.0,
                    Tone = "casual",
                });
                if (!accepted)
                    return;
                state.SetIssue("damage", "얼라인 틀어짐 의심 (직선 조향 오프셋 변화)");
                state.AddNarrative("(점검) 얼라인 틀어짐 감지 — 직선 조향 오프셋 변화");
            }
            _alignWarned = true;
            _alignPending = null;
        }

        private void CheckInstability(PlayerInfo p, double now,
                                      RaceState state, EventBus bus)
        {
            double yaw = p.YawRate;
            double latVel = p.LatVel;
            if (p.InPitLane || p.SpeedKmh < InstabSpeedKmh)
                return;
            if (Math.Abs(yaw) < InstabYawRadS || Math.Abs(latVel) < InstabLatSlipMs)
                return;
            if (now - _instabLastT < InstabGapSec)
                return;
            _instabLastT = now;
            // 상시 기록 (기준선용). 오래된 것은 정리.
            _slides.Add(now);
            _slides.RemoveAll(t => now - t > 2 * InstabWatchSec);

            if (_instabCalled || now >= _instabUntil)
                return;
            int post = 0;
            foreach (double t in _slides)
            {
                if (t >= _instabArmedT)
                    post++;
            }
            if (post < _instabRequired)
                return;
            _instabCalled = true;
            bus.Push(new RadioEvent
            {
                Type = EventTypes.DamageReport,
                Priority = Priority.Critical,
                Message = Messages.Get("rear_instab"),
                DedupKey = "rear_instab",
                Tone = "urgent",
                Ttl = 15.0,
            });
            state.SetIssue("damage", "리어 불안정 반복 (에어로/서스 손상 의심)");
            state.AddNarrative("(점검) 손상 후 리어 불안정 반복 감지 → 박스 권장");
        }

        private void ArmInstab(double now)
        {
            // 직전 3분 슬라이드 횟수를 기준선으로 콜 임계 결정 (×2, 최소 3회).
            // 이미 감시 중이면 창만 연장하고 기준선은 유지.
            bool already = now < _instabUntil;
            _instabUntil = now + InstabWatchSec;
            if (already)
                return;
            int baseline = 0;
            foreach (double t in _slides)
            {
                if (now - InstabWatchSec <= t && t <= now)
                    baseline++;
            }
            _instabArmedT = now;
            _instabRequired = Math.Max(InstabMinCount, baseline * 2);
            _instabCalled = false;
        }

        private void CheckSlowPuncture(PlayerInfo p, WheelInfo[] wheels,
                                       RaceState state, EventBus bus)
        {
            if (p.InPitLane || p.SpeedKmh < 60)
                return;    // 저속/피트에선 온도 하락으로 공기압이 자연히 떨어짐
            for (int i = 0; i < Math.Min(wheels.Length, 4); i++)
            {
                double pr = wheels[i].Pressure;
                if (pr <= 0)
                    continue;
                if (pr > _pressureMax[i])
                {
                    _pressureMax[i] = pr;
                }
                else if (_pressureMax[i] - pr >= SlowPunctureDropKpa
                         && !_punctureWarned.Contains(i) && !wheels[i].Flat)
                {
                    _punctureWarned.Add(i);
                    bus.Push(new RadioEvent
                    {
                        Type = EventTypes.TyreWarning,
                        Priority = Priority.High,
                        Message = Messages.Get("slow_puncture",
                            "wheel", Messages.WheelName(i)),
                        DedupKey = "slowpunc_" + i,
                        Ttl = 20.0,
                        Tone = "casual",
                    });
                    state.SetIssue("tyres", WheelNames[i] + " 슬로우 펑처 의심");
                }
            }
        }

        /// <summary>새로 생기거나 심해진 덴트 존 → 대략적 위치 이름.</summary>
        private static string NewDentZone(byte[] prev, byte[] cur)
        {
            if (cur == null || cur.Length == 0)
                return null;
            int worstI = -1;
            int worstDelta = 0;
            for (int i = 0; i < Math.Min(cur.Length, 8); i++)
            {
                int before = prev != null && i < prev.Length ? prev[i] : 0;
                if (cur[i] - before > worstDelta)
                {
                    worstI = i;
                    worstDelta = cur[i] - before;
                }
            }
            return worstI < 0 ? null : DentZones[worstI];
        }

        // -- 랩 완료 -----------------------------------------------------------

        public void OnLap(RaceState state, Snapshot snap, EventBus bus)
        {
            PlayerInfo p = snap.Player;
            if (p == null || state.RaceOver)
                return;

            CheckDamagePace(state, bus);

            double water = p.WaterTemp;
            double oil = p.OilTemp;
            if (p.Overheating || water >= _waterWarn || oil >= _oilWarn)
            {
                string what = Messages.Get(water >= _waterWarn ? "engine_what_water"
                    : oil >= _oilWarn ? "engine_what_oil" : "engine_what_engine");
                var ev = new RadioEvent
                {
                    Type = EventTypes.EngineWarning,
                    Priority = Priority.High,
                    Message = Messages.Get("engine_warn", "what", what),
                    Ttl = 30.0,
                };
                ev.Data["water"] = water;
                ev.Data["oil"] = oil;
                bus.Push(ev);
                state.SetIssue("engine", "엔진 온도 상승 (수온 "
                    + water.ToString("F0", CultureInfo.InvariantCulture) + ", 유온 "
                    + oil.ToString("F0", CultureInfo.InvariantCulture) + ")");
            }
            else
            {
                state.ClearIssue("engine");
            }

            WheelInfo[] wheels = p.Wheels ?? new WheelInfo[0];
            if (wheels.Length == 4)
            {
                double avgBrake = (wheels[0].BrakeTemp + wheels[1].BrakeTemp
                                   + wheels[2].BrakeTemp + wheels[3].BrakeTemp) / 4;
                string text = avgBrake >= _brakeWarn ? BrakeMessage(avgBrake) : null;
                if (text != null)
                {
                    var ev = new RadioEvent
                    {
                        Type = EventTypes.BrakeWarning,
                        Priority = Priority.Normal,
                        Message = text,
                        Ttl = 30.0,
                    };
                    ev.Data["avg_brake"] = (int)Math.Round(avgBrake, MidpointRounding.ToEven);
                    bus.Push(ev);
                }
            }
        }

        private void CheckDamagePace(RaceState state, EventBus bus)
        {
            if (!state.Issues.ContainsKey("damage") || !_impactLap.HasValue)
                return;
            // 피트 인 = 수리로 간주하고 리셋
            if (state.Laps.Count > 0 && state.Laps[state.Laps.Count - 1].InPits)
            {
                state.ClearIssue("damage");
                _impactLap = null;
                _detachedWarned = false;
                return;
            }
            var post = new List<LapRecord>();
            for (int i = _impactLap.Value; i < state.Laps.Count; i++)
            {
                if (state.Laps[i].Valid)
                    post.Add(state.Laps[i]);
            }
            if (post.Count < ObserveLaps || !_preImpactPace.HasValue)
                return;
            double postAvg = 0;
            for (int i = post.Count - ObserveLaps; i < post.Count; i++)
                postAvg += post[i].LapTime;
            postAvg /= ObserveLaps;
            double delta = postAvg - _preImpactPace.Value;

            if (delta >= RepairPaceDelta)
            {
                string deltaS = delta.ToString("F1", CultureInfo.InvariantCulture);
                var ev = new RadioEvent
                {
                    Type = EventTypes.LapAnalysis,
                    Priority = Priority.Normal,
                    Message = RepairMessage(delta),
                    DedupKey = "repair_" + _impactLap.Value,
                };
                ev.Data["triggers"] = new[]
                {
                    "접촉 데미지 이후 랩당 " + deltaS + "초 느려졌다. "
                    + "피트에서 수리할지, 그냥 달릴지 판단해라 (수리는 시간 손실, "
                    + "방치는 랩마다 손실 누적).",
                };
                bus.Push(ev);
                state.SetIssue("damage", "데미지로 랩당 " + deltaS + "초 손실 — 수리 권장");
                _impactLap = null;    // 판단은 한 번만
            }
            else if (delta <= NoEffectDelta)
            {
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.Damage,
                    Priority = Priority.Normal,
                    Message = Messages.Get("dmg_no_effect"),
                    DedupKey = "dmg_ok_" + _impactLap.Value,
                    Ttl = 30.0,
                });
                state.ClearIssue("damage");
                _impactLap = null;
            }
        }
    }
}
