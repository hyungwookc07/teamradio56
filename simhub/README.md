# teamradio56 — SimHub 플러그인 (C# 포팅)

파이썬 단독 앱을 SimHub 플러그인으로 옮기는 작업.
**설정 UI + 엔진 모드 완료 — SimHub만 켜면 전체 기능이 돕니다.**

SimHub은 호스팅(자동 실행 / 설정 UI / 배포)만 담당하고, 텔레메트리는
**공유 메모리에서 직접 읽는다.** LMU 전용 필드(덴트 존, 충격 크기,
pathLateral, 횡속도, 블루 플래그, 섹터 플래그, FCY 피트 오픈 상태)가 SimHub
정규화 계층에는 없기 때문 — 즉 포팅해도 기능 손실이 없다. 다른 게임으로
넓힐 때만 SimHub 정규화 데이터를 소스로 추가한다.

## 구조

```
TeamRadio56.Core      SimHub 무관한 순수 로직 (텔레메트리/설정/진단, 앞으로 분석기·멘트·TTS)
TeamRadio56.SimHub    얇은 플러그인 래퍼 (IPlugin/IDataPlugin/IWPFSettingsV2) + 설정 UI
```

Core가 SimHub을 참조하지 않으므로, 같은 Core로 나중에 단독 exe도 뽑을 수 있다.

### 컴파일 검증

**개발 환경에서도 전체 C#을 컴파일 검증한다** (SimHub 설치 불필요):

```bash
bash simhub/verify/build-check.sh
```

`verify/*.Stub`이 SimHub 인터페이스의 최소 스텁을 만들고, 그걸 참조해
Core와 플러그인을 빌드한다. 문법·타입·오탈자·시그니처 오류를 전부 잡는다.
**못 잡는 것은 실제 SimHub API와의 차이뿐** — 스텁이 곧 그 API에 대한
가정이므로, 가정이 틀리면 사용자 PC 빌드에서 드러난다.

그 외에도 실패 지점을 줄여뒀다:

- 구조체는 손으로 옮기지 않고 `rf2data.py`에서 **자동 생성** + 런타임 크기 대조
- 설정 UI는 **XAML 없이 코드로 조립** (x:Class 매칭, .g.cs 생성 실패 제거)
- 메뉴 아이콘은 **.resx 리소스 없이** base64 PNG를 코드에 내장
- 설정 저장은 외부 JSON 라이브러리 없이 **key=value 파일**

## 빌드

**필요한 것**: .NET SDK 8 하나뿐. Visual Studio도, .NET Framework 개발 팩도
필요 없다 (net48 참조 어셈블리를 NuGet으로 받도록 설정돼 있음).

```powershell
winget install Microsoft.DotNet.SDK.8     # 없으면 한 번만
# PowerShell 새로 열고
cd D:\teamradio56\simhub
dotnet build -c Release
```

winget이 없으면 https://dotnet.microsoft.com/download/dotnet/8.0 에서
**SDK x64** 설치 파일을 받는다.

SimHub이 기본 경로가 아니면:

```powershell
dotnet build -c Release -p:SimHubPath="D:\SimHub"
```

## 설치

두 DLL을 **SimHub 설치 폴더 루트**에 복사한다 (하위 폴더 아님):

```powershell
copy src\TeamRadio56.SimHub\bin\Release\net48\TeamRadio56.*.dll "C:\Program Files (x86)\SimHub\"
```

SimHub을 재시작하면 첫 실행 시 **"새 플러그인을 활성화할까요?"** 창이 뜬다 →
teamradio56 체크. (안 뜨면 SimHub → Settings → Plugins에서 수동 활성화.)

활성화되면 SimHub **좌측 메뉴에 "teamradio56"** 항목이 생긴다.

## 엔진 모드 — 지금 바로 전체 기능 쓰기

