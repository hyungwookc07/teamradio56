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
    /// 사용자 설정 (SimHub 설정 화면에 보이는 항목).
    ///
    /// 모든 항목이 bool/int/double/string 이라 직렬화가 단순하다.
    /// (SettingsStore가 리플렉션으로 key=value 파일에 읽고 쓴다 —
    ///  외부 JSON 라이브러리 의존을 만들지 않기 위함.)
    ///
    /// 플러그인은 항상 C# 내장 엔진 + 영어 무전 + kokoro 음성 팩으로 돈다. 예전 버전이
    /// 저장한 키(EngineMode, EngineExe/Args, VoiceLanguage, VoiceEngine, EdgeVoice,
    /// SpeechRatePercent, RadioFx, RadioNoise, Volume, AllowVoiceFallback, Llm*, SpeechLog)는
    /// 읽을 때 무시되고 다음 저장 때 파일에서 빠진다.
    /// </summary>
    public class PluginSettings
    {
        // -- 언어 --------------------------------------------------------
        /// <summary>설정 화면 언어 (ko/en) — 플러그인 UI 전용. 무전은 항상 영어.</summary>
        public string UiLanguage { get; set; } = "ko";

        // -- 음성 --------------------------------------------------------
        /// <summary>끄면 무전을 소리 없이 자막·최근 무전에만 낸다.</summary>
        public bool VoiceEnabled { get; set; } = true;
        /// <summary>
        /// 음성 팩(오디오 캐시) 폴더. 비우면 자동 탐색: DLL 옆 audio_cache →
        /// teamradio56-engine\audio_cache. 어느 쪽도 없으면 DLL 옆 audio_cache
        /// ([음성 팩 업데이트]가 거기에 설치한다).
        /// </summary>
        public string AudioCacheDir { get; set; } = "";

        // -- 수다스러움 --------------------------------------------------
        /// <summary>quiet | normal | chatty — 유형별 쿨다운에 배율로 적용.</summary>
        public string ChatterPreset { get; set; } = "normal";

        // -- 트래픽 ------------------------------------------------------
        public double AlongsideMeters { get; set; } = 4.6;    // 나란히 기준(실제 오버랩)
        public double StartSpotterSeconds { get; set; } = 45; // 스타트 스포터 모드
        public bool SideInvert { get; set; } = false;         // 좌우 콜이 반대면
        public bool TrafficRaceOnly { get; set; } = true;     // 연습/퀄리는 프라이빗(유령 콜) — 기본 차단

        // -- HUD 대체 정기 무전 -------------------------------------------
        public bool LapTimeEveryLap { get; set; } = false;
        public int StatusEveryLaps { get; set; } = 0;         // 0 = 끔

        // -- 동작 --------------------------------------------------------
        public bool RequireRealtime { get; set; } = true;     // 모니터/메뉴에선 침묵

        // -- 업데이트 ----------------------------------------------------
        /// <summary>시작 시 하루 한 번 새 버전 확인 (설치는 항상 사용자가 버튼으로).</summary>
        public bool CheckUpdates { get; set; } = true;
        /// <summary>마지막 확인 시각 (UTC 왕복 형식) — 확인 주기 계산용, 화면에서 편집하지 않는다.</summary>
        public string LastUpdateCheck { get; set; } = "";

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

        public static readonly string[] ChatterChoices = { "quiet", "normal", "chatty" };

        public static readonly string[] UiLanguageChoices = { "ko", "en" };
    }

    /// <summary>
    /// 설정 파일 입출력 (DLL 옆 teamradio56.settings.txt).
    ///
    /// 외부 직렬화 라이브러리에 의존하지 않으려고 key=value 형식을 쓴다.
    /// 리플렉션으로 프로퍼티를 순회하므로 항목을 추가해도 코드 수정이 없다.
    /// 모르는 키(예전 버전의 설정)와 깨진 줄은 조용히 건너뛴다.
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
            return Load(Path);
        }

        /// <summary>지정한 파일에서 읽는다 (셀프테스트용 — 플러그인은 Load()).</summary>
        public static PluginSettings Load(string path)
        {
            var settings = new PluginSettings();
            try
            {
                if (!File.Exists(path))
                {
                    FileLog.Info("설정 파일 없음 — 기본값 사용 (" + path + ")");
                    return settings;
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
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
                FileLog.Info("설정 로드: " + path);
            }
            catch (Exception ex)
            {
                FileLog.Error("설정 로드 실패 — 기본값 사용", ex);
            }
            return settings;
        }

        public static void Save(PluginSettings settings)
        {
            Save(settings, Path);
        }

        /// <summary>지정한 파일에 쓴다 (셀프테스트용 — 플러그인은 Save(settings)).</summary>
        public static void Save(PluginSettings settings, string path)
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
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
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
