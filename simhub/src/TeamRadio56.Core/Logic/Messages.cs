using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// messages.py 포팅 — 발화 문장 테이블(en)과 슬롯 헬퍼.
    /// 멘트 풀(PhraseLines) 밖에서 분석기/브리핑/렌더러가 직접 조립하는
    /// 모든 문장이 여기를 거친다. 플러그인 무전은 영어 고정 — 한국어 테이블은
    /// 파이썬 단독 앱(messages.py)에만 있다.
    ///
    /// 규칙 (messages.py와 동일):
    ///   - 템플릿은 파이썬 포맷 자리표를 그대로 쓴다 ({mm:.0f} 등) —
    ///     테이블을 messages.py와 눈으로 diff할 수 있게 하기 위함.
    ///   - 리플레이 회귀가 en 문장을 파이썬과 대조하므로 문장을 바꾸면
    ///     messages.py와 함께 바꿔야 한다 (그리고 음성 팩을 다시 렌더한다).
    ///   - 표시용 헬퍼(WheelDisplay 등)는 en에선 그대로 돌려주지만, 분석기 호출부가
    ///     messages.py와 1:1로 보이게 남겨 둔다.
    /// </summary>
    public static class Messages
    {

        public static string Get(string key)
        {
            return Template(key);
        }

        /// <summary>Get("wing_down", "mm", 12.0) — 이름/값 쌍.</summary>
        public static string Get(string key, params object[] nameValuePairs)
        {
            return Format(Template(key), nameValuePairs);
        }

        private static string Template(string key)
        {
            return En[key];
        }

        /// <summary>
        /// 파이썬 str.format의 부분 구현 — {name} / {name:.0f} / {name:.1f}.
        /// </summary>
        internal static string Format(string template, object[] nameValuePairs)
        {
            var sb = new StringBuilder(template.Length + 16);
            for (int i = 0; i < template.Length; i++)
            {
                char c = template[i];
                if (c != '{')
                {
                    sb.Append(c);
                    continue;
                }
                int end = template.IndexOf('}', i + 1);
                if (end < 0)
                {
                    sb.Append(template, i, template.Length - i);
                    break;
                }
                string field = template.Substring(i + 1, end - i - 1);
                i = end;
                string name = field;
                string spec = null;
                int colon = field.IndexOf(':');
                if (colon >= 0)
                {
                    name = field.Substring(0, colon);
                    spec = field.Substring(colon + 1);
                }
                object value = null;
                bool found = false;
                for (int j = 0; j + 1 < nameValuePairs.Length; j += 2)
                {
                    if (string.Equals((string)nameValuePairs[j], name, StringComparison.Ordinal))
                    {
                        value = nameValuePairs[j + 1];
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    sb.Append('{').Append(field).Append('}');
                    continue;
                }
                sb.Append(FormatValue(value, spec));
            }
            return sb.ToString();
        }

        private static string FormatValue(object value, string spec)
        {
            if (value == null)
                return "";
            if (spec == ".0f")
                return PyFixed(Convert.ToDouble(value, CultureInfo.InvariantCulture), 0);
            if (spec == ".1f")
                return PyFixed(Convert.ToDouble(value, CultureInfo.InvariantCulture), 1);
            if (value is double || value is float)
            {
                // 파이썬 str(float) 유사 — 정수값이면 "2.0"
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                    return ((long)d).ToString(CultureInfo.InvariantCulture) + ".0";
                return d.ToString("R", CultureInfo.InvariantCulture);
            }
            if (value is IFormattable f)
                return f.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }

        /// <summary>
        /// 파이썬 f"{v:.Nf}"와 바이트 단위로 같은 고정소수점 표기.
        ///
        /// double의 정확한 이진값을 기준으로 반올림하고, 정확히 한가운데면
        /// 짝수 쪽으로 간다(파이썬 규칙). .NET의 ToString("F1")은 런타임마다
        /// 다르게 반올림한다 — 특히 SimHub의 .NET Framework 4.8은 15자리로
        /// 먼저 자른 뒤 반올림해서 0.15를 "0.2"로 쓰지만 파이썬은 "0.1"이다
        /// (0.15의 실제 이진값은 0.1499…). 그러면 사전 생성된 문장과 다른
        /// 숫자를 말하게 되므로, 런타임 표기에 기대지 않고 직접 계산한다.
        /// </summary>
        public static string PyFixed(double v, int digits)
        {
            if (double.IsNaN(v))
                return "nan";
            if (double.IsInfinity(v))
                return v > 0 ? "inf" : "-inf";
            long bits = BitConverter.DoubleToInt64Bits(v);
            bool negative = bits < 0;
            int exp = (int)((bits >> 52) & 0x7FF);
            long frac = bits & 0xFFFFFFFFFFFFFL;
            if (exp == 0)
                exp = 1;                 // 비정규화 수
            else
                frac |= 1L << 52;
            exp -= 1075;                 // |v| = frac × 2^exp (정확히)

            System.Numerics.BigInteger num = frac;
            System.Numerics.BigInteger den = System.Numerics.BigInteger.One;
            if (exp >= 0)
                num <<= exp;
            else
                den <<= -exp;
            num *= System.Numerics.BigInteger.Pow(10, digits);
            System.Numerics.BigInteger rem;
            System.Numerics.BigInteger q = System.Numerics.BigInteger.DivRem(num, den, out rem);
            int cmp = (rem * 2).CompareTo(den);
            if (cmp > 0 || (cmp == 0 && !q.IsEven))
                q += 1;

            string text = q.ToString(CultureInfo.InvariantCulture);
            if (digits > 0)
            {
                text = text.PadLeft(digits + 1, '0');
                text = text.Substring(0, text.Length - digits) + "."
                       + text.Substring(text.Length - digits);
            }
            // 파이썬은 음수가 0으로 반올림돼도 부호를 남긴다 ("-0.0")
            return negative ? "-" + text : text;
        }

        // -- 슬롯/이름 헬퍼 (messages.py 미러) --------------------------------

        private static readonly string[] WheelsEn =
            { "front left", "front right", "rear left", "rear right" };

        // 순서 중요 — 파이썬 dict 순회 순서 그대로 (부분 문자열 매칭)
        private static readonly KeyValuePair<string, string>[] ClassEn =
        {
            new KeyValuePair<string, string>("hypercar", "Hypercar"),
            new KeyValuePair<string, string>("lmh", "Hypercar"),
            new KeyValuePair<string, string>("lmdh", "Hypercar"),
            new KeyValuePair<string, string>("lmp2", "LMP2"),
            new KeyValuePair<string, string>("lmgt3", "GT3"),
            new KeyValuePair<string, string>("gt3", "GT3"),
            new KeyValuePair<string, string>("gte", "GTE"),
        };

        public static string WheelName(int i)
        {
            return WheelsEn[i];
        }

        /// <summary>내부(영문) 휠 이름 → 표시 이름 (en은 그대로). 데이터엔 항상 영문 저장.</summary>
        public static string WheelDisplay(string nameEn)
        {
            return nameEn;
        }

        public static string ZoneDisplay(string zone)
        {
            return zone;
        }

        public static string ClassDisplay(string cls)
        {
            string c = (cls ?? "").ToLowerInvariant();
            foreach (KeyValuePair<string, string> entry in ClassEn)
            {
                if (c.Contains(entry.Key))
                    return entry.Value;
            }
            return "faster car";
        }

        /// <summary>traffic_approach 풀의 {gap} 슬롯 — "N second(s)".</summary>
        public static string GapSlot(int gapSec)
        {
            return gapSec + " second" + (gapSec > 1 ? "s" : "");
        }

        /// <summary>side_clear 풀의 {side} 슬롯.</summary>
        public static string SideSlot(string side)
        {
            return side;
        }

        public static string PenaltyKindDisplay(string kind)
        {
            return kind;
        }

        public static string PenaltyReasonDisplay(string reason)
        {
            return reason;
        }

        /// <summary>messages.py laps_text — "1 lap"/"3 laps".</summary>
        public static string LapsText(double n)
        {
            int i = (int)Math.Round(n, MidpointRounding.ToEven);
            return i + " lap" + (i != 1 ? "s" : "");
        }

        /// <summary>연료 풀의 {fuel_laps} 슬롯 — 단위 포함.</summary>
        public static string FuelSlot(int n)
        {
            return LapsText(n);
        }

        /// <summary>messages.py seconds_text — "2.4 seconds"/"1 second".</summary>
        public static string SecondsText(double sec)
        {
            string v = sec < 10 ? PyFixed(sec, 1) : PyFixed(sec, 0);
            if (v.EndsWith(".0"))
                v = v.Substring(0, v.Length - 2);
            return v == "1" ? "1 second" : v + " seconds";
        }

        /// <summary>
        /// messages.py fmt_laptime — "2 oh 1.8". 0.1초로 먼저 반올림한 뒤
        /// 분/초로 나눈다 (59.96초가 "1 60.0"이 되지 않게).
        /// </summary>
        public static string FmtLaptime(double sec)
        {
            int t10 = LaptimeT10(sec);
            int m = FloorDiv(t10, 600);
            int r = t10 - m * 600;
            string sTxt = (r / 10).ToString(CultureInfo.InvariantCulture) + "."
                          + (r % 10).ToString(CultureInfo.InvariantCulture);
            string mTxt = m.ToString(CultureInfo.InvariantCulture);
            if (m >= 1)
            {
                // "2 01.8"은 TTS가 201.8로 읽는다 — 엔지니어식 "two oh one point eight"
                return mTxt + " " + (r < 100 ? "oh " : "") + sTxt;
            }
            return sTxt;
        }

        /// <summary>랩타임을 0.1초 정수로 — 파이썬 int(f"{sec:.1f}".replace(".", "")).</summary>
        public static int LaptimeT10(double sec)
        {
            return int.Parse(PyFixed(sec, 1).Replace(".", ""), CultureInfo.InvariantCulture);
        }

        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
        }

        // -- 문장 조립 헬퍼 (messages.py 1:1 미러) ----------------------------

        private static readonly Regex SentSplit = new Regex(@"(?<=[.!?]) +");

        /// <summary>messages.py split_sentences — 소수점("7.8")은 갈라지지 않는다.</summary>
        public static List<string> SplitSentences(string text)
        {
            var result = new List<string>();
            foreach (string s in SentSplit.Split(text.Trim()))
            {
                if (s.Length > 0)
                    result.Add(s);
            }
            return result;
        }

        /// <summary>messages.py and_list — "a and b"/"a, b and c".</summary>
        public static string AndList(IList<string> items)
        {
            if (items.Count < 2)
                return string.Join("/", items);
            var head = new List<string>(items);
            head.RemoveAt(head.Count - 1);
            return string.Join(", ", head) + " and " + items[items.Count - 1];
        }

        /// <summary>messages.py join_problems — 문제당 한 문장(첫 글자 대문자).</summary>
        public static string JoinProblems(IList<string> problems)
        {
            var parts = new List<string>(problems.Count);
            foreach (string p in problems)
            {
                parts.Add(p.Length > 0
                    ? char.ToUpperInvariant(p[0]) + p.Substring(1) : p);
            }
            return string.Join(". ", parts);
        }

        /// <summary>messages.py multi_names — (표시 이름, 대수) 쌍, 순서는 호출자가 정한다.</summary>
        public static string MultiNames(IList<KeyValuePair<string, int>> pairs)
        {
            var parts = new List<string>(pairs.Count);
            foreach (KeyValuePair<string, int> pair in pairs)
            {
                string name = pair.Key;
                int n = pair.Value;
                if (n == 1)
                    parts.Add(Get("multi_one", "name", name));
                else if (n == 2)
                    parts.Add(Get("multi_two", "name", name));
                else
                    parts.Add(Get("multi_n", "name", name, "n", n));
            }
            return string.Join(", ", parts);
        }

        /// <summary>messages.py penalty_text — kind/reason은 내부(영문) 값.</summary>
        public static string PenaltyText(string kind, string reason)
        {
            string reasonD = !string.IsNullOrEmpty(reason)
                ? PenaltyReasonDisplay(reason) : "";
            string head = Get("penalty_head", "kind", PenaltyKindDisplay(kind))
                + (!string.IsNullOrEmpty(reasonD) ? ", " + reasonD : "");
            string advice;
            switch (kind)
            {
                case "drive-through":
                case "stop-and-go":
                case "time penalty":
                    advice = Get("penalty_advice_" + kind);
                    break;
                default:
                    advice = Get("penalty_advice_default");
                    break;
            }
            return head + ". " + advice;
        }

        /// <summary>
        /// messages.py brief_length_text — 세션 길이 슬롯. 1시간 이상은 정수
        /// 연산으로 반올림 (60~179분은 5분, 180분 이상은 30분 단위).
        /// </summary>
        public static string BriefLengthText(int minutes)
        {
            int m = minutes;
            if (m >= 180)
                m = (m + 15) / 30 * 30;
            else if (m >= 60)
                m = (m + 2) / 5 * 5;
            if (m == 1)
                return Get("brief_minute");
            if (m >= 60 && m % 60 == 0)
            {
                int h = m / 60;
                return Get(h > 1 ? "brief_hours_plural" : "brief_hours", "h", h);
            }
            if (m >= 180 && m % 60 == 30)
                return Get("brief_hours_half", "h", m / 60);
            return Get("brief_minutes", "m", m);
        }

        // -- 결정적 변형 (messages.py variant/fnv1a32 미러) -------------------
        // 문장 슬롯마다 변형 여러 개 — 랜덤이 아니라 스냅샷 값 시드의 FNV-1a 해시로 고른다.
        // 파이썬과 같은 문장을 골라야 리플레이가 브리핑을 문장 단위로 대조할 수 있다.

        // 정적 필드는 선언 순서대로 초기화된다 — 아래 VariantTable보다 먼저 만들지 않게 지연 생성
        private static Dictionary<string, string[]> _variants;

        private static Dictionary<string, string[]> Variants
        {
            get { return _variants ?? (_variants = BuildVariants()); }
        }

        private static Dictionary<string, string[]> BuildVariants()
        {
            var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string[]> kv in VariantTable)
                d.Add(kv.Key, kv.Value);
            return d;
        }

        /// <summary>변형 그룹 키 전체 (정의 순서) — 사전 생성 열거용 (messages.variant_keys).</summary>
        public static IEnumerable<string> VariantKeys()
        {
            foreach (KeyValuePair<string, string[]> kv in VariantTable)
                yield return kv.Key;
        }

        /// <summary>그룹의 변형 템플릿 (messages.variants).</summary>
        public static string[] VariantTemplates(string key)
        {
            return Variants[key];
        }

        /// <summary>FNV-1a 32비트 (UTF-8 바이트) — messages.fnv1a32와 같은 값.</summary>
        public static uint Fnv1a32(string text)
        {
            uint h = 2166136261u;
            foreach (byte b in Encoding.UTF8.GetBytes(text ?? ""))
            {
                h ^= b;
                h = unchecked(h * 16777619u);
            }
            return h;
        }

        /// <summary>
        /// 시드로 고른 변형 번호 — 슬롯(그룹 키)마다 따로 해시 (messages.variant_index).
        /// 해시의 상위 비트로 줄인다 ((h × n) >> 32, Lemire) — `h % n`이면 FNV-1a 하위 비트가
        /// 입력 하위 비트로만 정해져 변형 2·4개짜리 슬롯끼리 함께 움직인다.
        /// </summary>
        public static int VariantIndex(string key, string seed)
        {
            ulong h = Fnv1a32(seed + "|" + key);
            return (int)((h * (ulong)Variants[key].Length) >> 32);
        }

        /// <summary>Variant("brief_temp", seed, "t", 31.0) — 이름/값 쌍 (messages.variant).</summary>
        public static string Variant(string key, string seed, params object[] nameValuePairs)
        {
            return Format(Variants[key][VariantIndex(key, seed)], nameValuePairs);
        }

        /// <summary>레이스 요약의 {nth} 슬롯 — 2·3만 말한다 (messages.ordinal_word).</summary>
        public static string OrdinalWord(int n)
        {
            return n == 2 ? "second" : n == 3 ? "third" : "";
        }

        // -- 결정적 변형 (messages.py _V["en"] 미러, 정의 순서 그대로) ------------

        private static readonly KeyValuePair<string, string[]>[] VariantTable =
        {
            new KeyValuePair<string, string[]>("brief_open_practice", new[]
            {
                "Radio check. Practice session, we're all set.",
                "Radio check, one two. This is practice.",
                "Radio check. Practice is green, the car's yours.",
                "Radio check. We're in practice.",
                "Radio check. Practice, let's get some laps in.",
            }),
            new KeyValuePair<string, string[]>("brief_open_practice_mid", new[]
            {
                "Radio check. Practice is running, I'm with you.",
                "Radio check. Picking you up mid-session.",
                "Radio check, one two. Practice still live.",
            }),
            new KeyValuePair<string, string[]>("brief_open_quali", new[]
            {
                "Radio check. Qualifying session.",
                "Radio check. Qualifying, here we go.",
                "Radio check, one two. This is qualifying.",
                "Radio check. Qualifying, time to show the pace.",
                "Radio check. Qualifying is live.",
            }),
            new KeyValuePair<string, string[]>("brief_open_quali_mid", new[]
            {
                "Radio check. Qualifying is running, I'm with you.",
                "Radio check. Picking you up mid-qualifying.",
                "Radio check, one two. Qualifying still live.",
            }),
            new KeyValuePair<string, string[]>("brief_open_warmup", new[]
            {
                "Radio check. Warm-up session.",
                "Radio check. Warm-up, last look before the race.",
                "Radio check, one two. This is warm-up.",
                "Radio check. Short warm-up, nothing fancy.",
            }),
            new KeyValuePair<string, string[]>("brief_open_warmup_mid", new[]
            {
                "Radio check. Warm-up is running, I'm with you.",
                "Radio check, one two. Warm-up still live.",
                "Radio check. Picking you up in warm-up.",
            }),
            new KeyValuePair<string, string[]>("brief_open_race", new[]
            {
                "Radio check. Race session.",
                "Radio check, one two. This is the race.",
                "Radio check. Race day, here we go.",
                "Radio check. Race time.",
                "Radio check. Race on, I'm with you all the way.",
            }),
            new KeyValuePair<string, string[]>("brief_open_race_mid", new[]
            {
                "Radio check. Race is on, I'm with you.",
                "Radio check. Picking you up mid-race.",
                "Radio check, one two. Race still running.",
                "Radio check. Back on the radio, race is live.",
            }),
            new KeyValuePair<string, string[]>("brief_len_long", new[]
            {
                "{length} long.",
                "{length} on the clock.",
            }),
            new KeyValuePair<string, string[]>("brief_len_remaining", new[]
            {
                "{length} remaining.",
                "{length} to go.",
            }),
            new KeyValuePair<string, string[]>("brief_grid", new[]
            {
                "We start P{cp}.",
                "Starting P{cp}.",
            }),
            new KeyValuePair<string, string[]>("brief_grid_mid", new[]
            {
                "We're P{cp}.",
                "Running P{cp}.",
            }),
            new KeyValuePair<string, string[]>("brief_temp", new[]
            {
                "Track {t:.0f} degrees.",
                "Track temp {t:.0f}.",
                "Track's at {t:.0f} degrees.",
            }),
            new KeyValuePair<string, string[]>("brief_fuel", new[]
            {
                "Fuel {f:.0f} litres.",
                "{f:.0f} litres on board.",
            }),
            new KeyValuePair<string, string[]>("brief_rain", new[]
            {
                "It's raining, watch the grip.",
                "Wet out there, grip will be low.",
                "Rain falling, be gentle early on.",
            }),
            new KeyValuePair<string, string[]>("brief_plan_practice", new[]
            {
                "Build it up over a few laps, no need to push yet.",
                "Bring the tyres in gently, then we start the run.",
                "Install lap first, then build the pace.",
                "Settle in, find a rhythm, then tell me about the balance.",
                "Easy first lap, then we go to work.",
            }),
            new KeyValuePair<string, string[]>("brief_gap_quali", new[]
            {
                "Find a gap on the out lap, we want clear air.",
                "Use the out lap to make some space.",
                "Clear air is everything. Back off if you need to.",
                "Don't start a lap stuck behind someone. Make a gap first.",
                "Out lap is about space and tyre temperature.",
            }),
            new KeyValuePair<string, string[]>("brief_limits_quali", new[]
            {
                "Mind the track limits, we can't afford a deleted lap.",
                "Four wheels inside the lines, no deleted laps.",
                "Track limits are being watched. Keep it tidy.",
                "Stay inside the white lines, a deleted lap costs us everything.",
            }),
            new KeyValuePair<string, string[]>("brief_close_quali", new[]
            {
                "Make it count.",
                "One clean lap, that's all we need.",
                "Go and get it.",
                "Clean and committed. Off you go.",
                "Deep breath, then go for it.",
            }),
            new KeyValuePair<string, string[]>("brief_plan_warmup", new[]
            {
                "Just check the car. Brakes, tyres, balance.",
                "Systems check before the race, no heroics.",
                "Feel for the car, save the effort for the race.",
                "Couple of laps to make sure everything's working.",
            }),
            new KeyValuePair<string, string[]>("brief_close_out", new[]
            {
                "Out when you're ready.",
                "Pit exit is yours whenever you're ready.",
                "Off you go when you're happy.",
                "Car's ready when you are.",
                "Head out when you're set.",
            }),
            new KeyValuePair<string, string[]>("brief_formation", new[]
            {
                "Formation lap, get heat in the tyres and brakes. Hold your slot, I'll call the green.",
                "On the formation lap, keep the tyres and brakes warm. Stay in your slot, the green comes from me.",
                "Formation lap. Temperature in the tyres, hold position. I'll call the green.",
                "Rolling out for the formation lap. Brakes and tyres up to temp, hold your place. I'll call it when it's green.",
                "Formation lap, warm everything up. Keep your slot, I'll call the green.",
            }),
            new KeyValuePair<string, string[]>("brief_start_spotter", new[]
            {
                "Through the start it's left and right calls only.",
                "Into turn one I'll give you side calls only.",
                "At the start, just left and right from me.",
            }),
            new KeyValuePair<string, string[]>("brief_close_race", new[]
            {
                "Calm first lap.",
                "Stay out of trouble in turn one.",
                "Keep your nose clean on lap one.",
                "Patience on the opening lap.",
                "No heroes into turn one.",
            }),
            new KeyValuePair<string, string[]>("brief_close_mid", new[]
            {
                "Carry on.",
                "Carry on with the plan.",
                "Keep doing what you're doing.",
                "Head down, carry on.",
            }),
        };

        // -- 문장 테이블 (messages.py _T 미러) --------------------------------

        private static readonly Dictionary<string, string> En =
            new Dictionary<string, string>
        {
            // traffic
            { "stopped_hazard", "Stopped car ahead. Change your line early." },
            { "multi_alongside_left", "{cls} on your left" },
            { "multi_alongside_right", "{cls} on your right" },
            { "multi_alongside", "{cls} alongside" },
            { "multi_behind", "{names} behind" },
            { "multi_closing", "{names} closing" },
            { "multi_ahead_clear", " Ahead is clear." },
            // 한 절에 4대 이상 — 대수 조합을 다 사전 생성하지 않고 묶어 말한다
            { "multi_behind_many", "Several cars behind" },
            { "multi_closing_many", "Several cars closing" },
            { "multi_one", "one {name}" },
            { "multi_two", "two {name}s" },
            { "multi_n", "{n} {name}s" },
            // racecontrol
            { "sector_yellow", "Yellow in sector {n}. No overtaking, be ready to lift." },
            { "pen_clear", "Penalty served. Back to your race." },
            { "penalty_head", "Penalty — {kind}" },
            { "penalty_advice_drive-through", "Serve it next lap. Mind the limiter." },
            { "penalty_advice_stop-and-go", "Hold the stop time in the box. Stay calm." },
            { "penalty_advice_time penalty", "Added to the result. We claw it back on pace." },
            { "penalty_advice_default", "I'll call the timing." },
            // health
            { "wheel_gone", "{wheel} wheel is gone! Careful, bring it to the pits slowly." },
            { "part_detached_rear", "Bodywork gone at the rear. Could be the wing. "
                                    + "Careful next corner. If the rear goes, box." },
            { "part_detached", "Bodywork detached. Possible aero loss. Checking data." },
            { "wing_down", "Front aero down {mm:.0f} millimetres. Splitter damage. "
                           + "Careful in the fast stuff. Repair call on pace." },
            { "align_severe", "Alignment is badly out. You're steering on the straights. "
                              + "Box for repairs." },
            { "align_mild", "Steering pull on the straights. Alignment's off from that hit. "
                            + "It'll eat the tyre. Repair call on pace." },
            { "rear_instab", "Rear keeps stepping out. Looks damage-related. "
                             + "Don't push, recommend box." },
            { "slow_puncture", "{wheel} losing pressure. Slow puncture. We change it next stop." },
            { "engine_warn", "{what} climbing. Get out of the slipstream, give it air." },
            { "engine_what_water", "Water temp" },
            { "engine_what_oil", "Oil temp" },
            { "engine_what_engine", "Engine temp" },
            { "brake_warn", "Brakes averaging {t:.0f} degrees. Brake a touch earlier, cool them." },
            { "repair_cost", "Damage is costing {delta:.1f} a lap. "
                             + "We repair at the next stop. It pays off." },
            { "repair_cost_big", "Damage is costing a lot of time. "
                                 + "We repair at the next stop. It pays off." },
            { "dmg_no_effect", "That contact — no effect on pace. Forget it." },
            { "report_problems", "Check done. {problems}. {advice}" },
            { "report_advice_box", "Prepare to box." },
            { "report_advice_keep", "Keep pace, I'll make the repair call." },
            { "report_marks", "Check done. Just marks. Wheels, tyres, pressures all fine. Carry on." },
            { "report_clean", "Check done. No damage, car is clean. Carry on." },
            { "item_wheel_damage", "{names} wheel damage" },
            { "item_puncture", "{names} puncture" },
            { "item_losing_pressure", "{name} losing pressure" },
            { "item_heavy_body", "heavy bodywork damage, {zones}" },
            { "item_heavy_body_all", "heavy bodywork damage all round" },
            // tyres
            { "puncture_now", "Puncture, {wheel}! Box now, take it easy." },
            // fuel
            { "fuel_save", "No-stop target {target:.1f} litres a lap. "
                           + "Save {delta:.1f} — lift and coast, short shift." },
            // reporter
            { "last_lap_report", "Last lap {t}." },
            { "best_lap_suffix", " Best lap." },
            { "status_pos", "P{p}." },
            // 갭은 문장 하나씩 — 1단계 갭 문장("Gap ahead 3.2 seconds.")을 그대로 재사용
            { "status_gap_ahead", "Gap ahead {gs}" },
            { "status_gap_behind", "Gap behind {gs}" },
            { "status_gaps", "{gaps}." },
            { "status_gap_join", ". " },
            { "status_fuel", "Fuel {n}." },
            { "status_tyres", "Tyres {n}." },
            { "status_tyres_good", "Tyres good." },
            // 세션 브리핑 (SessionBriefer) — 문장 슬롯마다 변형은 VariantTable, 여기는 변형 없는 조각만
            { "brief_hours", "{h} hour" },
            { "brief_hours_plural", "{h} hours" },
            { "brief_hours_half", "{h} and a half hours" },
            { "brief_minute", "1 minute" },
            { "brief_minutes", "{m} minutes" },
            { "brief_lap", "1 lap." },
            { "brief_laps", "{n} laps." },
            { "brief_cars", "{n} cars in class." },
            // 렌더러 폴백
            { "milestone_hours", "{h} hour{s} to go. On plan." },
            { "milestone_minutes", "{m} minutes to go. Rechecking fuel and tyres." },
            { "pit_window", "Pit window open. Box within {n}." },
            { "stint_brief", "New stint. Easy on the tyres first lap, find the rhythm." },
            { "rival_pit_base", "P{p} in class, car {rel}, just pitted." },
            { "rival_rel_ahead", "ahead" }, { "rival_rel_behind", "behind" },
            { "rival_pit_undercut", " Undercut attempt. I'll look at our timing." },
            { "rival_pit_gap", " I'll call the gap when he's out." },
            { "rival_catch", "Car ahead in class is {diff:.1f} a lap slower. "
                             + "We catch him in {laps}." },
            { "rival_defend", "Car behind in class is {diff:.1f} a lap quicker. "
                              + "With us in {laps}. Be ready." },
            { "tyre_hot", "{wheel} tyre running hot. Up {delta:.0f} degrees. Ease that side." },
            { "tyre_wear", "{wheel} tyre going off. About {laps} left. Factoring it in." },
            { "pace_lost", "Lost {delta:.1f} on that lap. Checking why." },
            { "pace_quick", "{delta:.1f} quicker. Keep the rhythm." },
            // 갭 문장은 "Gap behind/ahead {gap}." 한 문장을 여섯 변형이 공유한다 —
            // 문장 단위 사전 생성(슬롯 하나당 한 문장)을 위해 쉼표로 잇지 말 것
            { "gap_behind_closing", "Gap behind {gap}. Closing {rate:.1f} a lap. "
                                    + "Just no mistakes." },
            { "gap_behind_opening", "Gap behind {gap}. Opening up. Good." },
            { "gap_behind_holding", "Gap behind {gap}. Holding steady. Rhythm's good." },
            { "gap_ahead_closing", "Gap ahead {gap}. Closing {rate:.1f} a lap. We can get him." },
            { "gap_ahead_opening", "Gap ahead {gap}. Opening up. Don't overdo it." },
            { "gap_ahead_holding", "Gap ahead {gap}. Holding steady. Keep the rhythm." },
            // 트레이닝 섹터 피드백 (파이썬 training.LapCoach 전용) — en 전용, 문장 집합 대조용 미러
            { "coach_quicker_best", "Sector {n}, {d:.1f} quicker, session best. Keep that feel." },
            { "coach_quicker", "Sector {n}, {d:.1f} quicker. Keep that feel." },
            { "coach_losing", "Losing {d:.1f} in sector {n}. Get it back next lap." },
        };
    }
}
