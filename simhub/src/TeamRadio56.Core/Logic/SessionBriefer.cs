using System;
using System.Collections.Generic;
using System.Globalization;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// main.py _maybe_session_briefing + briefing.py 포팅 — 세션 시작 브리핑 (세션당 1회).
    /// 주행 가능 상태에서 첫 데이터가 잡히면 무전 체크로 열고 세션 종류마다 다른 내용을
    /// 브리핑한다 (트랙 이름은 말하지 않는다). 세션 중간 합류도 현재 상황 기준으로.
    /// 문장 슬롯마다 변형은 스냅샷 값 시드의 FNV-1a 해시로 고른다 (Messages.Variant) —
    /// 파이썬과 같은 문장이라 리플레이가 문장 단위로 대조한다.
    /// </summary>
    public sealed class SessionBriefer
    {
        /// <summary>스타트 스포터 모드 길이(초) — 0이면 브리핑에서 예고 생략.</summary>
        public double StartSpotterSec = 45;

        private bool _briefed;

        public void Reset()
        {
            _briefed = false;
        }

        public void MaybeBrief(RaceState state, Snapshot snap, EventBus bus)
        {
            if (_briefed)
                return;
            if (snap.PlayerScoring() == null)
                return;
            _briefed = true;
            string text = Compose(state, snap, StartSpotterSec);
            if (text == null)
                return;
            bus.Push(new RadioEvent
            {
                Type = EventTypes.SessionBriefing,
                Priority = Priority.Normal,
                Message = text,
                DedupKey = "session_brief",
                Ttl = 60.0,
                Tone = "casual",
            });
        }

        /// <summary>브리핑 문장의 재료 — 스냅샷에서 뽑은 값만 (briefing.BriefingInputs 미러).</summary>
        public sealed class BriefingInputs
        {
            public int SessionType;
            public double EndEt;
            public double CurEt;
            public int MaxLaps;
            public bool MidJoin;
            public int ClsCount;
            public int ClassPlace;
            public double TrackTemp;
            public double Raining;
            public double Fuel;
            public double StartSpotterSec;
        }

        /// <summary>
        /// briefing.py compose_session_briefing 미러 — 브리핑 문장.
        /// 플레이어 차량이 아직 없으면 null. 리플레이 러너도 이걸 쓴다.
        /// </summary>
        public static string Compose(RaceState state, Snapshot snap, double startSpotterSec)
        {
            VehicleInfo me = snap.PlayerScoring();
            if (me == null)
                return null;

            SessionInfo ses = snap.Session;
            int stype = ses.SessionType;
            bool isRace = stype >= 10;
            double curEt = ses.CurrentEt;
            // 레이스의 그리드/포메이션 대기(롤링 스타트 전 ~40초)는 '시작'이다.
            // 세션 시계는 대기 중에도 흐르므로 경과 시간이 아니라 페이즈로
            // 판정한다 — 그린(5) 전이면 스타트 브리핑, 이후 합류면 중간 합류.
            bool midJoin = isRace
                ? ses.GamePhase >= 5 || me.TotalLaps > 0
                : curEt > 120 || me.TotalLaps > 0;
            int clsCount = 0, cp = 0;
            if (isRace)
            {
                foreach (VehicleInfo v in snap.Vehicles)
                {
                    if (v.Class == me.Class)
                        clsCount++;
                }
                cp = RaceState.ClassPlaceOf(snap, me);
            }
            return ComposeText(new BriefingInputs
            {
                SessionType = stype,
                EndEt = ses.EndEt,
                CurEt = curEt,
                MaxLaps = ses.MaxLaps,
                MidJoin = midJoin,
                ClsCount = clsCount,
                ClassPlace = cp,
                TrackTemp = ses.TrackTemp,
                Raining = ses.Raining,
                Fuel = snap.Player != null ? snap.Player.Fuel : 0.0,
                StartSpotterSec = startSpotterSec,
            });
        }

        /// <summary>rF2 mSession → practice(0~4) | quali(5~8) | warmup(9) | race(10~) (briefing.session_kind).</summary>
        public static string SessionKind(int sessionType)
        {
            if (sessionType >= 10)
                return "race";
            if (sessionType == 9)
                return "warmup";
            if (sessionType >= 5)
                return "quali";
            return "practice";
        }

        /// <summary>
        /// 변형 선택 시드 — briefing.briefing_seed와 바이트 단위로 같은 문자열
        /// (소수는 Messages.PyFixed = 파이썬 f-string, 정수는 InvariantCulture).
        /// </summary>
        public static string Seed(BriefingInputs b)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return string.Join("|", new[]
            {
                b.SessionType.ToString(ic), b.MidJoin ? "1" : "0",
                Messages.PyFixed(b.CurEt, 1), Messages.PyFixed(b.EndEt, 0),
                b.MaxLaps.ToString(ic),
                Messages.PyFixed(b.TrackTemp, 1), Messages.PyFixed(b.Fuel, 1),
                b.ClsCount.ToString(ic), b.ClassPlace.ToString(ic),
            });
        }

        /// <summary>
        /// (문장 그룹 키, 문장) 목록 — 순수 조립 (briefing.briefing_parts 미러).
        /// 도메인 밖 숫자(100랩 넘는 세션, 그리드/순위/노면/연료 범위 밖)는 그 문장만 뺀다.
        /// </summary>
        public static List<KeyValuePair<string, string>> ComposeParts(BriefingInputs b)
        {
            string seed = Seed(b);
            var parts = new List<KeyValuePair<string, string>>();
            Action<string, object[]> v = (key, kv) =>
                parts.Add(new KeyValuePair<string, string>(key, Messages.Variant(key, seed, kv)));

            string kind = SessionKind(b.SessionType);
            bool isRace = kind == "race";
            bool mid = b.MidJoin;
            v(mid ? "brief_open_" + kind + "_mid" : "brief_open_" + kind, new object[0]);

            // 세션 길이 — 시간제(잔여 기준)와 랩제 구분. 미기입 거대값은 무시.
            if (0 < b.EndEt && b.EndEt < 86400)
            {
                int minutes = Math.Max(
                    (int)Math.Round((b.EndEt - b.CurEt) / 60, MidpointRounding.ToEven), 1);
                if (minutes <= SpeechDomain.BriefMinutesMax)
                {
                    v(mid ? "brief_len_remaining" : "brief_len_long",
                      new object[] { "length", Messages.BriefLengthText(minutes) });
                }
            }
            else if (0 < b.MaxLaps && b.MaxLaps <= SpeechDomain.BriefLapsMax)
            {
                parts.Add(new KeyValuePair<string, string>("brief_laps",
                    b.MaxLaps == 1 ? Messages.Get("brief_lap")
                                   : Messages.Get("brief_laps", "n", b.MaxLaps)));
            }

            if (isRace && b.ClsCount > 1 && SpeechDomain.BriefGridOk(b.ClsCount, b.ClassPlace))
            {
                parts.Add(new KeyValuePair<string, string>("brief_cars",
                    Messages.Get("brief_cars", "n", b.ClsCount)));
                v(mid ? "brief_grid_mid" : "brief_grid", new object[] { "cp", b.ClassPlace });
            }

            if (b.TrackTemp > 0 && SpeechDomain.BriefTempOk(b.TrackTemp))
                v("brief_temp", new object[] { "t", b.TrackTemp });
            if (b.Raining >= 0.05)
                v("brief_rain", new object[0]);

            // 연료는 레이스만 — 연습/퀄리/웜업은 런 계획마다 바뀌어 의미가 없다
            if (isRace && b.Fuel != 0.0 && SpeechDomain.BriefFuelOk(b.Fuel))
                v("brief_fuel", new object[] { "f", b.Fuel });

            if (mid)
            {
                if (kind == "quali")
                    v("brief_limits_quali", new object[0]);
                v("brief_close_mid", new object[0]);
            }
            else if (kind == "practice")
            {
                v("brief_plan_practice", new object[0]);
                v("brief_close_out", new object[0]);
            }
            else if (kind == "quali")
            {
                v("brief_gap_quali", new object[0]);
                v("brief_limits_quali", new object[0]);
                v("brief_close_quali", new object[0]);
            }
            else if (kind == "warmup")
            {
                v("brief_plan_warmup", new object[0]);
                v("brief_close_out", new object[0]);
            }
            else
            {
                // 스타트 전용: 포메이션/그린 콜 안내 + 스타트 스포터 모드 예고 —
                // 그리드 대기 ~40초를 채우는 실전 브리핑
                v("brief_formation", new object[0]);
                if (b.StartSpotterSec > 0)
                    v("brief_start_spotter", new object[0]);
                v("brief_close_race", new object[0]);
            }
            return parts;
        }

        /// <summary>브리핑 한 덩어리 (briefing.briefing_text 미러).</summary>
        public static string ComposeText(BriefingInputs b)
        {
            var texts = new List<string>();
            foreach (KeyValuePair<string, string> p in ComposeParts(b))
                texts.Add(p.Value);
            return string.Join(" ", texts);
        }
    }
}
