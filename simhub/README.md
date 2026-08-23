# teamradio56 — SimHub 플러그인 (C# 포팅)

파이썬 단독 앱을 SimHub 플러그인으로 옮기는 작업. **설정 UI 단계까지 완료.**

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

개발 환경에서 C# 컴파일 검증이 불가능하므로(빌드는 사용자 PC에서) 실패
지점을 의도적으로 줄였다:

- 구조체는 손으로 옮기지 않고 `rf2data.py`에서 **자동 생성** + 런타임 크기 대조
- 설정 UI는 **XAML 없이 코드로 조립** (x:Class 매칭, .g.cs 생성 실패 제거)
- 메뉴 아이콘은 **.resx 리소스 없이** base64 PNG를 코드에 내장
- 설정 저장은 외부 JSON 라이브러리 없이 **key=value 파일**

## 빌드

**필요한 것**: .NET SDK 8 이상 (또는 Visual Studio 2022) + .NET Framework 4.8
개발 팩. VS는 "「.NET 데스크톱 개발」 워크로드"에 포함돼 있다.

```powershell
cd D:\teamradio56\simhub
dotnet build -c Release
```

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

## 설정 화면

| 섹션 | 내용 |
|---|---|
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
5. LMU를 켜면 상태 램프가 초록으로 바뀌고 **실제 값**(트랙/순위/연료/속도)이 보이는가

**로그 파일**: SimHub 폴더의 `teamradio56.log` — SimHub 로그 API에 의존하지
않는 독립 채널이라, 플러그인이 로드만 되면 무조건 기록이 남는다.

기대 로그:
```
==== teamradio56 0.8.1-simhub-ui 시작 ====
설정 파일 없음 — 기본값 사용 (...\teamradio56.settings.txt)
구조체 레이아웃 검증 통과 (Telemetry 241680B / Scoring 75312B / Extended 10152B)
초기화 완료. 로그: ... / 설정: ...
SimHub 게임 이름: 'LMU'            ← 실제로 뭐라고 나오는지 알려주세요
LMU 공유 메모리 연결됨
발화: Radio check. Team radio online.
```

**빌드 에러나 화면 이상은 그대로 붙여주세요.** 제가 컴파일 확인을 못 하는
환경이라, 에러 메시지가 유일한 피드백 채널입니다.

### 예상되는 걸림돌

| 증상 | 원인/조치 |
|---|---|
| `SimHub을 찾을 수 없습니다` | `-p:SimHubPath="실제경로"` 로 지정 |
| `IWPFSettingsV2를 구현하지 않습니다` / 멤버 불일치 | SimHub 버전별 API 차이. 에러에 나온 멤버 이름을 알려주세요 (플러그인 파일 상단 3개 프로퍼티만 고치면 됨) |
| `'GameData'에 'GameName' 정의가 없습니다` | 진단용 로깅일 뿐 — `_loggedGameName` 블록을 통째로 주석 처리 가능 |
| `net48 대상 팩이 없습니다` | VS Installer에서 ".NET Framework 4.8 targeting pack" 설치 |
| 설정 화면 글자가 안 보임 | SimHub 테마와 색 충돌 — 알려주시면 색을 테마 상속으로 바꿉니다 |
| 로그에 `구조체 레이아웃 불일치` | 생성기 문제 — 출력된 크기를 알려주세요 |
| 플러그인 목록에 안 보임 | DLL을 SimHub **루트**에 뒀는지 확인, SimHub 재시작 |

## 다음 단계

- 이벤트 버스 + 멘트 풀(영어 1262개) + edge-tts 사전 캐시 + 무전기 효과 → 긴급 콜 동작
- 분석기 이식 (트래픽/레이스컨트롤/차량상태/연료/타이어/페이스/라이벌/전략)
- LLM 멘트 + REST 보조 소스(가상 에너지/날씨 예보)

파이썬 버전(저장소 루트)은 그동안 레퍼런스 겸 실차 튜닝용으로 유지한다.

## 생성물 재생성

`rf2data.py`가 바뀌거나 아이콘을 고칠 때 (손으로 수정하지 말 것):

```bash
python tools/gen_csharp_structs.py    # → Telemetry/RF2Data.Generated.cs
python tools/gen_icon.py              # → PluginIcon.cs
```