C# 분석기 이식이 끝나기 전까지, **플러그인이 검증된 파이썬 엔진을 자식
프로세스로 띄웁니다.** 설정 화면은 그대로 SimHub에서 쓰고, 실제 판단·발화는
파이썬이 합니다. 통신은 파일 두 개뿐이라 프로토콜도 의존성도 없습니다:

```
SimHub 설정 UI ──(teamradio56.settings.txt)──▶ 파이썬 엔진
SimHub 설정 UI ◀──(teamradio56.status.txt)─── 파이썬 엔진
```

엔진 수명은 SimHub에 묶입니다 — SimHub이 뜨면 엔진도 뜨고, 닫으면 같이
종료됩니다. 비정상 종료로 남은 프로세스는 다음 시작 때 자동 정리합니다.

### A. 소스로 바로 돌리기 (권장 — 빌드 불필요)

이미 pipenv 환경이 있으면 이게 제일 빠릅니다. 파이썬 실행 파일 경로만
알아내면 됩니다:

```powershell
cd D:\teamradio56
pipenv --venv          # 예: C:\Users\me\.virtualenvs\teamradio56-AbCdEf
```

설정 화면 **엔진** 섹션에 입력:

| 항목 | 값 |
|---|---|
| 모드 | `python` |
| 실행 파일 | `C:\Users\me\.virtualenvs\teamradio56-AbCdEf\Scripts\python.exe` |
| 추가 인자 | `"D:\teamradio56\main.py"` |

`[엔진 시작]` → 상태가 초록으로 바뀌고 "Radio check"가 들리면 성공.

### B. exe로 묶기 (파이썬 없는 PC 배포용)

```powershell
cd D:\teamradio56
pipenv run pyinstaller teamradio56.spec
```

