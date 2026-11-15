using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TeamRadio56.Core.Config;

namespace TeamRadio56.Replay
{
    /// <summary>
    /// --settings-selftest: 예전 버전이 쓴 teamradio56.settings.txt를 그대로 읽어도 되는지.
    /// 0.18부터 플러그인은 항상 내장 엔진·영어·kokoro 음성 팩이라 EngineMode=python,
    /// EdgeVoice, VoiceLanguage=ko 같은 키는 오류 없이 무시되고, 다음 저장 때 빠져야 한다.
    /// </summary>
    internal static class SettingsSelfTest
    {
        // 0.17.x까지 있었고 이제 없는 키 — 다시 생기면(= 기능 부활) 이 목록과 문서를 함께 고친다
        private static readonly string[] RemovedKeys =
        {
            "EngineMode", "EngineExe", "EngineArgs", "VoiceLanguage", "VoiceEngine", "Volume",
            "EdgeVoice", "SpeechRatePercent", "RadioFx", "RadioNoise", "AllowVoiceFallback",
            "LlmEnabled", "LlmApiKey", "LlmBudgetPerHour", "SpeechLog",
        };

        public static int Run()
        {
            var failures = new List<string>();
            int pass = 0;
            Action<bool, string> check = (cond, what) =>
            {
                if (cond)
                    pass++;
                else
                    failures.Add(what);
            };

            string dir = Path.Combine(Path.GetTempPath(), "teamradio56-settingstest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // 0.17.1 SettingsStore.Save가 쓴 모양 그대로 (+ 깨진 줄, 주석, 대소문자 다른 키)
                string legacy = Path.Combine(dir, "teamradio56.settings.txt");
                File.WriteAllText(legacy, string.Join("\n", new[]
                {
                    "# teamradio56 설정 — SimHub 좌측 메뉴에서 수정하는 편이 안전합니다.",
                    "EngineMode = python",
                    "EngineExe = C:\\teamradio56-engine\\teamradio56.exe",
                    "EngineArgs = \"D:\\teamradio56\\main.py\"",
                    "UiLanguage = en",
                    "VoiceLanguage = ko",
                    "VoiceEnabled = false",
                    "VoiceEngine = edge",
                    "AudioCacheDir = D:\\packs\\audio_cache",
                    "Volume = 0.5",
                    "EdgeVoice = ko-KR-InJoonNeural",
                    "SpeechRatePercent = 25",
                    "RadioFx = false",
                    "RadioNoise = 0.01",
                    "AllowVoiceFallback = true",
                    "ChatterPreset = quiet",
                    "alongsidemeters = 5.2",
                    "StatusEveryLaps = not-a-number",
                    "LlmEnabled = true",
                    "LlmApiKey = sk-dummy-for-test",
                    "LlmBudgetPerHour = 30",
                    "RequireRealtime = false",
                    "SpeechLog = false",
                    "this line has no equals sign",
                    "= value without key",
                    "",
                }), new UTF8Encoding(false));

                PluginSettings s = null;
                try
                {
                    s = SettingsStore.Load(legacy);
                }
                catch (Exception ex)
                {
                    failures.Add("옛 설정 파일 Load 예외: " + ex.GetType().Name + " — " + ex.Message);
                }
                check(s != null, "옛 설정 파일을 읽는다");
                if (s != null)
                {
                    check(s.UiLanguage == "en", "UiLanguage 유지");
                    check(!s.VoiceEnabled, "VoiceEnabled 유지");
                    check(s.AudioCacheDir == "D:\\packs\\audio_cache", "AudioCacheDir 유지");
                    check(s.ChatterPreset == "quiet" && Math.Abs(s.CooldownScale - 1.8) < 1e-9, "ChatterPreset 유지");
                    check(Math.Abs(s.AlongsideMeters - 5.2) < 1e-9, "키 대소문자 무시 (alongsidemeters)");
                    check(s.StatusEveryLaps == 0, "잘못된 값은 기본값 (StatusEveryLaps)");
                    check(!s.RequireRealtime, "RequireRealtime 유지");
                    check(s.CheckUpdates, "없는 키는 기본값 (CheckUpdates)");
                    foreach (string key in RemovedKeys)
                        check(typeof(PluginSettings).GetProperty(key) == null,
                              "PluginSettings에 " + key + " 없음 (무시되는 키)");

                    string saved = Path.Combine(dir, "saved.txt");
                    SettingsStore.Save(s, saved);
                    string text = File.Exists(saved) ? File.ReadAllText(saved, Encoding.UTF8) : "";
                    check(text.Contains("UiLanguage = en") && text.Contains("ChatterPreset = quiet"),
                          "저장 파일에 남는 키");
                    foreach (string key in RemovedKeys)
                        check(!text.Contains("\n" + key + " ="), "저장하면 " + key + " 줄이 빠진다");
                    check(!text.Contains("sk-dummy-for-test"), "옛 API 키가 저장 파일에 남지 않는다");

                    PluginSettings again = SettingsStore.Load(saved);
                    check(again.UiLanguage == "en" && !again.VoiceEnabled
                          && Math.Abs(again.AlongsideMeters - 5.2) < 1e-9, "다시 읽어도 같은 값");
                }

                PluginSettings none = SettingsStore.Load(Path.Combine(dir, "missing.txt"));
                check(none != null && none.VoiceEnabled && none.UiLanguage == "ko", "파일 없음 → 기본값");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }

            if (failures.Count > 0)
            {
                Console.WriteLine($"❌ 설정 파일 호환 셀프테스트 실패 {failures.Count}건 (통과 {pass}건)");
                foreach (string f in failures)
                    Console.WriteLine("   - " + f);
                return 1;
            }
            Console.WriteLine($"✅ 설정 파일 호환 셀프테스트 {pass}건 통과 (옛 키 {RemovedKeys.Length}종 무시·저장 시 제거)");
            return 0;
        }
    }
}
