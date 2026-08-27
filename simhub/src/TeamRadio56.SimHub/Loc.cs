using System.Collections.Generic;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// 설정 화면 문자열 테이블 (ko/en). SettingsControl의 모든 라벨/힌트가
    /// 여기를 거친다 — 키가 en에 없으면 ko로 폴백.
    /// </summary>
    public static class Loc
    {
        public static string Lang = "ko";

        public static string L(string key)
        {
            Dictionary<string, string> table = Lang == "en" ? En : Ko;
            string text;
            if (table.TryGetValue(key, out text))
                return text;
            return Ko.TryGetValue(key, out text) ? text : key;
        }

        private static readonly Dictionary<string, string> Ko = new Dictionary<string, string>
        {
            { "subtitle", "LMU AI 크루치프 — 상황을 판단해 팀라디오로 불러줍니다. 버전 " },
            { "sec_status", "상태" },
            { "sec_engine", "엔진" },
            { "sec_voice", "음성" },
            { "sec_chatter", "수다스러움" },
            { "sec_traffic", "트래픽 / 스포터" },
            { "sec_reports", "HUD 대체 정기 무전" },
            { "sec_llm", "LLM 멘트" },
            { "sec_behaviour", "동작" },
            { "btn_test_speak", "테스트 발화" },
            { "btn_open_log", "로그 열기" },
            { "btn_open_settings", "설정 파일 열기" },
            { "btn_engine_start", "엔진 시작" },
            { "btn_engine_stop", "엔진 중지" },
            { "btn_engine_restart", "엔진 재시작" },
            { "btn_engine_log", "엔진 로그" },
            { "engine_hint", "python = 검증된 파이썬 엔진을 플러그인이 자식 프로세스로 띄웁니다 "
                             + "(전체 기능, 현재 권장). builtin = C# 내장 — 판단/콜은 이식 완료, "
                             + "LLM 멘트와 한국어는 아직 python 모드에서만." },
            { "engine_apply_hint", "설정을 바꾸면 [엔진 재시작]을 눌러야 엔진에 반영됩니다." },
            { "row_mode", "모드" },
            { "hint_mode", "바꾸면 즉시 전환됩니다" },
            { "row_cache_dir", "오디오 캐시 폴더" },
            { "hint_cache_dir", "비우면 자동 탐색 (DLL 옆 → teamradio56-engine → 엔진 옆)" },
            { "row_exe", "실행 파일" },
            { "hint_exe", "비우면 기본: 플러그인 폴더\\teamradio56-engine\\" },
            { "row_args", "추가 인자" },
            { "hint_args", "소스로 돌릴 때 main.py 경로" },
            { "row_ui_lang", "화면 언어 / UI language" },
            { "row_voice_lang", "멘트 언어" },
            { "hint_voice_lang", "en = 영어 무전(기본), ko = 한국어. 바꾼 뒤 [엔진 재시작]" },
            { "row_voice_on", "음성 출력" },
            { "hint_voice_on", "끄면 로그에만 남습니다" },
            { "row_voice_engine", "음성 엔진" },
            { "hint_voice_engine", "kokoro = 동봉 캐시 음성(권장), edge = edge-tts 보이스. 바꾼 뒤 [엔진 재시작]" },
            { "row_voice", "보이스" },
            { "hint_voice", "edge 엔진용 — 멘트 언어와 안 맞으면 자동 보정합니다" },
            { "row_rate", "말 속도" },
            { "row_volume", "볼륨" },
            { "row_radiofx", "무전기 효과" },
            { "hint_radiofx", "TTS 기계음을 팀라디오 질감으로" },
            { "row_noise", "무전 노이즈" },
            { "hint_noise", "0이면 지직임 없음" },
            { "row_preset", "프리셋" },
            { "hint_preset", "quiet = 꼭 필요한 콜만, chatty = 자주" },
            { "chatter_hint", "긴급 콜(나란히/충격/펑크/피트 리미터)은 프리셋과 무관하게 항상 나갑니다." },
            { "row_alongside", "나란히 판정 거리" },
            { "hint_alongside", "차 한 대 길이 ≈ 4.6m" },
            { "row_spotter", "스타트 스포터 모드" },
            { "hint_spotter", "혼전 구간엔 좌우 점유만 즉시 콜" },
            { "fmt_seconds", "{0:0}초" },
            { "row_invert", "좌우 반전" },
            { "hint_invert", "\"왼쪽/오른쪽\"이 반대로 들리면 켜세요" },
            { "row_race_only", "레이스에서만" },
            { "hint_race_only", "LMU 온라인 연습/퀄리는 프라이빗(유령 콜 방지). 트랙 공유 세션이면 끄세요" },
            { "reports_hint", "HUD를 끄고 달릴 때 켜세요. 기본은 꺼짐(침묵 우선)." },
            { "row_laptime", "매 랩 랩타임" },
            { "hint_laptime", "\"Last lap 2 01.8. Best lap.\"" },
            { "row_status_report", "상황 리포트" },
            { "fmt_every_laps", "{0:0}랩마다" },
            { "hint_status_report", "0 = 끔. 순위/갭/연료/타이어" },
            { "llm_hint", "여러 데이터를 엮은 판단형 멘트를 실시간 생성합니다. "
                          + "꺼도 긴급 콜과 템플릿 멘트는 그대로 동작합니다." },
            { "row_llm_on", "사용" },
            { "row_api_key", "API 키" },
            { "hint_api_key", "비우면 환경변수 ANTHROPIC_API_KEY" },
            { "row_budget", "시간당 호출 예산" },
            { "fmt_calls", "{0:0}회" },
            { "hint_budget", "2시간 레이스 기준 30회 이내 권장" },
            { "row_realtime", "주행 중에만 발화" },
            { "hint_realtime", "모니터/메뉴에선 침묵" },
            { "row_speech_log", "발화 로그" },
            { "hint_speech_log", "무슨 말을 언제 했는지 기록" },
            { "conn_on", "● LMU 연결됨" },
            { "conn_off", "○ 게임 대기 중" },
            { "engine_error", "엔진 오류 — " },
            { "engine_running", "엔진 실행 중 · " },
            { "engine_stopped", "엔진 중지됨 · " },
            { "engine_builtin", "내장(C#) 엔진 동작 중 — 판단/콜 이식 완료 "
                                + "(LLM 멘트·한국어는 python 모드)" },
            { "no_recent", "최근 무전 없음" },
            { "recent_title", "최근 무전" },
        };

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            { "subtitle", "LMU AI crew chief — reads the race and calls it on team radio. Version " },
            { "sec_status", "Status" },
            { "sec_engine", "Engine" },
            { "sec_voice", "Voice" },
            { "sec_chatter", "Chatter" },
            { "sec_traffic", "Traffic / Spotter" },
            { "sec_reports", "HUD-replacement reports" },
            { "sec_llm", "LLM lines" },
            { "sec_behaviour", "Behaviour" },
            { "btn_test_speak", "Test speak" },
            { "btn_open_log", "Open log" },
            { "btn_open_settings", "Open settings file" },
            { "btn_engine_start", "Start engine" },
            { "btn_engine_stop", "Stop engine" },
            { "btn_engine_restart", "Restart engine" },
            { "btn_engine_log", "Engine log" },
            { "engine_hint", "python = the plugin runs the proven Python engine as a child "
                             + "process (full features, recommended). builtin = native C# — "
                             + "calls are fully ported; LLM lines and Korean still need python mode." },
            { "engine_apply_hint", "After changing settings, press [Restart engine] to apply." },
            { "row_mode", "Mode" },
            { "hint_mode", "switches immediately" },
            { "row_cache_dir", "Audio cache folder" },
            { "hint_cache_dir", "empty = auto-detect (next to DLL → teamradio56-engine → engine)" },
            { "row_exe", "Executable" },
            { "hint_exe", "empty = default: plugin folder\\teamradio56-engine\\" },
            { "row_args", "Extra args" },
            { "hint_args", "path to main.py when running from source" },
            { "row_ui_lang", "UI language / 화면 언어" },
            { "row_voice_lang", "Radio language" },
            { "hint_voice_lang", "en = English radio (default), ko = Korean. Then [Restart engine]" },
            { "row_voice_on", "Voice output" },
            { "hint_voice_on", "off = log only" },
            { "row_voice_engine", "Voice engine" },
            { "hint_voice_engine", "kokoro = bundled cached voice (recommended), edge = edge-tts. Then [Restart engine]" },
            { "row_voice", "Voice" },
            { "hint_voice", "for the edge engine — auto-corrected if it doesn't match the radio language" },
            { "row_rate", "Speech rate" },
            { "row_volume", "Volume" },
            { "row_radiofx", "Radio effect" },
            { "hint_radiofx", "turns raw TTS into team-radio texture" },
            { "row_noise", "Radio noise" },
            { "hint_noise", "0 = no static" },
            { "row_preset", "Preset" },
            { "hint_preset", "quiet = essential calls only, chatty = frequent" },
            { "chatter_hint", "Urgent calls (alongside/impact/puncture/pit limiter) always go out." },
            { "row_alongside", "Alongside window" },
            { "hint_alongside", "one car length ≈ 4.6m" },
            { "row_spotter", "Start spotter mode" },
            { "hint_spotter", "first-corner chaos: side-occupancy calls only" },
            { "fmt_seconds", "{0:0}s" },
            { "row_invert", "Swap left/right" },
            { "hint_invert", "enable if \"left/right\" sound reversed" },
            { "row_race_only", "Race only" },
            { "hint_race_only", "LMU online practice/quali are private (ghost calls). Untick for shared-track sessions" },
            { "reports_hint", "Enable when driving with the HUD off. Default off (silence first)." },
            { "row_laptime", "Lap time every lap" },
            { "hint_laptime", "\"Last lap 2 01.8. Best lap.\"" },
            { "row_status_report", "Status report" },
            { "fmt_every_laps", "every {0:0} laps" },
            { "hint_status_report", "0 = off. Position/gaps/fuel/tyres" },
            { "llm_hint", "Generates judgment lines that combine multiple data points. "
                          + "Urgent calls and template lines keep working when off." },
            { "row_llm_on", "Enabled" },
            { "row_api_key", "API key" },
            { "hint_api_key", "empty = env var ANTHROPIC_API_KEY" },
            { "row_budget", "Calls per hour" },
            { "fmt_calls", "{0:0}" },
            { "hint_budget", "aim under 30 for a 2-hour race" },
            { "row_realtime", "Speak only while driving" },
            { "hint_realtime", "silent in monitor/menus" },
            { "row_speech_log", "Speech log" },
            { "hint_speech_log", "records what was said and when" },
            { "conn_on", "● LMU connected" },
            { "conn_off", "○ waiting for game" },
            { "engine_error", "Engine error — " },
            { "engine_running", "Engine running · " },
            { "engine_stopped", "Engine stopped · " },
            { "engine_builtin", "Native (C#) engine running — calls fully ported "
                                + "(LLM lines & Korean need python mode)" },
            { "no_recent", "No recent radio" },
            { "recent_title", "Recent radio" },
        };
    }
}
