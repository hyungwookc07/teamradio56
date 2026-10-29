using System;
using System.Collections.Generic;
using System.Globalization;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// main.py _maybe_session_briefing + briefing.py 포팅 — 세션 시작 브리핑 (세션당 1회).
    /// 주행 가능 상태에서 첫 데이터가 잡히면 세션 종류/길이/그리드/날씨/연료를
    /// 한 번에 브리핑한다. 세션 중간 합류도 현재 상황 기준으로.
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
            string kind = Messages.Get(isRace ? "kind_race"
                : stype == 9 ? "kind_warmup"
                : stype >= 5 ? "kind_quali" : "kind_practice");
            string track = (ses.Track ?? "").Trim();
            var parts = new List<string>
            {
                track.Length > 0
                    ? Messages.Get("brief_track", "track", track, "kind", kind)
                    : Messages.Get("brief_radio", "kind", kind),
            };

            // 세션 길이 — 시간제(잔여 기준)와 랩제 구분. 미기입 거대값은 무시.
            double endEt = ses.EndEt;
            double curEt = ses.CurrentEt;
            int maxLaps = ses.MaxLaps;
            // 레이스의 그리드/포메이션 대기(롤링 스타트 전 ~40초)는 '시작'이다.
            // 세션 시계는 대기 중에도 흐르므로 경과 시간이 아니라 페이즈로
            // 판정한다 — 그린(5) 전이면 스타트 브리핑, 이후 합류면 중간 합류.
            bool midJoin = isRace
                ? ses.GamePhase >= 5 || me.TotalLaps > 0
                : curEt > 120 || me.TotalLaps > 0;
            if (0 < endEt && endEt < 86400)
            {
                int minutes = Math.Max(
                    (int)Math.Round((endEt - curEt) / 60, MidpointRounding.ToEven), 1);
                parts.Add(Messages.Get(midJoin ? "brief_len_remaining" : "brief_len_long",
                    "length", Messages.BriefLengthText(minutes)));
            }
            else if (0 < maxLaps && maxLaps < 10000)
            {
                parts.Add(maxLaps == 1 ? Messages.Get("brief_lap")
                    : Messages.Get("brief_laps", "n", maxLaps));
            }

            if (isRace)
            {
                int clsCount = 0;
                foreach (VehicleInfo v in snap.Vehicles)
                {
                    if (v.Class == me.Class)
                        clsCount++;
                }
                int cp = RaceState.ClassPlaceOf(snap, me);
                if (clsCount > 1)
                {
                    parts.Add(Messages.Get(midJoin ? "brief_grid_mid" : "brief_grid",
                        "cp", cp, "n", clsCount));
                }
            }

            if (ses.TrackTemp > 0)
                parts.Add(Messages.Get("brief_temp", "t", ses.TrackTemp));
            if (ses.Raining >= 0.05)
                parts.Add(Messages.Get("brief_rain"));

            double fuel = snap.Player != null ? snap.Player.Fuel : 0.0;
            if (fuel != 0.0)
                parts.Add(Messages.Get("brief_fuel", "f", fuel));

            // 스타트 전용: 포메이션/그린 콜 안내 + 스타트 스포터 모드 예고 —
            // 그리드 대기 ~40초를 채우는 실전 브리핑
            if (isRace && !midJoin)
            {
                parts.Add(Messages.Get("brief_formation"));
                if (startSpotterSec > 0)
                    parts.Add(Messages.Get("brief_start_spotter"));
            }

            parts.Add(Messages.Get(midJoin ? "brief_carry_on"
                : isRace ? "brief_calm" : "brief_out"));
            return string.Join(" ", parts);
        }
    }
}
