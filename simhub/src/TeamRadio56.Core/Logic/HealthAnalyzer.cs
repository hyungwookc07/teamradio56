using System;
using System.Collections.Generic;
using System.Globalization;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/health.py 포팅 — 차량 컨디션 (파손/온도).
    ///
    /// 충격 감지 → 8초 뒤 자동 점검 리포트 → 페이스 관찰로 수리 판단.
    /// 상시: 휠 탈락, 슬로우 펑처, 프론트 윙 EMA, 얼라인 오프셋,
    /// 리어 불안정(드라이버 기준선 대비 빈도).
    /// </summary>
    public sealed class HealthAnalyzer
    {
        private static readonly string[] DentZones =
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

        private const double WingSampleSpeedKmh = 150.0;
        private const double WingDropM = 0.010;
        private const double WingEmaAlpha = 0.03;

        private const double SteerSampleSpeedKmh = 150.0;
        private const double SteerSampleMax = 0.20;
        private const double SteerShiftMin = 0.03;
        private const double SteerShiftSevere = 0.08;
        private const double SteerEmaAlpha = 0.02;
        private const int SteerJudgeSamples = 75;

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
        private double? _wingEma;
        private double? _preImpactWing;
        private bool _wingWarned;
        private double? _steerEma;
        private double? _preImpactSteer;
        private int _steerSamples;
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
            _wingEma = null;
            _preImpactWing = null;
            _wingWarned = false;
            _steerEma = null;
            _preImpactSteer = null;
            _steerSamples = 0;
            _alignWarned = false;
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
                    _wingEma = null;
                    _preImpactWing = null;
                    _wingWarned = false;
                    _steerEma = null;
                    _preImpactSteer = null;
                    _steerSamples = 0;
                    _alignWarned = false;
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

            TrackWing(p, state, bus);
            TrackAlignment(p, state, bus);

            double impactEt = p.LastImpactEt;
            if (impactEt != 0.0 && impactEt > _lastImpactEt + 0.5)
            {
                byte[] prevDents = _dents;
                _dents = p.DentSeverity != null ? (byte[])p.DentSeverity.Clone() : new byte[0];
                _lastImpactEt = impactEt;
                double mag = p.LastImpactMag;
                if (mag >= _impactMag)
                {
                    int nw = Math.Min(wheels.Length, 4);
                    _prePressures = new double[nw];
                    for (int i = 0; i < nw; i++)
                        _prePressures[i] = wheels[i].Pressure;
                    if (!_preImpactWing.HasValue)      // 다중 충격 시 최초 기준 유지
                        _preImpactWing = _wingEma;
                    if (!_preImpactSteer.HasValue)
                    {
                        _preImpactSteer = _steerEma;
                        _steerSamples = 0;
                    }
                    _reportDue = snap.T + ReportDelaySec;
                    OnImpact(mag, prevDents, snap.T, state, bus);
                }
            }

            // 충격 후 자동 점검 리포트 — 드라이버가 아니라 우리가 데이터로 확인
            if (_reportDue.HasValue && snap.T >= _reportDue.Value)
            {
                _reportDue = null;
                DamageReport(p, state, bus);
            }

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
                        Message = WheelNames[i]
                                  + " wheel is gone! Careful, bring it to the pits slowly.",
                        DedupKey = "wheel_det_" + i,
                        Tone = "urgent",
                        Ttl = 10.0,
                    });
                    state.SetIssue("damage", WheelNames[i] + " 휠 탈락 — 즉시 피트 필요");
                }
            }

            CheckSlowPuncture(p, wheels, state, bus);
            CheckInstability(p, snap.T, state, bus);
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

        private void DamageReport(PlayerInfo p, RaceState state, EventBus bus)
        {
            WheelInfo[] wheels = p.Wheels ?? new WheelInfo[0];
            int nw = Math.Min(wheels.Length, 4);
            var problems = new List<string>();

            var detached = new List<string>();
            var flats = new List<string>();
            for (int i = 0; i < nw; i++)
            {
                if (wheels[i].Detached)
                    detached.Add(WheelNames[i]);
                else if (wheels[i].Flat)
                    flats.Add(WheelNames[i]);
            }
            if (detached.Count > 0)
                problems.Add(string.Join("/", detached) + " wheel damage");
            if (flats.Count > 0)
                problems.Add(string.Join("/", flats) + " puncture");

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
                        problems.Add(WheelNames[i] + " losing pressure");
                    }
                }
            }
            _prePressures = null;

            byte[] dents = p.DentSeverity ?? new byte[0];
            var heavyZones = new List<string>();
            bool light = false;
            for (int i = 0; i < Math.Min(dents.Length, 8); i++)
            {
                if (dents[i] >= 2)
                    heavyZones.Add(DentZones[i]);
                if (dents[i] == 1)
                    light = true;
            }
            if (heavyZones.Count > 0)
                problems.Add("heavy bodywork damage, " + string.Join("/", heavyZones));

            string message;
            if (problems.Count > 0)
            {
                bool needBox = detached.Count > 0 || flats.Count > 0;
                string advice = needBox
                    ? "Prepare to box."
                    : "Keep pace, I'll make the repair call.";
                message = "Check done. " + string.Join(", ", problems) + ". " + advice;
                state.SetIssue("damage", "점검 결과: " + string.Join(", ", problems));
            }
            else if (light)
            {
                message = "Check done. Just marks. Wheels, tyres, pressures all fine. Carry on.";
            }
            else
            {
                message = "Check done. No damage, car is clean. Carry on.";
            }
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

        private void TrackWing(PlayerInfo p, RaceState state, EventBus bus)
        {
            double h = p.FrontWingHeight;
            if (h <= 0 || p.InPitLane || p.SpeedKmh < WingSampleSpeedKmh)
                return;
            if (!_wingEma.HasValue)
            {
                _wingEma = h;
                return;
            }
            _wingEma = (1 - WingEmaAlpha) * _wingEma.Value + WingEmaAlpha * h;

            if (_wingWarned || !_preImpactWing.HasValue)
                return;
            double drop = _preImpactWing.Value - _wingEma.Value;
            if (drop >= WingDropM)
            {
                // push가 쿨다운으로 거절되면 다음 틱에 재시도
                bool accepted = bus.Push(new RadioEvent
                {
                    Type = EventTypes.DamageReport,
                    Priority = Priority.High,
                    Message = "Front aero down "
                              + (drop * 1000).ToString("F0", CultureInfo.InvariantCulture)
                              + " millimetres. Splitter damage. "
                              + "Careful in the fast stuff. Repair call on pace.",
                    DedupKey = "wing_damage",
                    Ttl = 20.0,
                    Tone = "casual",
                });
                if (!accepted)
                    return;
                _wingWarned = true;
                state.SetIssue("damage", "프론트 윙 손상 (높이 -"
                    + (drop * 1000).ToString("F0", CultureInfo.InvariantCulture) + "mm)");
                state.AddNarrative("(점검) 프론트 윙 손상 감지");
            }
        }

        private void TrackAlignment(PlayerInfo p, RaceState state, EventBus bus)
        {
            double steer = p.Steering;
            if (p.InPitLane || p.SpeedKmh < SteerSampleSpeedKmh
                || Math.Abs(steer) > SteerSampleMax)
            {
                return;
            }
            if (!_steerEma.HasValue)
            {
                _steerEma = steer;
                return;
            }
            _steerEma = (1 - SteerEmaAlpha) * _steerEma.Value + SteerEmaAlpha * steer;

            if (_alignWarned || !_preImpactSteer.HasValue)
                return;
            _steerSamples++;
            if (_steerSamples < SteerJudgeSamples)
                return;    // EMA가 새 상태로 수렴할 때까지 판정 유보
            double shift = _steerEma.Value - _preImpactSteer.Value;
            if (Math.Abs(shift) < SteerShiftMin)
                return;
            if (Math.Abs(shift) >= SteerShiftSevere)
            {
                bool ok = bus.Push(new RadioEvent
                {
                    Type = EventTypes.DamageReport,
                    Priority = Priority.Critical,
                    Message = "Alignment is badly out. You're steering on the straights. "
                              + "Box for repairs.",
                    DedupKey = "align_severe",
                    Tone = "urgent",
                    Ttl = 15.0,
                });
                if (!ok)
                    return;
                _alignWarned = true;
                state.SetIssue("damage", "얼라인 심각 손상 — 즉시 수리 권장");
                state.AddNarrative("(점검) 얼라인 심각 손상 → 박스 콜");
                return;
            }
            bool accepted2 = bus.Push(new RadioEvent
            {
                Type = EventTypes.DamageReport,
                Priority = Priority.High,
                Message = "Steering pull on the straights. Alignment's off from that hit. "
                          + "It'll eat the tyre. Repair call on pace.",
                DedupKey = "align_damage",
                Ttl = 20.0,
                Tone = "casual",
            });
            if (!accepted2)
                return;
            _alignWarned = true;
            state.SetIssue("damage", "얼라인 틀어짐 의심 (직선 조향 오프셋 변화)");
            state.AddNarrative("(점검) 얼라인 틀어짐 감지 — 직선 조향 오프셋 변화");
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
                Message = "Rear keeps stepping out. Looks damage-related. "
                          + "Don't push, recommend box.",
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
                        Message = WheelNames[i] + " losing pressure. Slow puncture. "
                                  + "We change it next stop.",
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
            if (p == null)
                return;

            CheckDamagePace(state, bus);

            double water = p.WaterTemp;
            double oil = p.OilTemp;
            if (p.Overheating || water >= _waterWarn || oil >= _oilWarn)
            {
                string what = water >= _waterWarn ? "Water temp"
                    : oil >= _oilWarn ? "Oil temp" : "Engine temp";
                var ev = new RadioEvent
                {
                    Type = EventTypes.EngineWarning,
                    Priority = Priority.High,
                    Message = what + " climbing. Get out of the slipstream, give it air.",
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
                if (avgBrake >= _brakeWarn)
                {
                    var ev = new RadioEvent
                    {
                        Type = EventTypes.BrakeWarning,
                        Priority = Priority.Normal,
                        Message = "Brakes averaging "
                                  + avgBrake.ToString("F0", CultureInfo.InvariantCulture)
                                  + " degrees. Brake a touch earlier, cool them.",
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
                    Message = "Damage is costing " + deltaS + " a lap. "
                              + "We repair at the next stop. It pays off.",
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
                    Message = "That contact — no effect on pace. Forget it.",
                    DedupKey = "dmg_ok_" + _impactLap.Value,
                    Ttl = 30.0,
                });
                state.ClearIssue("damage");
                _impactLap = null;
            }
        }
    }
}
