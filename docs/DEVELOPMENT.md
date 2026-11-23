# 개발 문서 — teamradio56

사용자 문서는 [README.md](../README.md) / [README.ko.md](../README.ko.md)와
[INSTALL.md](INSTALL.md)에 있다. 이 문서는 저장소를 고치거나 빌드·릴리스하는 사람을 위한 것이다.
(README·INSTALL에 있던 개발자용 내용 — 파이썬 앱, 구조, 마일스톤, 릴리스, 공유 메모리 특성 — 을
이리로 옮겼다.)

## 문서 지도

| 문서 | 내용 |
|---|---|
| [simhub/README.md](../simhub/README.md) | SimHub 플러그인(C#): 빌드, 컴파일 검증, 소스 업데이트(`update.bat`), **릴리스 만들기(메인테이너)**, 설정 화면, 포팅 현황·리플레이 회귀, 음성 팩 배포 파일 규약, 오버레이 프로퍼티, 생성물 재생성 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 파이썬 앱의 계층 구조 |
| [DATA_SOURCES.md](DATA_SOURCES.md) | 공유 메모리 / REST 데이터 소스 |
| 이 문서 | 파이썬 앱 설치·실행·리플레이·패키징, 음성 팩 렌더, 저장소 구조, 릴리스 요약, 버전별 변경 메모, 마일스톤, 공유 메모리 특성 |

## 플러그인과 파이썬 앱

- **사용자에게 배포하는 것은 SimHub 플러그인**(`simhub/`, C#)이다. 항상 C# 내장 엔진 하나로 돌고,
  영어 무전을 Kokoro 음성 팩 한 목소리로 낸다. 파이썬이 필요 없다.
- **저장소 루트의 파이썬 앱**은 레퍼런스 구현(리플레이 회귀의 기준)이자 음성 팩을 렌더하는 도구다.
  사용자에게는 필요 없다.

SimHub 플러그인은 0.18부터 일부러 가볍게 간다 ("lite"): 파이썬 엔진 모드, 온라인(edge)·
Windows 목소리, 목소리 섞임, LLM 설정, 한국어 무전이 없다. 그런 실험 기능 — Anthropic
API(본인 키)로 만드는 LLM 멘트, 한국어 무전, edge·ElevenLabs 목소리, 랩 후 섹터 피드백 — 은
개발·음성 팩 렌더용으로 유지하는 파이썬 앱에만 있다. 예전 플러그인 설정의 해당 항목은 무시된다.

플러그인과 파이썬 앱을 **동시에 돌리지 않는다** — 둘 다 무전을 낸다. 파이썬 앱과 런처는 플러그인이
쥔 뮤텍스(`Local\teamradio56-plugin`)를 보면 스스로 물러난다.

## 파이썬 앱

### 요구사항

- Windows 10/11 (공유 메모리 모드) — 리플레이 모드는 OS 무관
- Python 3.11 이상 — 파이썬 앱·음성 팩 도구에만 필요 (SimHub 플러그인은 불필요)
- LMU + [rF2 Shared Memory Map Plugin](https://github.com/TheIronWolfModding/rF2SharedMemoryMapPlugin)
  (설치는 [INSTALL.md](INSTALL.md#선택-rf2-공유-메모리-플러그인)). 파이썬 앱은 지금도 rF2 플러그인
  버퍼(`$rFactor2SMMP_*$`, `rf2data.py`)만 읽는다 — LMU 내장 공유 메모리(`LMU_Data`)는 SimHub
  플러그인(C#)만 읽는다 ([DATA_SOURCES.md](DATA_SOURCES.md#lmu-내장-공유-메모리-lmu_data)).

### 설치

```bat
git clone https://github.com/hyungwookc07/teamradio56.git
cd teamradio56
pip install pipenv
pipenv install
pipenv run python main.py        :: 또는 pipenv shell 후 python main.py
```

pipenv 대신 venv를 쓴다면:

```bat
cd teamradio56
python -m venv venv
venv\Scripts\activate
pip install -r requirements.txt
copy config.yaml.example config.yaml
python main.py
```

### 설정

```bat
copy config.yaml.example config.yaml
```

`config.yaml`은 커밋하지 않는다. 열어서 필요한 항목을 수정한다. 최소한 아래를 확인한다.

- `llm.api_key` — Anthropic API 키 (또는 환경변수 `ANTHROPIC_API_KEY` 설정)
- `tts.engine` — 설정에 없으면 기본 `kokoro` (음성 팩 재생, 인터넷 불필요). 예시 파일은
  `edge`(무료, 인터넷 필요)로 되어 있다. ElevenLabs 사용 시 `elevenlabs` + 키 입력.
  **세션당 한 목소리**: 고른 목소리로 낼 수 없는 멘트는 다른 목소리로 읽지 않고 로그(`[무전]`)와
  자막으로만 남긴다 — kokoro는 음성 팩에 없는 멘트, edge는 오프라인이라 합성하지 못한 멘트.
  kokoro 음성 팩(`audio_cache\`)이 없으면 세션 전체를 edge로, 한국어 무전은 항상 edge로 말한다.
  다른 목소리로라도 듣고 싶으면 `tts.allow_fallback: true` (목소리가 섞인다).
  kokoro 음성 팩은 임의 문장을 말할 수 없어서, kokoro로 말할 때는 LLM 멘트를 요청하지 않고
  템플릿 멘트를 쓴다 (`pip install kokoro-onnx`로 로컬 런타임이 있거나 edge면 LLM 사용)
- `voice.volume`, 각종 `thresholds` — 취향대로

### 실행

```bat
pipenv run python main.py
```

게임보다 먼저 실행해도 된다. 게임/세션이 시작되면 자동으로 붙는다. 게임이나 세션을 기다리는 동안
콘솔에 `게임 대기 중...` / `세션 대기 중...`이 나오고, 세션에 들어가면 상태 라인이 출력된다.

#### 게임과 자동 동시 실행 (Steam 시작 옵션)

> **SimHub 플러그인을 쓴다면 이 설정은 하지 않는다** (이미 했다면 시작 옵션을 지운다).
> 플러그인이 내장 엔진으로 직접 무전을 내므로, 이 런처까지 쓰면 무전이 두 번(서로 다른
> 목소리로) 나온다. 0.14부터 런처와 파이썬 앱은 플러그인이 돌고 있으면 스스로 물러나지만,
> 지우는 편이 확실하다.

플러그인 없이 독립 실행만 쓸 때, 매번 따로 켜기 귀찮으면 Steam이 게임을 켤 때 teamradio56을
같이 띄우게 할 수 있다:

1. Steam 라이브러리 → **Le Mans Ultimate** 우클릭 → **속성** → 일반
2. **시작 옵션**에 입력 (경로는 실제 설치 위치로):

```
"D:\teamradio56\launch_with_lmu.bat" %command%
```

이후 Steam에서 게임을 실행하면 teamradio56이 최소화 창으로 같이 뜨고, 게임을 끄면 20초 뒤
(디브리핑/저장 마무리 시간) 자동 종료된다. 시작 옵션을 지우면 원래대로 돌아간다.

### 게임 없이 테스트 (리플레이 모드)

실주행 중 `--record`로 녹화해 두면, 게임 없이 전체 파이프라인을 재생·테스트할 수 있다.

```bat
python main.py --record data\myrace.jsonl     :: 주행하며 녹화
python main.py --replay data\myrace.jsonl     :: 재생 (게임 불필요)
python main.py --replay data\myrace.jsonl --speed 10   :: 10배속
```

녹화 파일이 없으면 합성 레이스 데이터를 만들어 테스트할 수 있다:

```bat
python tools\make_test_replay.py data\test.jsonl
python main.py --replay data\test.jsonl --speed 5
```

### 단일 exe로 패키징 (선택)

파이썬 없이 배포하고 싶다면 PyInstaller로 단일 실행 파일을 만들 수 있다.

```bat
pipenv install --dev
pipenv run pyinstaller teamradio56.spec
```

- 결과물: `dist\teamradio56.exe`
- `config.yaml`을 exe와 같은 폴더에 두고 실행한다. `audio_cache\`, `data\`도
  실행 위치 기준으로 생성된다.
- 사전 생성 오디오 캐시(`python tools\pregen_audio.py`)를 미리 만들어
  `audio_cache\` 폴더째 함께 배포하면 첫 실행부터 긴급 콜이 지연 없이 나온다.

### 음성 팩 렌더

영어 kokoro 팩(`tts.engine: kokoro`)은 제작자 PC(`pip install kokoro-onnx`)에서 두 번 돌려 만든다:

```bat
python tools\pregen_audio.py --engine kokoro
python tools\pregen_audio.py --segments --tiers 1,2,3
```

배포할 파일은 `*_rfx3.wav`(통문장), `*_seg1.wav`(문장 세그먼트), `_seg1_*.wav`(조립 자산 4개)이다.
효과 전 원본 `*.wav`는 넣지 않는다. 파이썬 엔진은 조립한 멘트를 `audio_cache\_assembled\`에
최근 200개까지 저장해 재사용한다 (배포 대상 아님).

릴리스용 음성 팩은 GitHub Actions의 `audio-pack` 워크플로가 렌더·게시한다 (기술자는
`voice_lines/audio_pack.txt`). 배포 파일 규약과 세그먼트 버전은
[simhub/README.md](../simhub/README.md)의 해당 절을 본다.

### 파이썬 앱 문제 해결

| 증상 | 확인 사항 |
|------|-----------|
| 계속 `게임 대기 중...` | 플러그인 DLL 위치, `" Enabled": 1` (공백 포함), 게임이 64비트인지 |
| `세션 대기 중...` | 게임은 연결됨. 메뉴가 아니라 실제 세션(주행 화면)에 들어가야 함 |
| 음성이 안 나옴 | `voice.enabled: true`, Edge TTS는 인터넷 필요, 볼륨 설정 |
| LLM 멘트가 안 나옴 | `ANTHROPIC_API_KEY` 설정 여부, 콘솔의 API 오류 로그 |

## 저장소 구조

```
main.py            # 메인 루프 (5Hz 폴링 + 랩 이벤트 디스패치)
telemetry.py       # 공유 메모리 읽기 / 리플레이 / 녹화
rf2data.py         # rF2 공유 메모리 ctypes 구조체 (pyRfactor2SharedMemory 기반)
state.py           # 세션 상태 + 랩 히스토리 축적
analyzers/
  fuel.py          # 연료 소모/남은 랩/피트 윈도우
  tyres.py         # 타이어 온도 불균형/마모 추세/예상 수명
  pace.py          # 랩타임 추세/갭 변화율
  traffic.py       # 상대 차량 접근 감지 (5Hz)
vehicle_class.py   # 클래스 문자열 → 계열/서열/호칭 (단일 출처)
events.py          # 이벤트 우선순위 큐 (중복 제거, 쿨다운)
voice.py           # 멘트 생성 (사전 생성 풀 / 실시간 LLM 이원화)
tts.py             # TTS 재생 스레드 + 오디오 캐시
config.py          # config.yaml 로드
messages.py        # 문장 표 (en/ko) + 슬롯 헬퍼
voice_lines/       # 멘트 풀 YAML + 음성 팩 기술자 (audio_pack.txt)
tools/             # 리플레이 생성·검사, 음성 팩 렌더, 코드 생성기
simhub/            # SimHub 플러그인 + C# 엔진 (simhub/README.md)
```

전체 계층은 [ARCHITECTURE.md](ARCHITECTURE.md).

## 검증 (게이트)

- `bash simhub/verify/build-check.sh` — SimHub 없이 스텁으로 전체 C# 컴파일 + 경량판 가드
- `bash simhub/verify/replay-check.sh` — 리플레이 회귀 (C#이 파이썬과 같은 판단·문장을 내는지,
  레이스 종료 오라클, 기대 콜 대조). LMU 내장 공유 메모리 교차 검증(`tools/lmu_native_blob.py` 합성
  덤프 ↔ C# 리더, rF2 동등성, 원천 대체 규칙)과 릴리스 흐름 오프라인 검사(`release-flow-check.sh`)도
  여기서 부른다
- `pwsh -NoProfile -File simhub/verify/ps-check.ps1 update.ps1 release.ps1` — PowerShell 5 호환 정적 검사
- CI(`.github/workflows/ci.yml`)는 `build-check.sh`와 `replay-check.sh`를 돌린다

자세한 내용과 환경 변수는 [simhub/README.md](../simhub/README.md)를 본다.

## 릴리스

자세한 절차는 [simhub/README.md](../simhub/README.md)의 "릴리스 만들기 (메인테이너)" 절.
관련 파일: `.github/workflows/release.yml`, `.github/workflows/audio-pack.yml`,
`voice_lines/audio_pack.txt`, `release.bat` / `release.ps1`, `update.bat`(소스 빌드 + DLL 복사).

요약: 릴리스(플러그인 zip + `teamradio56-release.txt` 매니페스트)는 한 단계다 —
`TeamRadio56Plugin.Version`과 같은 `vX.Y.Z` 태그를 main 커밋에 push하거나 GitHub 화면에서
그 릴리스를 만들면, GitHub Actions가 실제 SimHub 어셈블리로 플러그인을 빌드하고, 멘트 집합이
바뀌었으면 음성 팩(별도 `audio-<pack>` 프리릴리스)을 먼저 렌더·게시해(약 2시간 더) 기술자를
main에 커밋한 뒤 릴리스를 채운다. 태그를 push할 수 없으면 main 커밋을 `release/vX.Y.Z`
브랜치로 push해도 같다 (태그는 게시할 때 생김). dry run은 `release-dry/**` 브랜치 push, 음성 팩만
따로 만들 때는 `audio-pack` 워크플로. 실패한 실행은 플러그인 릴리스를 남기지 않는다 — 네트워크·
권한·승인처럼 코드 밖 원인이면 원인을 풀고 Re-run failed jobs. Re-run은 같은 태그 커밋을 다시
빌드하므로, 코드를 고쳐야 하면 main에 고친 뒤 태그를 지우고 main 최신 커밋에 다시 push한다
(GitHub 화면에서 만든 릴리스면 그 릴리스와 태그를 지우고 다시 만든다). 이미 게시한 음성 팩은
다시 렌더하지 않는다. `v0.17.0`은 손으로 만든 빈 릴리스이고 0.17.1이 첫 실제 릴리스다.

소스로 빌드해 쓰는 경우는 `update.bat` 더블클릭 (pull + 빌드 + DLL 복사).

## 버전별 업데이트 메모 (플러그인)

- **0.16** — 플러그인이 [GitHub 릴리스](https://github.com/hyungwookc07/teamradio56/releases)에서
  직접 업데이트한다 (설정 화면 → **업데이트** → [업데이트 확인] → [플러그인 업데이트](끝나면 SimHub
  재시작) 또는 [음성 팩 업데이트]). 버튼을 누르기 전에는 아무것도 설치하지 않는다. 설정 화면에
  **업데이트** 절이 없는 0.15 이하(또는 소스 빌드)는 최신 릴리스(0.17 이상)를 한 번 손으로 설치한다
  (릴리스 zip의 DLL 두 개를 SimHub 폴더에 복사).
- **0.17** — 새 음성 팩(`kokoro-v016`, 약 175 MB). 플러그인이 음성 팩이 없거나 옛 팩을 찾으면
  [음성 팩 업데이트]를 띄우고, 설치가 끝나면 엔진이 스스로 다시 시작해 바로 적용한다. 그 전까지
  설치된 팩에 없는 멘트(팩이 없으면 전부)는 말하지 않고 자막으로만 나간다.
- **0.18** — 파이썬 엔진 옆의 오디오 캐시는 찾지 않는다. 그걸 쓰던 경우 [음성 팩 업데이트]로 DLL 옆에
  새로 받거나 설정의 **음성 팩 폴더**에 그 경로를 넣는다.
- **0.19** — 음성 팩 `kokoro-v017` (zip 8개, 약 185 MB / 화면 표기 약 176 MB).
- **다음 버전 (미출시)** — LMU 1.2+ 내장 공유 메모리(`LMU_Data`)를 직접 읽어 rF2 Shared Memory Map
  Plugin(DLL)이 선택이 된다 (게임 Settings → Gameplay → Enable Plugins + 재시작). DLL은 대체 수단으로
  남고, 있으면 피트 제한속도·페널티 메시지를 거기서 보충한다. 실차 확인 전 ([DATA_SOURCES.md](DATA_SOURCES.md#lmu-내장-공유-메모리-lmu_data)).

## 개발 마일스톤

- [x] v0.1 — 공유 메모리 연결, 연료/랩타임/갭 콘솔 출력, 게임 미실행 시 대기, 리플레이 모드
- [x] v0.2 — 랩 완료 분석(연료/페이스) + 이벤트 큐 + 템플릿 멘트 TTS
- [x] v0.3 — 트래픽 분석기 + 사전 생성 변형 풀 + 오디오 캐시
- [x] v0.4 — 실시간 LLM 멘트 (Anthropic API, 페르소나/서사) — 실험 기능
- [x] v0.5 — 타이어 분석기, 레이스 JSON 저장, PyInstaller 패키징

### 자연스러움 개선 (v0.6)

(여기의 LLM 항목은 실험 기능이며 켜야 동작한다.)

- [x] 트래픽 per-car 상태 머신 — 상태 전이 시에만 발화, 같은 차 멘트는 서사로 연결
  ("붙는다" → "옆이야" → "지나갔어"), 다중 차량은 위협도 순 한 문장 종합
- [x] 클래스 정규화 — 게임 클래스 문자열(`mVehicleClass`)을 계열로 묶는다 (`Hyper`/`HY`/`LMH`/
  `LMDh` → 하이퍼카, `LMP2_ELMS`/`LMP2 Pro-Am` → LMP2, `LMP3`, `LMGTE Am` → GTE, `LMGT3` → GT3).
  빠른 계열(하이퍼카 > LMP2 > LMP3 > GTE > GT3)이거나 한 랩 앞선 같은 클래스만 "빠른 트래픽"
  접근 예고를 하고, 같은 계열은 배틀 상대라 "faster car"라고 부르지 않는다. 모르는 클래스
  문자열은 관측 페이스(클래스별 베스트 랩 중앙값, 2% 여유)로만 가르고, 그것도 없으면 빠른 차도
  백마커도 아니며 클래스 이름 없이 말한다 (다중 종합은 "car"/"차"). 내 클래스와 같은 클래스의
  차도 내 클래스 이름으로 부르지 않는다 — "Car behind.", "He's there.", 랩 돌리러 오는 클래스
  리더는 "Traffic behind, 3 seconds.", 두 대면 "two cars behind" / "one car, one Hypercar behind"
  (vehicle_class.spoken_class — 단일 클래스 경기에서 "LMP3 right behind"가 나오던 문제). 세션마다 처음 본 클래스
  문자열을 계열·서열·호칭과 함께 로그에 남긴다 (`클래스 진단:` 줄 — 실제 LMU 문자열 확인용)
- [x] 변형 풀 톤 태그(캐주얼/긴박) + `scripts/generate_variants.py` (LLM 일괄 재생성)
- [x] 브리지 기법 — 긴급 캐시 콜(지연 0) 직후 LLM 후속 설명을 비동기로 이어붙임,
  상황 종료 시 자동 폐기. 재생 미세 랜덤화(0~300ms, 볼륨 ±5%)
- [x] 레이스 서사 문맥 유지(진행 중 이슈) + LLM 전략 엔진 (판단 시점에만 트리거,
  시간당 호출 예산으로 2시간 레이스 30회 이내)
- [x] 발화 억제 강화 (LLM PASS=침묵) + 발화 로그 `data/speech_log.jsonl`
- [x] 트레이닝 모드 — 랩 직후 섹터 델타 피드백, 세션 종료 LLM 디브리핑,
  트랙 재방문 시 과거 기록 대비 추세 코멘트

### 레이스 정보 확장 (v0.7)

- [x] 레이스 컨트롤 — FCY/세이프티카 발동·피트 오픈·리스타트 콜(내구레이스 전략 핵심),
  섹터 로컬 옐로, 그린/체커, 남은 시간 마일스톤, 마지막 랩, 피트 리미터 미작동 경고
- [x] 실제 WEC/IMSA 엔지니어 같은 세션 브리핑 — 트랙 이름 대신 항상 무전 체크로 열고 세션마다
  다르게: 연습(길이·노면/비·빌드업 런 계획), 퀄리(아웃랩 공간 확보·트랙 리미트·"한 랩에 다",
  연료 없음), 웜업(차 점검), 레이스(클래스 N대 중 스타트 순위·연료·포메이션 랩과 그린 콜·스타트
  스포터·첫 랩 침착), 중간 합류 변형 (연습·퀄리·웜업은 차가 이미 나가 있을 때만 중간 합류 — 진행 중인
  세션에 차고에서 붙으면 남은 시간으로 전체 브리핑). 문장 슬롯마다 변형 2~5개를 스냅샷 값의 FNV-1a
  해시(상위 비트로 줄여 슬롯끼리 독립)로 골라 파이썬과 C#이 같은 문장을 말하면서도 세션마다 달라진다
- [x] 체커와 그 뒤 — 체커 콜은 결과 등급(우승·클래스 우승·포디움·스타트 대비 상승/비슷/하락,
  중간 합류면 결과만)별 변형이고 실제 클래스 순위를 말한다. 이어서 쿨다운 랩 콜(아직 달리는 빠른
  차 경고 포함), 요약 한 번(클래스 패스티스트 랩 또는 베스트 랩 순위), 피트인 인사(ESC로 차고에 바로
  돌아가면 생략). 내 피니시 뒤에는 전략·연료·타이어·라이벌·순위·정기 리포트를 끄고 안전 콜(피트
  리미터·옐로·휠 탈락·나란히/빠른 차 접근)만 남긴다
- [x] 클래스 순위 — 멀티클래스에서 진짜 순위(클래스 내) 계산, 변동 시에만 콜
- [x] 블루 플래그 / 백마커 — mFlag(블루) 엣지 + 랩 델타 기반 이중 감지.
  랩 앞선 리더가 오면 양보 안내, 내가 잡는 백마커는 "배틀 아님" 예고
- [x] 라이벌 인텔 — 동클래스 경쟁자 피트 진입 감지(언더컷/오버컷 판단),
  클래스 앞뒤 차와 페이스 비교("랩당 0.7초 빨라, 8랩쯤 뒤에 온다")
- [x] 차량 컨디션 — 수온/유온/과열, 브레이크 온도 경고
- [x] 데미지 자동 점검 — 충격 8초 뒤 덴트/휠/펑크/공기압을 데이터로 훑어
  결과 리포트 ("가벼운 자국뿐, 그대로 가" / "공기압 빠지는 중, 박스 준비").
  휠 탈락 즉시 콜, 슬로우 펑처 상시 감시 포함 — 확인은 드라이버가 아니라 툴이 한다.
  얼라인("직선에서 핸들 쏠림")은 차가 실제로 곧게 가는(요레이트가 작고 미끄러지지 않는 —
  고속 스위퍼는 빠지고, 리어 토가 틀어져 비스듬히 곧게 가는 차는 들어가는) 샘플의 조향
  중앙값을 첫 충격 전과 뒤로 비교한다. 충격 전 기준이 없으면 그 충격만 판정하지 않고(기준을
  다시 모아 다음 충격은 판정), 타이어가 펑크면 콜하지 않는다.
  프론트 윙("프론트 에어로 N밀리 하락")은 스플리터 높이를 같은 속도끼리 비교한다 — 150km/h
  이상, 제동 중이 아닌 샘플을 10km/h 속도 구간으로 묶어 첫 충격 전과 뒤를 맞춘다. 멀쩡한 차도
  속도(공력 하중)와 브레이크 다이브로 높이가 15mm 넘게 움직여서, 느린 구간 접촉 뒤 긴 직선을
  달린 것은 손상이 아니다. 기준 없음·펑크·충격당 한 번 판정 규칙은 얼라인과 같고, 콜은 그 충격의
  점검 리포트 뒤에 나간다. 판정이 끝나 리포트 쿨다운을 기다리는 윙·얼라인 콜은 그 사이 새 충격의
  점검 리포트를 먼저 보내고, 타이어 펑크·공기압 누출 중엔 붙든다
- [x] 연료 세이브 코칭 — 노피트 경계 상황에서 목표 소모량 대비 델타 지시
- [x] 웻/드라이 크로스오버 — 노면 웻니스 기준 타이어 전환 판단 트리거
- [x] HUD 대체 정기 무전 (옵션) — HUD를 끄고 몰입 주행할 때 `reports` 설정으로
  매 랩 랩타임 콜 + N랩마다 순위/갭/연료/타이어 리포트. 기본 꺼짐

### SimHub 플러그인 & C# 포팅 (v0.8~v0.18)

- [x] v0.8~v0.17 — 이중언어(한국어/English) 설정 화면의 SimHub 플러그인. 파이썬 엔진을 자식
  프로세스로 돌리거나 **C# 내장 엔진**으로 돌았다 — 분석기 9종 전부, 세션 브리핑, 멘트 풀, 캐시
  오디오(Kokoro), edge 목소리용 런타임 합성, 한국어 무전, 앱 안 업데이터. 리플레이 회귀로 파이썬
  엔진과 대조(수락된 이벤트가 en/ko 모두 문장 텍스트까지 동일). 실험 기능인 LLM 멘트는 파이썬
  모드가 필요했다.
- [x] v0.18 경량판("lite") — 플러그인은 내장 엔진·영어 무전·Kokoro 음성 팩만 남김 (업데이터,
  오버레이, 이중언어 설정 화면은 그대로). 파이썬 엔진 모드, edge·Windows 목소리, 목소리 섞임,
  LLM 설정, 한국어 무전은 플러그인에서 빼고 파이썬 앱에만 둠. 음성 팩이 없으면 자막만 나가고
  설정 화면이 [음성 팩 업데이트]를 안내. 리플레이 회귀의 파이썬 ↔ C# 대조는 en만

## 공유 메모리 미지원 항목과 실차 특성

공유 메모리 미지원 항목(가상 에너지, 날씨 예보, 피트 전략)은 LMU 내장
REST API(localhost, 게임 UI가 쓰는 로컬 서버)로 보충 예정 — `resttelemetry.py`에
자동 비활성 폴러가 준비돼 있고, 게임 실행 중 `tools/probe_rest.py`로 실제
응답을 수집하면 파싱을 확정한다. 하이브리드 배터리는 양쪽 다 없음.
(REST 소스는 파이썬 앱 전용이고 플러그인에는 없다.)

플러그인은 LMU 1.2+ 내장 공유 메모리(`LMU_Data`)를 우선 읽고 없으면 rF2 플러그인 버퍼를 읽는다.
두 경로가 복사하는 차량 레코드는 게임이 쓰는 같은 구조체라 아래 특성은 양쪽에 똑같이 해당한다
(내장 경로의 출처·빈 필드·확인 안 된 것: [DATA_SOURCES.md](DATA_SOURCES.md#lmu-내장-공유-메모리-lmu_data)).

실차(LMU)에서 확인된 공유 메모리 특성 — 코드가 이에 맞게 방어적으로 동작:

- `mEstimatedLapTime`: 전 차량이 동일한 트랙 기본값으로 채워짐 → 사용하지 않고
  상위 클래스 판정은 클래스 계열 서열(Hypercar > LMP2 > LMP3 > GTE > GT3)로, 모르는 클래스
  문자열은 실제 베스트 랩 중앙값으로 대체
- `mSectorFlag`: 상시로 들어오는 11은 쓰레기가 아니라 LMU의 "깨끗함"이다 (1 = 옐로, 인덱스 i =
  섹터 i+1 — lmu-pitwall 인게임 측정, LmuOverlay 검증 목록; rF2 헤더 기준 깨끗함은 0). 예전
  `0 → 1..2` 전이 조건은 LMU의 11 → 1을 못 잡아 섹터 옐로 콜이 한 번도 안 나갔다 → 이제 파이썬·C#
  모두 원시값 1만 옐로로 보고 `옐로 아님 → 1` 엣지에서만 콜한다 (2·255 등 나머지는 옐로 아님, 첫
  샘플은 기록만). 버스가 거절한 엣지(다른 섹터 콜 뒤 종류 쿨다운 45초)는 섹터가 1인 동안 매 틱 다시
  시도하고, 같은 섹터 120초는 수락된 콜 기준이다. FCY(페이즈 6) 중엔 FCY 콜이 맡고 섹터 콜은
  억제하되 직전 값은 계속 갱신한다. `mYellowFlagState`는 억제 조건이 아니다 — LMU는 그린 복귀 뒤에도
  리스타트 준비 6을 잠깐 남기는데, 그때 리스타트 랩 로컬 옐로는 바로 콜한다.
  회귀: `tools/make_sector_flag_replays.py` (`sector_yellow_*` 리플레이 + 기대 콜, 억제 조건·기록
  시점·재시도 변이를 리플레이가 잡는지도 확인)
- `mBrakeTemp`: 섭씨로 그대로 쓴다. **확인 필요:** TinyPedal·lmu-pitwall은 켈빈으로 보고 273.15를
  뺀다 — 맞다면 브레이크 온도 경고 문턱이 약 273도 어긋나 있다. 이번 작업에서는 바꾸지 않았다
- Extended `mCurrentPitSpeedLimit`: 0으로 미기입되는 경우 있음 → 80km/h 폴백
- `mWear`: 새 타이어가 1.0 (남은 수명 비율 방향) — 실차 확인 완료
- 고스트 차량 플래그 없음 → 속도 추정으로 정지/서행 차량 필터링. 프라이빗 연습/퀄리는 그래도
  유령 참가자를 내보내므로 트래픽 콜은 기본값이 레이스 세션 전용
- 출발 전 그리드 대기 중에도 세션 시계가 흐름 → 브리핑은 경과 시간이 아니라 게임 페이즈로
  레이스 스타트와 중간 합류를 가른다

## 이후 확장 아이디어

STT 양방향 무전, 멀티 스틴트 연료 계획 최적화.

## 크레딧

- 공유 메모리 플러그인/레이아웃: The Iron Wolf
- LMU 내장 공유 메모리 레이아웃 교차 확인: [pyLMUSharedMemory](https://github.com/TinyPedal/pyLMUSharedMemory)
  (MIT), [lmu-pitwall](https://github.com/Swizzjack/lmu-pitwall) (MIT) — 사실만 참고, 코드는 직접 작성
- 파이썬 구조체 매핑 참조: [pyRfactor2SharedMemory](https://github.com/TonyWhitley/pyRfactor2SharedMemory)
