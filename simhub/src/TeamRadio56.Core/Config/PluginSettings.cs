using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.Core.Config
{
    /// <summary>
    /// 사용자 설정. 파이썬 config.yaml의 UI 노출 항목을 옮긴 것.
    ///
    /// 모든 항목이 bool/int/double/string 이라 직렬화가 단순하다.
    /// (SettingsStore가 리플렉션으로 key=value 파일에 읽고 쓴다 —
    ///  외부 JSON 라이브러리 의존을 만들지 않기 위함.)
    /// </summary>
    public class PluginSettings
    {
        // -- 음성 --------------------------------------------------------
        public bool VoiceEnabled { get; set; } = true;
        public double Volume { get; set; } = 0.9;
        public string EdgeVoice { get; set; } = "en-GB-RyanNeural";
        public int SpeechRatePercent { get; set; } = 10;      // edge-tts "+10%"
        public bool RadioFx { get; set; } = true;             // 무전기 효과
        public double RadioNoise { get; set; } = 0.004;       // 배경 지직임

        // -- 수다스러움 --------------------------------------------------
        /// <summary>quiet | normal | chatty — 유형별 쿨다운에 배율로 적용.</summary>
        public string ChatterPreset { get; set; } = "normal";

        // -- 트래픽 ------------------------------------------------------
        public double AlongsideMeters { get; set; } = 4.6;    // 나란히 기준(실제 오버랩)
        public double StartSpotterSeconds { get; set; } = 45; // 스타트 스포터 모드
        public bool SideInvert { get; set; } = false;         // 좌우 콜이 반대면
        public bool TrafficRaceOnly { get; set; } = false;    // 연습/퀄리에선 끔

        // -- HUD 대체 정기 무전 -------------------------------------------
        public bool LapTimeEveryLap { get; set; } = false;
        public int StatusEveryLaps { get; set; } = 0;         // 0 = 끔

        // -- LLM ---------------------------------------------------------
        public bool LlmEnabled { get; set; } = true;
        public string LlmApiKey { get; set; } = "";           // 비우면 환경변수 사용
        public int LlmBudgetPerHour { get; set; } = 15;

        // -- 동작 --------------------------------------------------------
        public bool RequireRealtime { get; set; } = true;     // 모니터/메뉴에선 침묵
        public bool SpeechLog { get; set; } = true;

        /// <summary>수다스러움 프리셋 → 쿨다운 배율 (클수록 조용).</summary>
        public double CooldownScale
        {
            get
            {
                switch ((ChatterPreset ?? "normal").ToLowerInvariant())
                {
                    case "quiet": return 1.8;
                    case "chatty": return 0.6;
                    default: return 1.0;
                }
            }
        }

        public static readonly string[] VoiceChoices =
        {
            "en-GB-RyanNeural",       // 영국 남성 — 레이스 엔지니어 톤 (기본)
            "en-GB-ThomasNeural",     // 영국 남성, 차분함
            "en-GB-SoniaNeural",      // 영국 여성
            "en-US-GuyNeural",        // 미국 남성
            "en-US-ChristopherNeural",// 미국 남성, 저음
            "en-AU-WilliamNeural",    // 호주 남성
            "ko-KR-InJoonNeural",     // 한국어 (멘트 풀은 영어 — 참고용)
        };

        public static readonly string[] ChatterChoices = { "quiet", "normal", "chatty" };
    }

    /// <summary>
    /// 설정 파일 입출력 (DLL 옆 teamradio56.settings.txt).
    ///
    /// 외부 직렬화 라이브러리에 의존하지 않으려고 key=value 형식을 쓴다.
    /// 리플렉션으로 프로퍼티를 순회하므로 항목을 추가해도 코드 수정이 없다.
    /// </summary>
    public static class SettingsStore
    {
        private static string _path;

        public static string Path
        {
            get
            {
                if (_path == null)
                {
                    try
                    {
                        string dir = System.IO.Path.GetDirectoryName(
                            Assembly.GetExecutingAssembly().Location);
                        _path = System.IO.Path.Combine(dir ?? ".", "teamradio56.settings.txt");
                    }
                    catch (Exception)
                    {
                        _path = "teamradio56.settings.txt";
                    }
                }
                return _path;
            }
        }

        public static PluginSettings Load()
        {
            var settings = new PluginSettings();
            try
            {
                if (!File.Exists(Path))
                {
                    FileLog.Info("설정 파일 없음 — 기본값 사용 (" + Path + ")");
                    return settings;
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(Path, Encoding.UTF8))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                        continue;
                    int eq = trimmed.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    values[trimmed.Substring(0, eq).Trim()] = trimmed.Substring(eq + 1).Trim();
                }

                foreach (PropertyInfo prop in Properties())
                {
                    string raw;
                    if (!values.TryGetValue(prop.Name, out raw))
                        continue;
                    object parsed = Parse(prop.PropertyType, raw);
                    if (parsed != null)
                        prop.SetValue(settings, parsed, null);
                }
                FileLog.Info("설정 로드: " + Path);
            }
            catch (Exception ex)
            {
                FileLog.Error("설정 로드 실패 — 기본값 사용", ex);
            }
            return settings;
        }

        public static void Save(PluginSettings settings)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# teamradio56 설정 — SimHub 좌측 메뉴에서 수정하는 편이 안전합니다.");
                foreach (PropertyInfo prop in Properties())
                {
                    object value = prop.GetValue(settings, null);
                    sb.AppendLine(prop.Name + " = " + Format(value));
                }
                File.WriteAllText(Path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                FileLog.Error("설정 저장 실패", ex);
            }
        }

        private static IEnumerable<PropertyInfo> Properties()
        {
            foreach (PropertyInfo prop in typeof(PluginSettings).GetProperties())
            {
                if (!prop.CanRead || !prop.CanWrite)
                    continue;   // CooldownScale 같은 파생 값은 저장하지 않는다
                Type t = prop.PropertyType;
                if (t == typeof(bool) || t == typeof(int) || t == typeof(double) || t == typeof(string))
                    yield return prop;
            }
        }

        private static string Format(object value)
        {
            if (value == null)
                return "";
            if (value is bool)
                return ((bool)value) ? "true" : "false";
            if (value is double)
                return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is int)
                return ((int)value).ToString(CultureInfo.InvariantCulture);
            return value.ToString();
        }

        private static object Parse(Type type, string raw)
        {
            try
            {
                if (type == typeof(bool))
                    return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
                if (type == typeof(int))
                    return int.Parse(raw, CultureInfo.InvariantCulture);
                if (type == typeof(double))
                    return double.Parse(raw, CultureInfo.InvariantCulture);
                return raw;
            }
            catch (Exception)
            {
                return null;   // 잘못된 값은 기본값 유지
            }
        }
    }
}
