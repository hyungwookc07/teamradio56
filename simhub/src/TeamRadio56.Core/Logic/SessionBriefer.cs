using System;
using System.Collections.Generic;
using System.Globalization;
using TeamRadio56.Core.Diagnostics;
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

        /// <summary>세션 목소리가 음성 팩만 말함 (kokoro, 섞임 비허용) — en은 목록에 없는 트랙 이름을 뺀다.</summary>
        public bool PackOnly;

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
            string text = Compose(state, snap, StartSpotterSec, PackOnly);
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

        private static readonly HashSet<string> UnknownLogged =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>브리핑 문장의 재료 — 스냅샷에서 뽑은 값만 (briefing.BriefingInputs 미러).</summary>
        public sealed class BriefingInputs
        {
            public int SessionType;
            public string Track = "";
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
        public static string Compose(RaceState state, Snapshot snap, double startSpotterSec,
                                     bool packOnly = false)
        {
            VehicleInfo me = snap.PlayerScoring();
            if (me == null)
                return null;

            SessionInfo ses = snap.Session;
            int stype = ses.SessionType;
            bool isRace = stype >= 10;
            string track = (ses.Track ?? "").Trim();
            if (packOnly && track.Length > 0 && UnspeakableTrack(track))
            {
                bool first;
                lock (UnknownLogged)
                    first = UnknownLogged.Add(track);
                if (first)
                {
                    FileLog.Info("트랙 이름 목록에 없어 브리핑에서 생략: " + track
                                 + " (voice_lines/track_names_en.txt에 추가)");
                }
            }
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
                Track = track,
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
            }, packOnly);
        }

        /// <summary>음성 팩이 말할 수 없는 트랙 이름 — 목록은 en 전용.</summary>
        private static bool UnspeakableTrack(string track)
        {
            return !Messages.IsKo && !SpeechDomain.KnownTrackEn(track);
        }

        /// <summary>
        /// 세션 종류/길이/그리드/날씨/연료 브리핑 — 순수 조립 (briefing.briefing_text 미러).
        /// 도메인 밖 숫자(100랩 넘는 세션, 그리드/순위/노면/연료 범위 밖)는 그 문장만 뺀다.
        /// packOnly(세션 목소리가 음성 팩만 말하는 kokoro)면 en은 트랙 이름 목록에 없는
        /// 트랙을 '{track}.' 대신 무전 체크로 연다 — edge는 어떤 이름이든 말하므로 그대로.
        /// </summary>
        public static string ComposeText(BriefingInputs b, bool packOnly = false)
        {
            int stype = b.SessionType;
            bool isRace = stype >= 10;
            string kind = Messages.Get(isRace ? "kind_race"
                : stype == 9 ? "kind_warmup"
                : stype >= 5 ? "kind_quali" : "kind_practice");
            string track = (b.Track ?? "").Trim();
            var parts = new List<string>
            {
                track.Length > 0 && !(packOnly && UnspeakableTrack(track))
                    ? Messages.Get("brief_track", "track", track, "kind", kind)
                    : Messages.Get("brief_radio", "kind", kind),
            };

            // 세션 길이 — 시간제(잔여 기준)와 랩제 구분. 미기입 거대값은 무시.
            bool midJoin = b.MidJoin;
            if (0 < b.EndEt && b.EndEt < 86400)
            {
                int minutes = Math.Max(
                    (int)Math.Round((b.EndEt - b.CurEt) / 60, MidpointRounding.ToEven), 1);
                if (minutes <= SpeechDomain.BriefMinutesMax)
                {
                    parts.Add(Messages.Get(midJoin ? "brief_len_remaining" : "brief_len_long",
                        "length", Messages.BriefLengthText(minutes)));
                }
            }
            else if (0 < b.MaxLaps && b.MaxLaps <= SpeechDomain.BriefLapsMax)
            {
                parts.Add(b.MaxLaps == 1 ? Messages.Get("brief_lap")
                    : Messages.Get("brief_laps", "n", b.MaxLaps));
            }

            if (isRace && b.ClsCount > 1 && SpeechDomain.BriefGridOk(b.ClsCount, b.ClassPlace))
            {
                parts.Add(Messages.Get(midJoin ? "brief_grid_mid" : "brief_grid",
                    "cp", b.ClassPlace, "n", b.ClsCount));
            }

            if (b.TrackTemp > 0 && SpeechDomain.BriefTempOk(b.TrackTemp))
                parts.Add(Messages.Get("brief_temp", "t", b.TrackTemp));
            if (b.Raining >= 0.05)
                parts.Add(Messages.Get("brief_rain"));

            if (b.Fuel != 0.0 && SpeechDomain.BriefFuelOk(b.Fuel))
                parts.Add(Messages.Get("brief_fuel", "f", b.Fuel));

            // 스타트 전용: 포메이션/그린 콜 안내 + 스타트 스포터 모드 예고 —
            // 그리드 대기 ~40초를 채우는 실전 브리핑
            if (isRace && !midJoin)
            {
                parts.Add(Messages.Get("brief_formation"));
                if (b.StartSpotterSec > 0)
                    parts.Add(Messages.Get("brief_start_spotter"));
            }

            parts.Add(Messages.Get(midJoin ? "brief_carry_on"
                : isRace ? "brief_calm" : "brief_out"));
            return string.Join(" ", parts);
        }
    }
}
