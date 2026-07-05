# 설치 가이드 — LMU 공유 메모리 플러그인 + 크루치프 앱

## 1. 공유 메모리 플러그인 설치 (필수)

Le Mans Ultimate는 rFactor 2 엔진 기반이라 The Iron Wolf가 만든
**rF2 Shared Memory Map Plugin**을 그대로 사용합니다.

### 1-1. DLL 다운로드

- 배포처: https://github.com/TheIronWolfModding/rF2SharedMemoryMapPlugin (Releases)
- 파일: `rFactor2SharedMemoryMapPlugin64.dll`

> 이미 SimHub, CrewChief, pitwall 앱 등을 쓰고 있다면 설치되어 있을 수 있습니다.
> 아래 경로에 DLL이 있는지 먼저 확인하세요.

### 1-2. DLL 복사

게임 설치 폴더의 `Plugins` 디렉토리에 복사합니다.

```
<Steam 라이브러리>\steamapps\common\Le Mans Ultimate\Plugins\rFactor2SharedMemoryMapPlugin64.dll
```

### 1-3. 플러그인 활성화

`<게임 폴더>\UserData\player\CustomPluginVariables.JSON` 파일을 열어
플러그인 항목의 `" Enabled"` 값을 `1`로 설정합니다.

```json
{
  "rFactor2SharedMemoryMapPlugin64.dll": {
    " Enabled": 1
  }
}
```

⚠️ **키 이름 `" Enabled"` 앞의 공백 한 칸은 오타가 아니라 의도된 것입니다.**
공백을 지우면 게임이 설정을 인식하지 못합니다.

- 파일에 해당 항목이 없으면 게임을 (플러그인 DLL을 복사한 상태로) 한 번
  실행했다가 종료하세요. 항목이 자동 생성됩니다. 그 후 값을 1로 바꾸면 됩니다.
- 게임 실행 중에는 이 파일을 수정하지 마세요. 게임 종료 후 수정 → 재실행.

### 1-4. 동작 확인

게임을 실행하고 세션(연습주행이라도)에 들어간 상태에서 크루치프 앱을 실행했을 때
`게임 대기 중...` 대신 상태 라인이 출력되면 정상입니다.

## 2. 크루치프 앱 설치

요구사항: **Windows 10/11, Python 3.11 이상**

```bat
git clone <이 저장소>
cd aicrew
python -m venv venv
venv\Scripts\activate
pip install -r requirements.txt
```

## 3. 설정

```bat
copy config.yaml.example config.yaml
```

`config.yaml`을 열어 필요한 항목을 수정합니다. 최소한 아래를 확인하세요.

- `llm.api_key` — Anthropic API 키 (또는 환경변수 `ANTHROPIC_API_KEY` 설정)
- `tts.engine` — 기본 `edge` (무료, 인터넷 필요). ElevenLabs 사용 시 `elevenlabs` + 키 입력
- `voice.volume`, 각종 `thresholds` — 취향대로

## 4. 실행

```bat
venv\Scripts\activate
python main.py
```

게임보다 먼저 실행해도 됩니다. 게임/세션이 시작되면 자동으로 붙습니다.

### 게임 없이 테스트 (리플레이 모드)

실주행 중 `--record`로 녹화해 두면, 게임 없이 전체 파이프라인을 재생·테스트할 수
있습니다.

```bat
python main.py --record data\myrace.jsonl     :: 주행하며 녹화
python main.py --replay data\myrace.jsonl     :: 재생 (게임 불필요)
python main.py --replay data\myrace.jsonl --speed 10   :: 10배속
```

녹화 파일이 없으면 합성 레이스 데이터를 만들어 테스트할 수 있습니다:

```bat
python tools\make_test_replay.py data\test.jsonl
python main.py --replay data\test.jsonl --speed 5
```

## 문제 해결

| 증상 | 확인 사항 |
|------|-----------|
| 계속 `게임 대기 중...` | 플러그인 DLL 위치, `" Enabled": 1` (공백 포함), 게임이 64비트인지 |
| `세션 대기 중...` | 게임은 연결됨. 메뉴가 아니라 실제 세션(주행 화면)에 들어가야 함 |
| 음성이 안 나옴 | `voice.enabled: true`, Edge TTS는 인터넷 필요, 볼륨 설정 |
| LLM 멘트가 안 나옴 | `ANTHROPIC_API_KEY` 설정 여부, 콘솔의 API 오류 로그 |
