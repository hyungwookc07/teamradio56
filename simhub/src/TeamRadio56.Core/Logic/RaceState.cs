using System.Collections.Generic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// state.py SessionState의 최소 포팅 — 지금은 분석기들이 쓰는 부분만.
    /// (랩 기록/베이스라인/자동 저장은 연료·페이스 분석기 이식 때 채운다.)
    /// </summary>
    public sealed class RaceState
    {
        public const int RaceSessionMin = 10;   // mSession 10-13 = race

        public int? SessionType;

        /// <summary>진행 중 이슈 — LLM 문맥 연속성용 (키 → 설명).</summary>
        public readonly Dictionary<string, string> Issues =
            new Dictionary<string, string>();

        public bool IsRace
        {
            get { return SessionType.HasValue && SessionType.Value >= RaceSessionMin; }
        }

        public void SetIssue(string key, string text)
        {
            string cur;
            if (!Issues.TryGetValue(key, out cur) || cur != text)
                Issues[key] = text;
        }

        public void ClearIssue(string key)
        {
            Issues.Remove(key);
        }

        /// <summary>매 틱 세션 종류만 추적 (전체 update 포팅 전까지의 최소본).</summary>
        public void Observe(Snapshot snap)
        {
            if (snap != null && snap.Session != null && snap.InSession)
                SessionType = snap.Session.SessionType;
        }

        public void Reset()
        {
            SessionType = null;
            Issues.Clear();
        }
    }
}