`dist\teamradio56.exe` 와 그 폴더 내용을 SimHub 폴더의
`teamradio56-engine\` 안에 넣으면 **실행 파일 칸을 비워둬도** 자동으로 찾습니다.

### 확인

- 설정 화면 **엔진** 섹션에 "엔진 실행 중 · 경로"가 초록으로
- **상태** 섹션에 트랙/순위/연료/속도와 최근 무전 5줄
- `[엔진 로그]` 버튼 → 파이썬 쪽 로그 (`teamradio56.engine.log`)

설정을 바꾸면 **[엔진 재시작]**을 눌러야 반영됩니다.

## 설정 화면

| 섹션 | 내용 |
|---|---|
| 엔진 | 모드(python/builtin), 실행 파일, 추가 인자, [시작][중지][재시작][엔진 로그] |
| 상태 | 연결 램프, 현재 트랙/순위/랩/연료/속도, 최근 무전 5건, [테스트 발화] [로그 열기] [설정 파일 열기] |
| 음성 | 출력 on/off, 보이스 선택, 말 속도, 볼륨, 무전기 효과, 노이즈 강도 |
| 수다스러움 | quiet / normal / chatty 프리셋 (긴급 콜은 항상 나감) |
| 트래픽 | 나란히 판정 거리, 스타트 스포터 모드 길이, 좌우 반전, 레이스에서만 |
| HUD 대체 | 매 랩 랩타임, N랩마다 상황 리포트 |
| LLM | 사용 여부, API 키, 시간당 호출 예산 |
| 동작 | 주행 중에만 발화, 발화 로그 |

바꾸는 즉시 `teamradio56.settings.txt`(DLL 옆)에 저장된다.

## 이번 단계에서 확인할 것

1. 빌드가 통과하는가
2. SimHub 좌측 메뉴에 **아이콘과 함께** teamradio56이 뜨는가
3. 설정 화면이 그려지고, 값을 바꾸면 설정 파일에 반영되는가
4. [테스트 발화] 누르면 소리가 나는가
5. 엔진 경로를 넣고 [엔진 시작] → "엔진 실행 중"(초록)이 뜨는가
6. LMU를 켜면 상태 램프가 초록으로 바뀌고 **실제 값**(트랙/순위/연료/속도)과
   최근 무전이 보이는가 — 그리고 **실제 콜이 들리는가**

**로그 파일**: SimHub 폴더의 `teamradio56.log` — SimHub 로그 API에 의존하지
않는 독립 채널이라, 플러그인이 로드만 되면 무조건 기록이 남는다.

기대 로그:
```
==== teamradio56 0.9.0-simhub-engine 시작 ====
설정 파일 없음 — 기본값 사용 (...\teamradio56.settings.txt)
구조체 레이아웃 검증 통과 (Telemetry 241680B / Scoring 75312B / Extended 10152B)
초기화 완료. 로그: ... / 설정: ...
SimHub 게임 이름: 'LMU'            ← 실제로 뭐라고 나오는지 알려주세요
엔진 시작: C:\...\python.exe "D:\teamradio56\main.py" --settings "..." --status-file "..."
파이썬 엔진 모드 — 전체 기능 동작
```

**빌드 에러나 화면 이상은 그대로 붙여주세요.** 제가 컴파일 확인을 못 하는
환경이라, 에러 메시지가 유일한 피드백 채널입니다.

### 예상되는 걸림돌

| 증상 | 원인/조치 |
|---|---|
| `SimHub을 찾을 수 없습니다` | `-p:SimHubPath="실제경로"` 로 지정 |
| `IWPFSettingsV2를 구현하지 않습니다` / 멤버 불일치 | SimHub 버전별 API 차이. 에러에 나온 멤버 이름을 알려주세요 (플러그인 파일 상단 3개 프로퍼티만 고치면 됨) |
| `'GameData'에 'GameName' 정의가 없습니다` | 진단용 로깅일 뿐 — `_loggedGameName` 블록을 통째로 주석 처리 가능 |
| `dotnet: 명령을 찾을 수 없음` | SDK 설치 후 PowerShell을 새로 열어야 PATH가 잡힌다 |
| 설정 화면 글자가 안 보임 | SimHub 테마와 색 충돌 — 알려주시면 색을 테마 상속으로 바꿉니다 |
| 로그에 `구조체 레이아웃 불일치` | 생성기 문제 — 출력된 크기를 알려주세요 |
| 플러그인 목록에 안 보임 | DLL을 SimHub **루트**에 뒀는지 확인, SimHub 재시작 |
| "엔진을 찾을 수 없습니다" | 실행 파일 경로 확인. 소스 실행이면 venv의 `python.exe`를 지정하고 추가 인자에 `main.py` 전체 경로 |
| 엔진은 도는데 콜이 없음 | [엔진 로그]에서 파이썬 쪽 오류 확인 (오디오 캐시 미생성, 플러그인 DLL 미설치 등) |
| 소리가 두 번 들림 | 파이썬 앱을 따로도 띄웠는지 확인 — 엔진 모드에선 SimHub이 관리합니다 |

## C# 내장 엔진 (builtin 모드) — 포팅 현황

**판단 계층은 포팅 완료**: 이벤트 버스(쿨다운/우선순위/TTL), 상태(랩
히스토리/베이스라인), 분석기 9종(트래픽·스포터/레이스컨트롤/차량상태/
연료/페이스/타이어/라이벌/전략/리포터), 세션 브리핑, 멘트 풀(자동 생성),
보이스 워커 + 사전 캐시 오디오 재생, 캐시 미스 시 edge 런타임 합성
(EdgeTtsClient — 파이썬 edge-tts와 같은 서비스·같은 캐시 키라 결과가
서로 재사용된다; 오프라인이면 Windows TTS 폴백, 2분 서킷 브레이커).

**리플레이 회귀로 파이썬과 동작 일치가 검증된다**:

```bash
bash simhub/verify/replay-check.sh
```

파이썬 하니스(tools/replay_calls.py)와 C# 러너(tests/TeamRadio56.Replay)에
같은 녹화(data/replays/*.jsonl.gz)를 먹여 "버스가 수락한 이벤트"를
순서·타이밍·데이터까지 비교하고, 사전 캐시 멘트 전체 집합(1057개)도
diff한다. 분석기를 고치면 파이썬을 먼저 고치고 이 회귀로 C#을 맞춘다.

**아직 파이썬 엔진에만 있는 것** (builtin에서는 침묵):

- LLM 멘트 (전략 판단/브리지 후속/디브리핑) — lap_analysis 등은 템플릿 폴백만
- 런타임 합성분의 무전 효과 (캐시 항목은 효과 적용본, edge 합성 mp3는 원음)
- 한국어 멘트 (builtin은 영어 전용)
- 트레이닝(섹터 코치/트랙 히스토리), REST 보조 소스, 레이스 JSON 저장

그래서 **기본 모드는 여전히 python**이고, builtin은 위 항목이 필요 없는
배포 시나리오(캐시 오디오 + 판단 콜만)에서 이미 쓸 수 있다. 설정 화면
엔진 섹션에서 모드를 `builtin`으로 바꾸면 된다 — 오디오 캐시는 DLL 옆
`audio_cache\` 또는 `teamradio56-engine\audio_cache\`에서 자동으로 찾는다.

파이썬 버전(저장소 루트)은 레퍼런스 겸 실차 튜닝용으로 유지한다 —
버그는 파이썬에서 먼저 잡고 회귀로 C#을 따라오게 하는 게 빠르다.

## 오버레이 (SimHub 프로퍼티)

플러그인이 Dash Studio/LED에서 쓸 수 있는 프로퍼티를 노출한다
(`TeamRadio56Plugin.이름`으로 보임). **엔진 모드와 무관하게** 공유 메모리에서
직접 계산되므로 스포터 표시가 5Hz로 즉답한다.

| 프로퍼티 | 타입 | 의미 |
|---|---|---|
| `LastRadio` / `LastRadioAgeSec` | string / double | 마지막 무전 텍스트 / 경과 초 (자막 페이드용) |
| `SpotterLeft` / `SpotterRight` | bool | 좌/우 나란히 (유령 차 필터 적용) |
| `NearestAheadM` / `NearestBehindM` | double | 앞/뒤 최근접 실존 차 거리 m (없으면 -1) |
| `GapAheadSec` / `GapBehindSec` | double | 동클래스 앞/뒤 시간 갭 (없으면 -1) |
| `Position` / `ClassPosition` | int | 전체 / 클래스 순위 |
| `HasDamage` / `HasFuelIssue` | bool | 진행 중 이슈 (경고 칩·LED 트리거) |
| `Connected` / `InSession` / `EngineMode` | — | 상태 |

권장 오버레이 구성 (Dash Studio에서 10분 조립, 전부 선택식):

1. **무전 자막** — 하단 중앙 텍스트, `LastRadio` 바인딩,
   Visibility에 `[TeamRadio56Plugin.LastRadioAgeSec] < 6`
2. **스포터 바** — 화면 좌/우 가장자리 빨간 바,
   Visibility에 `SpotterLeft` / `SpotterRight`
3. **상태 칩** — 상단 `P[ClassPosition]`, `▲[GapAheadSec]s ▼[GapBehindSec]s`,
   `HasDamage`일 때만 DAMAGE 칩 표시

완성한 대시는 우클릭 → Export로 `.simhubdash`를 만들어 배포 zip에 동봉하면
유저는 더블클릭 임포트로 끝난다.

## 생성물 재생성

`rf2data.py`가 바뀌거나 아이콘을 고칠 때 (손으로 수정하지 말 것):

```bash
python tools/gen_csharp_structs.py    # → Telemetry/RF2Data.Generated.cs
python tools/gen_icon.py              # → PluginIcon.cs
```
