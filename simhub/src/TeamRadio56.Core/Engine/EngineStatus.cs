using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace TeamRadio56.Core.Engine
{
    /// <summary>
    /// 파이썬 엔진이 1초마다 쓰는 상태 파일을 읽는다.
    ///
    /// 형식은 설정 파일과 같은 key=value — JSON 파서 의존을 만들지 않기 위함.
    /// 엔진이 원자적으로 교체 저장하므로 반쯤 쓰인 파일을 읽을 일은 없다.
    /// </summary>
    public sealed class EngineStatus
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private DateTime _lastWrite = DateTime.MinValue;

        public static string DefaultPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir ?? ".", "teamradio56.status.txt");
            }
            catch (Exception)
            {
                return "teamradio56.status.txt";
            }
        }

        /// <summary>엔진이 살아 있고 최근에 갱신했는가 (5초 이내).</summary>
        public bool IsFresh { get; private set; }

        public string Get(string key)
        {
            string value;
            return _values.TryGetValue(key, out value) ? value : null;
        }

        public bool GetBool(string key)
        {
            string v = Get(key);
            return v != null && v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>파일이 갱신됐으면 다시 읽는다.</summary>
        public void Refresh(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    IsFresh = false;
                    return;
                }
                DateTime written = File.GetLastWriteTimeUtc(path);
                IsFresh = (DateTime.UtcNow - written).TotalSeconds <= 5.0;
                if (written == _lastWrite)
                    return;
                _lastWrite = written;

                var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                        continue;
                    int eq = trimmed.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    parsed[trimmed.Substring(0, eq).Trim()] = trimmed.Substring(eq + 1).Trim();
                }
                _values.Clear();
                foreach (KeyValuePair<string, string> kv in parsed)
                    _values[kv.Key] = kv.Value;
            }
            catch (IOException)
            {
                // 엔진이 교체 저장 중 — 다음 주기에 다시
            }
            catch (Exception)
            {
                IsFresh = false;
            }
        }

        /// <summary>설정 화면에 보여줄 한 줄 요약.</summary>
        public string Summary()
        {
            if (!IsFresh)
                return null;
            string state = Get("state") ?? "";
            if (!GetBool("connected"))
                return state;

            var sb = new StringBuilder();
            Append(sb, Get("track"));
            Append(sb, Get("phase"));
            string cls = Get("cls");
            string place = Get("class_place");
            if (!string.IsNullOrEmpty(place))
                Append(sb, "클래스 P" + place + (string.IsNullOrEmpty(cls) ? "" : " " + cls));
            string lap = Get("lap");
            if (!string.IsNullOrEmpty(lap))
                Append(sb, "랩 " + lap);
            string fuel = Get("fuel");
            if (!string.IsNullOrEmpty(fuel))
                Append(sb, "연료 " + fuel + "L");
            string speed = Get("speed");
            if (!string.IsNullOrEmpty(speed))
                Append(sb, speed + "km/h");
            return sb.Length > 0 ? sb.ToString() : state;
        }

        /// <summary>엔진이 최근에 말한 무전 (최대 5줄).</summary>
        public string[] RecentCalls()
        {
            var calls = new List<string>();
            for (int i = 1; i <= 5; i++)
            {
                string line = Get("call" + i);
                if (!string.IsNullOrEmpty(line))
                    calls.Add(line);
            }
            return calls.ToArray();
        }

        /// <summary>최근 무전 + 말하지 않았는지 (call{i}_dropped).</summary>
        public Logic.RecentCall[] RecentCallItems()
        {
            var calls = new List<Logic.RecentCall>();
            for (int i = 1; i <= 5; i++)
            {
                string line = Get("call" + i);
                if (!string.IsNullOrEmpty(line))
                    calls.Add(Logic.RecentCall.Parse(line, GetBool("call" + i + "_dropped")));
            }
            return calls.ToArray();
        }

        // -- 한 목소리 정책 (파이썬 main._publish_status) -------------------------

        /// <summary>엔진의 세션 목소리 (kokoro/edge/elevenlabs). 아직 안 썼으면 null.</summary>
        public string Voice { get { return Get("voice"); } }

        public string VoiceNote
        {
            get
            {
                string v = Get("voice_note");
                return string.IsNullOrEmpty(v) ? null : v;
            }
        }

        public int VoiceDropped { get { return GetInt("voice_dropped", 0); } }

        /// <summary>팩에 없는 멘트 수 (-1 = 해당 없음/모름).</summary>
        public int VoiceMissing { get { return GetInt("voice_missing", -1); } }

        public bool? LlmOn { get { return GetOptBool("llm"); } }

        /// <summary>세션 목소리가 LLM 임의 문장을 말할 수 있는가.</summary>
        public bool? LlmVoice { get { return GetOptBool("llm_voice"); } }

        public int GetInt(string key, int fallback)
        {
            int v;
            return int.TryParse(Get(key), System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out v)
                ? v : fallback;
        }

        private bool? GetOptBool(string key)
        {
            string v = Get(key);
            if (v == null)
                return null;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static void Append(StringBuilder sb, string part)
        {
            if (string.IsNullOrEmpty(part))
                return;
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append(part);
        }
    }
}
