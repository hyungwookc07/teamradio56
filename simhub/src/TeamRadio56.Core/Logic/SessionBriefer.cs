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
            string kind = isRace ? "Race"
                : stype == 9 ? "Warmup"
                : stype >= 5 ? "Qualifying" : "Practice";
            string track = (ses.Track ?? "").Trim();
            var parts = new List<string>
            {
                track.Length > 0
                    ? track + ". " + kind + " session."
                    : "Radio check. " + kind + " session.",
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
                if (minutes >= 120 && minutes % 60 == 0)
                {
                    int h = minutes / 60;
                    length = h + " hour" + (h > 1 ? "s" : "");
                }
                else
                {
                    length = minutes + " minutes";
                }
                parts.Add(length + " " + (midJoin ? "remaining" : "long") + ".");
            }
            else if (0 < maxLaps && maxLaps < 10000)
            {
                parts.Add(maxLaps + " laps.");
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
                    parts.Add("P" + cp + " of " + clsCount + " in class"
                              + (midJoin ? "." : " on the grid."));
                }
            }

            double rain = ses.Raining;
            if (rain >= 0.05)
                parts.Add("It's raining, watch the grip.");
            else if (ses.TrackTemp > 0)
            {
                parts.Add("Track "
                    + ses.TrackTemp.ToString("F0", CultureInfo.InvariantCulture)
                    + " degrees.");
            }

            double fuel = snap.Player != null ? snap.Player.Fuel : 0.0;
            if (fuel != 0.0)
            {
                parts.Add("Fuel "
                    + fuel.ToString("F0", CultureInfo.InvariantCulture) + " litres.");
            }

            parts.Add(midJoin ? "Carry on."
                : isRace ? "Calm first lap." : "Out when you're ready.");
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
