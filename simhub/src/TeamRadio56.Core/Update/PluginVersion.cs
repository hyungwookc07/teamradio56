using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TeamRadio56.Core.Update
{
    /// <summary>
    /// 숫자 x.y.z 버전 비교. 플러그인 버전 문자열("0.15.0-kokoro")은 숫자 접두부만 본다.
    /// </summary>
    public static class PluginVersion
    {
        private static readonly Regex Strict = new Regex(
            @"^\d{1,9}\.\d{1,9}\.\d{1,9}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// 숫자 접두부 [major, minor, patch] — 빠진 자리는 0, 넷째 자리부터와 접미사는 무시.
        /// 숫자로 시작하지 않거나 자리가 int 범위를 넘으면 null.
        /// </summary>
        public static int[] ParseNumeric(string s)
        {
            if (s == null)
                return null;
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;
            if (i < s.Length && (s[i] == 'v' || s[i] == 'V'))
                i++;
            var parts = new int[3];
            for (int k = 0; k < 3; k++)
            {
                int start = i;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                    i++;
                int len = i - start;
                if (len == 0)
                {
                    if (k == 0)
                        return null;
                    break;
                }
                if (len > 9)
                    return null;
                parts[k] = int.Parse(s.Substring(start, len), NumberStyles.None,
                                     CultureInfo.InvariantCulture);
                if (k == 2 || i + 1 >= s.Length || s[i] != '.'
                    || s[i + 1] < '0' || s[i + 1] > '9')
                    break;
                i++;
            }
            return parts;
        }

        /// <summary>매니페스트용 엄격 형식 (정확히 x.y.z).</summary>
        public static bool IsStrict(string s) => s != null && Strict.IsMatch(s);

        /// <summary>a &lt; b → 음수, 같으면 0. 해석 불가한 쪽이 가장 낮다.</summary>
        public static int Compare(string a, string b)
        {
            int[] x = ParseNumeric(a);
            int[] y = ParseNumeric(b);
            if (x == null || y == null)
                return (x == null ? 0 : 1) - (y == null ? 0 : 1);
            for (int i = 0; i < 3; i++)
                if (x[i] != y[i])
                    return x[i] < y[i] ? -1 : 1;
            return 0;
        }

        /// <summary>latest가 current보다 확실히 새것일 때만 true (둘 중 하나라도 해석 불가면 false).</summary>
        public static bool IsNewer(string latest, string current)
        {
            if (ParseNumeric(latest) == null || ParseNumeric(current) == null)
                return false;
            return Compare(latest, current) > 0;
        }
    }
}
