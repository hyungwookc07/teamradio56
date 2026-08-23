# teamradio56 — SimHub 플러그인 (C# 포팅)

파이썬 단독 앱을 SimHub 플러그인으로 옮기는 작업. **1단계 진행 중.**

SimHub은 호스팅(자동 실행 / 설정 UI / 배포)만 담당하고, 텔레메트리는
**공유 메모리에서 직접 읽는다.** LMU 전용 필드(덴트 존, 충격 크기,
pathLateral, 횡속도, 블루 플래그, 섹터 플래그)가 SimHub 정규화 계층에는
없기 때문 — 즉 포팅해도 기능 손실이 없다. 나중에 다른 게임을 지원할 때만
SimHub의 정규화 데이터를 추가로 쓴다.

## 구조

```
TeamRadio56.Core      SimHub 무관한 순수 로직 (텔레메트리/분석기/멘트/TTS)
TeamRadio56.SimHub    얇은 플러그인 래퍼 (IPlugin) — SimHub API 접촉면 최소화
```

접촉면을 좁힌 이유: 개발 환경에서 C# 컴파일 검증이 불가능해(빌드는 사용자
PC에서) SimHub API 관련 왕복을 최소화해야 하기 때문.

## 빌드

**필요한 것**: .NET SDK 8 이상 (또는 Visual Studio 2022) — `net48` 타깃이라
.NET Framework 4.8 개발 팩이 함께 필요하다. VS는 "「.NET 데스크톱 개발」
워크로드"를 설치하면 포함된다.

```powershell
cd D:\teamradio56\simhub
dotnet build -c Release
```

SimHub이 기본 경로가 아니면:

```powershell
dotnet build -c Release -p:SimHubPath="D:\SimHub"
```

빌드 산출물:
```
src\TeamRadio56.SimHub\bin\Release\net48\TeamRadio56.SimHub.dll
src\TeamRadio56.SimHub\bin\Release\net48\TeamRadio56.Core.dll
```

## 설치

두 DLL을 **SimHub 설치 폴더 루트**에 복사한다 (하위 폴더 아님):

```powershell
copy src\TeamRadio56.SimHub\bin\Release\net48\TeamRadio56.*.dll "C:\Program Files (x86)\SimHub\"
```

SimHub을 재시작하면 첫 실행 시 **"새 플러그인을 활성화할까요?"** 창이 뜬다 →
teamradio56 체크 후 활성화. (안 뜨면 SimHub → Settings → Plugins에서 수동 활성화.)

## 1단계에서 확인할 것

이 단계는 기능이 아니라 **파이프라인 검증**이 목적이다:

1. 플러그인이 SimHub에 로드되는가
2. 자동 생성된 rF2 구조체 레이아웃이 맞는가 (크기 대조)
3. LMU 공유 메모리를 직접 읽는가
4. 소리가 나가는가 (Windows 내장 TTS로 "Radio check")

**로그 파일**: SimHub 폴더의 `teamradio56.log` — SimHub 로그 API에 의존하지
않는 독립 채널이라, 플러그인이 로드만 되면 무조건 기록이 남는다.

기대 출력:
```
==== teamradio56 0.8.0-simhub-stage1 시작 ====
구조체 레이아웃 검증 통과 (Telemetry 241680B / Scoring 75312B / Extended 10152B)
초기화 완료. 로그 파일: ...
SimHub 게임 이름: 'LMU'            ← 실제로 뭐라고 나오는지 알려주세요
LMU 공유 메모리 연결됨
발화: Radio check. Team radio online.
[Le Mans] 페이즈 5 | P4 LMGT3 | 랩 3 | 1820m | 연료 87.0L | 210km/h | 차량 18대
```

**빌드 에러나 로그 이상은 그대로 붙여주세요.** 제가 컴파일 확인을 못 하는
환경이라, 에러 메시지가 유일한 피드백 채널입니다.

### 예상되는 첫 걸림돌

| 증상 | 원인/조치 |
|---|---|
| `SimHub을 찾을 수 없습니다` | `-p:SimHubPath="실제경로"` 로 지정 |
| `'GameData'에 'GameName' 정의가 없습니다` | SimHub 버전별 API 차이. `TeamRadio56Plugin.cs`의 `_loggedGameName` 블록(게임 이름 로깅, 진단용일 뿐)을 통째로 주석 처리하면 됨 |
| `net48 대상 팩이 없습니다` | VS Installer에서 ".NET Framework 4.8 targeting pack" 설치 |
| 로그에 `구조체 레이아웃 불일치` | 생성기 문제 — 출력된 크기를 알려주세요 |
| 플러그인 목록에 안 보임 | DLL을 SimHub **루트**에 뒀는지 확인, SimHub 재시작 |

## 다음 단계

- 2단계: 이벤트 버스 + 멘트 풀 + edge-tts 사전 캐시 + 무전기 효과 → 긴급 콜 동작
- 3단계: 분석기 이식 (트래픽/레이스컨트롤/차량상태/연료/타이어/페이스/라이벌/전략)
- 4단계: SimHub 설정 UI + LLM 멘트 + REST 보조 소스

파이썬 버전(저장소 루트)은 그동안 레퍼런스 겸 실차 튜닝용으로 유지한다.

## 구조체 재생성

`rf2data.py`가 바뀌면 C# 구조체를 다시 생성한다 (손으로 고치지 말 것):

```bash
python tools/gen_csharp_structs.py
```
