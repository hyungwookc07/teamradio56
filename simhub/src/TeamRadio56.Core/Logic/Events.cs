using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace TeamRadio56.Core.Logic
{
    /// <summary>events.py 포팅 — 우선순위/이벤트 타입/이벤트 버스.</summary>
    public enum Priority
    {
        Critical = 0,   // 즉시 발화, 재생 중인 저우선순위 멘트 중단 가능
        High = 1,       // 다음 차례에 발화
        Normal = 2,     // 여유 있을 때 발화 (LLM 멘트 등)
    }

    /// <summary>이벤트 타입 상수 (쿨다운 키로도 사용). events.py의 EventType.</summary>
    public static class EventTypes
    {
        public const string FuelCritical = "fuel_critical";
        public const string FuelWarning = "fuel_warning";
        public const string PitCall = "pit_call";
        public const string TrafficApproach = "traffic_approach";
        public const string TrafficClose = "traffic_close";
        public const string TrafficUpdate = "traffic_update";
        public const string TrafficMulti = "traffic_multi";
        public const string Spotter = "spotter";
        public const string BridgeFollowup = "bridge_followup";
        public const string Damage = "damage";
        public const string DamageReport = "damage_report";
        public const string WheelDamage = "wheel_damage";
        public const string PartDetached = "part_detached";
        public const string Penalty = "penalty";
        public const string PaceComment = "pace_comment";
        public const string GapComment = "gap_comment";
        public const string TyreWarning = "tyre_warning";
        public const string LapAnalysis = "lap_analysis";
        public const string StintBriefing = "stint_briefing";
        public const string SessionBriefing = "session_briefing";
        public const string LapTimeReport = "lap_time_report";
        public const string StatusReport = "status_report";
        public const string RaceStart = "race_start";
        public const string RaceEnd = "race_end";
        public const string LapFeedback = "lap_feedback";
        public const string TrackTrend = "track_trend";
        public const string Fcy = "fcy";
        public const string FcyPitOpen = "fcy_pit_open";
        public const string GreenFlag = "green_flag";
        public const string SectorYellow = "sector_yellow";
        public const string BlueFlag = "blue_flag";
        public const string RaceMilestone = "race_milestone";
        public const string PositionChange = "position_change";
        public const string RivalPit = "rival_pit";
        public const string RivalPace = "rival_pace";
        public const string PitLimiter = "pit_limiter";
        public const string EngineWarning = "engine_warning";
        public const string BrakeWarning = "brake_warning";
        public const string FuelSave = "fuel_save";
    }

    /// <summary>events.py의 Event. C# 예약어/충돌을 피해 RadioEvent.</summary>
    public sealed class RadioEvent
    {
        public string Type;
        public Priority Priority;
        public Dictionary<string, object> Data = new Dictionary<string, object>();
        public string Message;              // 완성된 멘트 텍스트 (있으면 그대로 사용)
        public string DedupKey;             // 없으면 Type이 dedup 키
        public double? Ttl;
        public string Tone = "casual";      // casual | urgent
        public string BridgeTopic;          // 긴급 콜 뒤 LLM 후속 생성 컨텍스트
        public Func<bool> ValidFn;          // 재생 직전 유효성 검사 (false → 폐기)
        public double CreatedAt;            // 버스가 push 시점에 채운다

        public string Key
        {
            get { return DedupKey ?? Type; }
        }

        public bool Expired(double now)
        {
            double ttl = Ttl ?? DefaultTtl(Priority);
            return now - CreatedAt > ttl;
        }

        public static double DefaultTtl(Priority p)
        {
            switch (p)
            {
                case Priority.Critical: return 8.0;   // 긴급 콜은 8초 지나면 의미 없음
                case Priority.High: return 20.0;
                default: return 45.0;
            }
        }
    }

    /// <summary>
    /// 이벤트 버스 (events.py의 EventBus 포팅).
    ///
    /// 시계를 주입받는다 — 실전에선 monotonic 실시간, 리플레이 회귀에선
    /// 스냅샷 기록 시각. 파이썬 하니스도 같은 방식으로 가상 시계를 써서
    /// 두 구현의 쿨다운/TTL 판정이 결정적으로 일치한다.
    /// </summary>
    public sealed class EventBus
    {
        // 타입 → 쿨다운 설정 키 (events.py의 COOLDOWN_KEY)
        private static readonly Dictionary<string, string> CooldownKey =
            new Dictionary<string, string>
            {
                { EventTypes.FuelCritical, "fuel_warning" },
                { EventTypes.FuelWarning, "fuel_warning" },
                { EventTypes.TrafficApproach, "traffic" },
                { EventTypes.TrafficClose, "traffic_close" },
                { EventTypes.TrafficUpdate, "traffic_update" },
                { EventTypes.TrafficMulti, "traffic_multi" },
                { EventTypes.Spotter, "spotter" },
                { EventTypes.BridgeFollowup, "bridge" },
                { EventTypes.PaceComment, "pace_comment" },
                { EventTypes.GapComment, "gap_comment" },
                { EventTypes.TyreWarning, "tyre_warning" },
                { EventTypes.Damage, "damage" },
                { EventTypes.DamageReport, "damage_report" },
                { EventTypes.WheelDamage, "wheel_damage" },
                { EventTypes.Penalty, "penalty" },
                { EventTypes.Fcy, "race_control" },
                { EventTypes.FcyPitOpen, "fcy_pit_open" },
                { EventTypes.GreenFlag, "green_flag" },
                { EventTypes.SectorYellow, "sector_yellow" },
                { EventTypes.BlueFlag, "blue_flag" },
                { EventTypes.RaceMilestone, "race_milestone" },
                { EventTypes.PositionChange, "position_change" },
                { EventTypes.RivalPit, "rival_pit" },
                { EventTypes.RivalPace, "rival_pace" },
                { EventTypes.PitLimiter, "pit_limiter" },
                { EventTypes.EngineWarning, "engine_warning" },
                { EventTypes.BrakeWarning, "engine_warning" },
                { EventTypes.FuelSave, "fuel_save" },
            };

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly Dictionary<string, double> _cooldowns;
        private readonly Func<double> _now;
        private readonly object _gate = new object();
        // (priority, seq) 순 정렬 힙 대신 정렬 리스트 — 대기 이벤트는 항상 소수
        private readonly List<RadioEvent> _queue = new List<RadioEvent>();
        private long _seq;
        private readonly Dictionary<RadioEvent, long> _seqOf =
            new Dictionary<RadioEvent, long>();
        private readonly HashSet<string> _pendingKeys = new HashSet<string>();
        private readonly Dictionary<string, double> _lastFired =
            new Dictionary<string, double>();
        private readonly ManualResetEventSlim _available = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _urgentPending = new ManualResetEventSlim(false);

        public EventBus(Dictionary<string, double> cooldowns, Func<double> clock = null)
        {
            _cooldowns = cooldowns ?? new Dictionary<string, double>();
            _now = clock ?? (() => Clock.Elapsed.TotalSeconds);
        }

        /// <summary>
        /// push가 수락될 때마다 호출 (락 밖). 리플레이 회귀의 기록 지점이자
        /// 플러그인 UI의 "최근 무전" 소스.
        /// </summary>
        public Action<RadioEvent> Accepted;

        /// <summary>재생 중단 판단용 — CRITICAL 이벤트가 대기 중인가.</summary>
        public bool UrgentPending
        {
            get { return _urgentPending.IsSet; }
        }

        private double CooldownSec(string etype)
        {
            string key;
            if (!CooldownKey.TryGetValue(etype, out key))
                key = etype;
            double sec;
            if (_cooldowns.TryGetValue(key, out sec))
                return sec;
            return _cooldowns.TryGetValue("default", out sec) ? sec : 60.0;
        }

        /// <summary>수락되면 true. 쿨다운/중복으로 버려지면 false.</summary>
        public bool Push(RadioEvent ev)
        {
            double now = _now();
            lock (_gate)
            {
                if (_pendingKeys.Contains(ev.Key))
                    return false;
                string cdKey;
                if (!CooldownKey.TryGetValue(ev.Type, out cdKey))
                    cdKey = ev.Type;
                double last;
                if (_lastFired.TryGetValue(cdKey, out last)
                    && now - last < CooldownSec(ev.Type))
                {
                    // CRITICAL은 같은 쿨다운 그룹의 하위 우선순위보다 한 단계 봐준다:
                    // 쿨다운 절반이 지났으면 통과 (연료 warning 직후 critical 등)
                    if (!(ev.Priority == Priority.Critical
                          && now - last >= CooldownSec(ev.Type) * 0.5))
                    {
                        return false;
                    }
                }
                _lastFired[cdKey] = now;
                _pendingKeys.Add(ev.Key);
                ev.CreatedAt = now;
                _seqOf[ev] = _seq++;
                _queue.Add(ev);
                _available.Set();
                if (ev.Priority == Priority.Critical)
                    _urgentPending.Set();
            }
            Action<RadioEvent> accepted = Accepted;
            if (accepted != null)
            {
                try { accepted(ev); } catch (Exception) { }
            }
            return true;
        }

        /// <summary>우선순위가 가장 높은 유효 이벤트를 꺼낸다. 없으면 null.</summary>
        public RadioEvent Pop(double timeoutSec = 0.5)
        {
            if (!_available.Wait(TimeSpan.FromSeconds(timeoutSec)))
                return null;
            double now = _now();
            lock (_gate)
            {
                while (_queue.Count > 0)
                {
                    RadioEvent ev = TakeBest();
                    _pendingKeys.Remove(ev.Key);
                    if (ev.Expired(now))
                        continue;
                    if (ev.ValidFn != null)
                    {
                        bool valid = true;
                        try
                        {
                            valid = ev.ValidFn();
                        }
                        catch (Exception)
                        {
                            // 유효성 검사 실패는 폐기하지 않는 쪽이 안전
                        }
                        if (!valid)
                            continue;
                    }
                    if (_queue.Count == 0)
                        _available.Reset();
                    if (!AnyCriticalQueued())
                        _urgentPending.Reset();
                    return ev;
                }
                _available.Reset();
                _urgentPending.Reset();
            }
            return null;
        }

        private RadioEvent TakeBest()
        {
            int best = 0;
            for (int i = 1; i < _queue.Count; i++)
            {
                RadioEvent a = _queue[i];
                RadioEvent b = _queue[best];
                if ((int)a.Priority < (int)b.Priority
                    || ((int)a.Priority == (int)b.Priority && _seqOf[a] < _seqOf[b]))
                {
                    best = i;
                }
            }
            RadioEvent ev = _queue[best];
            _queue.RemoveAt(best);
            _seqOf.Remove(ev);
            return ev;
        }

        private bool AnyCriticalQueued()
        {
            for (int i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].Priority == Priority.Critical)
                    return true;
            }
            return false;
        }

        /// <summary>세션 경계에서 호출 — 대기 이벤트와 쿨다운 기록을 모두 비운다.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _queue.Clear();
                _seqOf.Clear();
                _pendingKeys.Clear();
                _lastFired.Clear();
                _available.Reset();
                _urgentPending.Reset();
            }
        }
    }

    /// <summary>config.py DEFAULTS["cooldowns"] 포팅 + 수다스러움 배율.</summary>
    public static class Cooldowns
    {
        public static Dictionary<string, double> Default(double scale = 1.0)
        {
            var raw = new Dictionary<string, double>
            {
                { "fuel_warning", 240 },
                { "traffic", 20 },
                { "traffic_close", 8 },
                { "traffic_update", 15 },
                { "traffic_multi", 25 },
                { "spotter", 1 },
                { "bridge", 20 },
                { "pace_comment", 120 },
                { "gap_comment", 90 },
                { "tyre_warning", 180 },
                { "damage", 60 },
                { "damage_report", 20 },
                { "wheel_damage", 30 },
                { "penalty", 30 },
                { "lap_analysis", 300 },
                { "stint_briefing", 120 },
                { "lap_feedback", 150 },
                { "track_trend", 600 },
                { "race_control", 15 },
                { "fcy_pit_open", 45 },
                { "green_flag", 30 },
                { "sector_yellow", 45 },
                { "blue_flag", 45 },
                { "race_milestone", 60 },
                { "lap_time_report", 25 },
                { "status_report", 60 },
                { "position_change", 45 },
                { "rival_pit", 90 },
                { "rival_pace", 240 },
                { "pit_limiter", 15 },
                { "engine_warning", 240 },
                { "fuel_save", 180 },
                { "default", 60 },
            };
            if (Math.Abs(scale - 1.0) < 1e-9)
                return raw;
            var scaled = new Dictionary<string, double>(raw.Count);
            foreach (KeyValuePair<string, double> kv in raw)
                scaled[kv.Key] = Math.Round(kv.Value * scale);
            return scaled;
        }
    }
}
