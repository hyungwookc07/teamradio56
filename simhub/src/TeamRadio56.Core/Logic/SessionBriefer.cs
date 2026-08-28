using System;
using System.Collections.Generic;
using System.Globalization;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// main.py _maybe_session_briefing 포팅 — 세션 시작 브리핑 (세션당 1회).
    /// 주행 가능 상태에서 첫 데이터가 잡히면 세션 종류/길이/그리드/날씨/연료를
    /// 한 번에 브리핑한다. 세션 중간 합류도 현재 상황 기준으로.
    /// </summary>
    public sealed class SessionBriefer
    {
        private bool _briefed;

        public void Reset()
        {
            _briefed = false;
        }

        public void MaybeBrief(RaceState state, Snapshot snap, EventBus bus)
        {
            if (_briefed)
                return;
            VehicleInfo me = snap.PlayerScoring();
            if (me == null)
                return;
            _briefed = true;

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
            bool midJoin = curEt > 120 || me.TotalLaps > 0;
            if (0 < endEt && endEt < 86400)
            {
                int minutes = Math.Max(
                    (int)Math.Round((endEt - curEt) / 60, MidpointRounding.ToEven), 1);
                string length;
                if (minutes >= 60 && minutes % 60 == 0)
                {
                    int h = minutes / 60;
                    length = Messages.Get(h > 1 ? "brief_hours_plural" : "brief_hours",
                        "h", h);
                }
                else
                {
                    length = Messages.Get("brief_minutes", "m", minutes);
                }
                parts.Add(Messages.Get(midJoin ? "brief_len_remaining" : "brief_len_long",
                    "length", length));
            }
            else if (0 < maxLaps && maxLaps < 10000)
            {
                parts.Add(Messages.Get("brief_laps", "n", maxLaps));
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

            double rain = ses.Raining;
            if (rain >= 0.05)
                parts.Add(Messages.Get("brief_rain"));
            else if (ses.TrackTemp > 0)
                parts.Add(Messages.Get("brief_temp", "t", ses.TrackTemp));

            double fuel = snap.Player != null ? snap.Player.Fuel : 0.0;
            if (fuel != 0.0)
                parts.Add(Messages.Get("brief_fuel", "f", fuel));

            parts.Add(Messages.Get(midJoin ? "brief_carry_on"
                : isRace ? "brief_calm" : "brief_out"));
            bus.Push(new RadioEvent
            {
                Type = EventTypes.SessionBriefing,
                Priority = Priority.Normal,
                Message = string.Join(" ", parts),
                DedupKey = "session_brief",
                Ttl = 60.0,
                Tone = "casual",
            });
        }
    }
}
