using System;
using System.Collections.Generic;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// voice.py PhrasePool 포팅 — 이벤트 타입(풀 키) × 톤(casual/urgent)별
    /// 변형 멘트 풀. 최근 사용 이력(키 단위 큐)을 피해서 뽑아 반복감을 줄인다.
    /// 데이터는 PhraseLines.Generated.cs (YAML에서 자동 생성).
    /// </summary>
    public sealed class PhrasePool
    {
        private const int RecentExclude = 5;   // 같은 풀에서 최근 N개는 다시 안 씀

        // 풀 키 → (톤, 변형들) 목록 — YAML 순서 유지 (톤 폴백 순서에 중요)
        private readonly Dictionary<string, List<PhraseSet>> _pools =
            new Dictionary<string, List<PhraseSet>>();
        private readonly List<string> _poolOrder = new List<string>();
        private readonly Dictionary<string, Queue<string>> _recent =
            new Dictionary<string, Queue<string>>();
        private readonly Random _rng;

        public PhrasePool(Random rng = null)
        {
            _rng = rng ?? new Random();
            foreach (PhraseSet set in PhraseLines.All)
            {
                List<PhraseSet> list;
                if (!_pools.TryGetValue(set.Pool, out list))
                {
                    list = new List<PhraseSet>();
                    _pools[set.Pool] = list;
                    _poolOrder.Add(set.Pool);
                }
                list.Add(set);
            }
        }

        public IEnumerable<string> PoolKeys
        {
            get { return _poolOrder; }
        }

        /// <summary>해당 톤의 변형 목록. 톤이 없으면 있는 톤으로 폴백.</summary>
        public string[] Lines(string poolKey, string tone = "casual")
        {
            List<PhraseSet> entry;
            if (!_pools.TryGetValue(poolKey, out entry))
                return new string[0];
            foreach (PhraseSet set in entry)
            {
                if (set.Tone == tone && set.Lines != null && set.Lines.Length > 0)
                    return set.Lines;
            }
            foreach (PhraseSet set in entry)     // 폴백: 첫 번째 비어있지 않은 톤
            {
                if (set.Lines != null && set.Lines.Length > 0)
                    return set.Lines;
            }
            return new string[0];
        }

        /// <summary>최근 사용을 피해 하나 뽑아 슬롯을 채운다. 실패 시 null.</summary>
        public string Pick(string poolKey, Dictionary<string, string> slots,
                           string tone = "casual")
        {
            string[] pool = Lines(poolKey, tone);
            if (pool.Length == 0)
                return null;

            Queue<string> recent;
            if (!_recent.TryGetValue(poolKey, out recent))
            {
                recent = new Queue<string>();
                _recent[poolKey] = recent;
            }
            int maxRecent = Math.Min(RecentExclude, Math.Max(pool.Length - 1, 1));

            var candidates = new List<string>();
            foreach (string p in pool)
            {
                if (!recent.Contains(p))
                    candidates.Add(p);
            }
            if (candidates.Count == 0)
                candidates.AddRange(pool);

            string phrase = candidates[_rng.Next(candidates.Count)];
            recent.Enqueue(phrase);
            while (recent.Count > maxRecent)
                recent.Dequeue();

            return Format(phrase, slots);
        }

        /// <summary>
        /// 파이썬 str.format(**slots)의 최소 구현 — "{name}" 토큰 치환.
        /// 없는 슬롯을 참조하면 null (파이썬 KeyError → None과 동일).
        /// </summary>
        public static string Format(string phrase, Dictionary<string, string> slots)
        {
            var sb = new System.Text.StringBuilder(phrase.Length + 16);
            int i = 0;
            while (i < phrase.Length)
            {
                char c = phrase[i];
                if (c == '{')
                {
                    if (i + 1 < phrase.Length && phrase[i + 1] == '{')
                    {
                        sb.Append('{');
                        i += 2;
                        continue;
                    }
                    int end = phrase.IndexOf('}', i + 1);
                    if (end < 0)
                        return null;    // 짝 없는 중괄호 — 파이썬도 ValueError
                    string name = phrase.Substring(i + 1, end - i - 1);
                    string value;
                    if (slots == null || !slots.TryGetValue(name, out value))
                        return null;    // KeyError와 동일 취급
                    sb.Append(value);
                    i = end + 1;
                }
                else if (c == '}')
                {
                    if (i + 1 < phrase.Length && phrase[i + 1] == '}')
                    {
                        sb.Append('}');
                        i += 2;
                        continue;
                    }
                    return null;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }
    }
}
