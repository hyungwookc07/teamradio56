using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// vehicle_class.py 포팅 — 차량 클래스 문자열(mVehicleClass)을 계열로 묶는 단일 출처.
    /// 트래픽(빠른 차/백마커), 다중 차량 종합, 체커 뒤 쿨다운 콜, 음성 렌더가 모두 이것만 본다.
    /// 파이썬과 결과가 바이트 단위로 같아야 한다 (replay-check의 클래스 정규화 대조).
    ///
    /// 정규화: ASCII 대문자만 소문자로, [a-z0-9] 밖의 문자는 구분자. 토큰을 앞에서부터, 그다음
    /// 전부 이어 붙인 문자열을 계열 패턴(토큰 전체 일치)에 맞춘다. 계열 서열 hypercar 5 > lmp2 4 >
    /// lmp3 3 > gte 2 > gt3 1 (LMP3 위치의 랩타임 근거는 vehicle_class.py), 모름 0.
    /// 모르는 클래스의 호칭은 "" — 클래스 이름 없이 말한다 (예전 "faster car").
    /// </summary>
    public static class VehicleClass
    {
        public const string Unknown = "unknown";

        /// <summary>관측 페이스 비교 여유 — 근거는 vehicle_class.PACE_MARGIN.</summary>
        public const double PaceMargin = 0.02;

        // (계열, 토큰 전체와 맞아야 하는 패턴) — 순서 = 우선순위. 파이썬 FAMILY_PATTERNS와 같은 문자열
        private static readonly KeyValuePair<string, Regex>[] Patterns =
        {
            P("hypercar", "hyper[a-z0-9]*|hy|lmh[0-9]*|lmdh[0-9]*"),
            P("lmp2", "lmp2(?:[a-z][a-z0-9]*)?"),
            P("lmp3", "lmp3(?:[a-z][a-z0-9]*)?"),
            P("gte", "(?:lm)?gte(?:pro|am)*[0-9]*"),
            P("gt3", "(?:lm)?gt3(?:[a-z][a-z0-9]*)?"),
        };

        private static readonly Dictionary<string, int> Ranks = new Dictionary<string, int>
        {
            { "hypercar", 5 }, { "lmp2", 4 }, { "lmp3", 3 }, { "gte", 2 }, { "gt3", 1 },
        };

        private static readonly Dictionary<string, string> Displays = new Dictionary<string, string>
        {
            { "hypercar", "Hypercar" }, { "lmp2", "LMP2" }, { "lmp3", "LMP3" },
            { "gte", "GTE" }, { "gt3", "GT3" },
        };

        /// <summary>호칭 전부 (파이썬 FAMILY_DISPLAY["en"] 순서) — 사전 생성 열거용.</summary>
        public static readonly string[] AllDisplays = { "Hypercar", "LMP2", "LMP3", "GTE", "GT3" };

        private static KeyValuePair<string, Regex> P(string family, string pattern)
        {
            // \A…\z = 파이썬 fullmatch (입력은 [a-z0-9]뿐)
            return new KeyValuePair<string, Regex>(family, new Regex(
                @"\A(?:" + pattern + @")\z", RegexOptions.CultureInvariant));
        }

        private static List<string> Tokens(string raw)
        {
            var output = new List<string>();
            var cur = new StringBuilder();
            foreach (char c0 in raw ?? "")
            {
                char c = c0;
                if (c >= 'A' && c <= 'Z')
                    c = (char)(c + 32);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    cur.Append(c);
                }
                else if (cur.Length > 0)
                {
                    output.Add(cur.ToString());
                    cur.Clear();
                }
            }
            if (cur.Length > 0)
                output.Add(cur.ToString());
            return output;
        }

        /// <summary>클래스 문자열 → 계열 (hypercar/lmp2/lmp3/gte/gt3/unknown).</summary>
        public static string Family(string raw)
        {
            List<string> toks = Tokens(raw);
            if (toks.Count == 0)
                return Unknown;
            var cands = new List<string>(toks) { string.Concat(toks) };
            foreach (string cand in cands)
            {
                foreach (KeyValuePair<string, Regex> p in Patterns)
                {
                    if (p.Value.IsMatch(cand))
                        return p.Key;
                }
            }
            return Unknown;
        }

        /// <summary>계열 서열 (0 = 모름).</summary>
        public static int Rank(string raw)
        {
            int r;
            return Ranks.TryGetValue(Family(raw), out r) ? r : 0;
        }

        /// <summary>무전 호칭 — 모르는 클래스는 "".</summary>
        public static string Display(string raw)
        {
            string d;
            return Displays.TryGetValue(Family(raw), out d) ? d : "";
        }

        /// <summary>같은 클래스 — 문자열이 같거나 같은 (아는) 계열.</summary>
        public static bool SameClass(string a, string b)
        {
            if ((a ?? "") == (b ?? ""))
                return true;
            string fa = Family(a);
            return fa != Unknown && fa == Family(b);
        }

        /// <summary>클래스 문자열 → 베스트 랩 중앙값 (best_lap &gt; 0만, 짝수면 가운데 둘 평균).</summary>
        public static Dictionary<string, double> Pace(IEnumerable<VehicleInfo> vehicles)
        {
            var laps = new Dictionary<string, List<double>>();
            foreach (VehicleInfo v in vehicles)
            {
                if (v.BestLap > 0)
                {
                    string key = v.Class ?? "";
                    List<double> xs;
                    if (!laps.TryGetValue(key, out xs))
                    {
                        xs = new List<double>();
                        laps[key] = xs;
                    }
                    xs.Add(v.BestLap);
                }
            }
            var output = new Dictionary<string, double>();
            foreach (KeyValuePair<string, List<double>> kv in laps)
            {
                List<double> xs = kv.Value;
                xs.Sort();
                int n = xs.Count;
                output[kv.Key] = n % 2 == 1 ? xs[n / 2] : (xs[n / 2 - 1] + xs[n / 2]) / 2;
            }
            return output;
        }

        /// <summary>
        /// 상대 클래스가 내 클래스보다 빠른가: +1 빠른 클래스, -1 느린 클래스, 0 같은 클래스·모름
        /// (vehicle_class.class_relation). 하나라도 계열을 모르면 관측 페이스로만 가른다.
        /// </summary>
        public static int Relation(string mine, string theirs, Dictionary<string, double> pace)
        {
            if (SameClass(mine, theirs))
                return 0;
            int rm = Rank(mine), rt = Rank(theirs);
            if (rm > 0 && rt > 0)
                return rt > rm ? 1 : (rt < rm ? -1 : 0);
            double pm = 0.0, pt = 0.0;
            if (pace != null)
            {
                pace.TryGetValue(mine ?? "", out pm);
                pace.TryGetValue(theirs ?? "", out pt);
            }
            if (pm > 0 && pt > 0)
            {
                if (pt <= pm * (1.0 - PaceMargin))
                    return 1;
                if (pm <= pt * (1.0 - PaceMargin))
                    return -1;
            }
            return 0;
        }
    }
}
