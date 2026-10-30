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

## 업데이트 (설정 화면에서 — 사용자)

0.16부터 플러그인이 **GitHub 릴리스에서 직접 업데이트**한다. 저장소·빌드 도구가
필요 없다.

1. SimHub → teamradio56 설정 화면 맨 아래 **업데이트** 섹션
2. **[업데이트 확인]** → 현재 버전 / 최신 버전, 설치된 음성 팩 / 최신 음성 팩이 보인다
   ("시작 시 확인"이 켜져 있으면 하루 한 번 SimHub 시작 15초 뒤 **확인만** 자동으로 한다.
   새 버전이 있으면 화면 위에 안내가 뜬다)
3. **[플러그인 업데이트]** → 끝나면 **SimHub을 재시작**해야 적용된다
4. **[음성 팩 업데이트]** (팩이 바뀐 릴리스에서만 켜짐, 약 89 MB) → 끝나면
   [엔진 재시작] 또는 SimHub 재시작

설치는 **버튼을 누르고 확인 창에서 [예]를 눌렀을 때만** 한다. 받는 동안 진행률이
보이고 [취소]할 수 있다.

**안에서 일어나는 일**

- 릴리스마다 붙는 `teamradio56-release.txt`(버전, zip 이름, sha256)를
  `https://github.com/hyungwookc07/teamradio56/releases/latest/download/` 에서 받는다.
  GitHub API·로그인을 쓰지 않으므로 호출 제한도 없다.
- 플러그인: zip 받기 → **sha256 검증** → 두 DLL이 다 있는지 확인 → 작업 폴더에 풀기 →
  실행 중인 `TeamRadio56.*.dll`을 `.old`로 **이름만 바꾸고**(로드된 DLL은 덮어쓸 수는
  없어도 이름은 바꿀 수 있다) 새 DLL을 넣는다. 중간에 하나라도 실패하면 바꾼 이름을
  전부 되돌리므로 **반쯤 설치된 상태는 남지 않는다.** `.old`는 다음 SimHub 시작 때 지워진다.
- 음성 팩: zip을 **전부 받고 sha256을 확인한 뒤에야** 오디오 캐시에 푼다. 오디오 캐시
  폴더의 `teamradio56_audio_pack.txt`가 설치된 팩 이름이다 (중간에 끊기면 "설치 미완료"로
  보이고 다시 받을 수 있다). 표식 없이 조립 자산 4개(`_seg1_*.wav`)만 있으면 손으로 넣은
  `kokoro-v015`로 본다.
- 받는 중 임시 파일은 SimHub 폴더의 `teamradio56_update\`(쓸 수 없으면 `%TEMP%`)에
  두고, 다음 시작 때 정리한다.

**처음 한 번은 손으로**: 0.15 이하에는 업데이터가 없다. 릴리스 페이지에서
`teamradio56-plugin-x.y.z.zip`을 받아 두 DLL을 SimHub 폴더에 복사하거나(SimHub 종료 후),
소스가 있으면 아래 `update.bat`을 쓴다. 그 다음부터는 설정 화면에서 된다.

| 메시지 | 원인/조치 |
|---|---|
| 인터넷에 연결할 수 없습니다 | 오프라인·방화벽·프록시. 시스템 프록시 설정을 그대로 쓴다. 안 되면 릴리스 페이지에서 직접 받기 |
| 릴리스 파일을 찾을 수 없습니다 | 새 릴리스를 막 올리는 중이거나 아직 업데이터 릴리스가 없음 — 잠시 후 다시 |
| 폴더에 쓸 권한이 없습니다 | SimHub 폴더가 쓰기 금지 (보통은 플러그인이 설정·로그를 쓰는 폴더라 괜찮다). SimHub을 한 번 관리자 권한으로 실행해 업데이트하거나 직접 복사 |
| 받은 파일이 손상되었습니다 | sha256 불일치 — 기존 플러그인은 그대로다. 다시 시도 |

화면에는 원인 분류만 나오고 자세한 내용(URL, 파일 이름, OS 오류)은 `teamradio56.log`에 남는다.

### 되돌리기 (.old)

업데이트 뒤 SimHub을 켰는데 플러그인이 안 뜨면(새 DLL이 로드조차 안 되면 정리가
돌지 않아 `.old`가 남아 있다):

1. SimHub 종료
2. SimHub 폴더의 `teamradio56_backup.txt`를 연다. 업데이터가 DLL마다 실제로 백업한
   파일 이름을 `백업 파일 -> 원래 이름` 줄로 적어 둔다. 예:
   ```
   TeamRadio56.Core.dll.old1 -> TeamRadio56.Core.dll
   TeamRadio56.SimHub.dll.old -> TeamRadio56.SimHub.dll
   ```
3. 각 줄마다 오른쪽 파일(새 DLL)을 지우고 왼쪽 파일을 오른쪽 이름으로 바꾼다
4. SimHub 시작, `teamradio56.log`를 알려주세요

`.old` 번호로 짐작하지 말 것: 지우지 못한 묵은 `.old`가 있으면 비어 있는 다음 번호
(`.old1`, `.old2`…)에 백업하므로 번호 없는 `.old`가 직전 버전이라는 보장이 없고, 번호는
DLL마다 다를 수 있다. 기록 파일이 없으면 탐색기에서 수정한 날짜를 본다 — 이름을 바꿔도
빌드 시각이 그대로라 같은 버전의 두 DLL은 몇 초 차이로 붙어 있다. 지금 DLL보다 오래된
것 중 가장 최근의 **짝**을 고른다. 다른 버전끼리 섞으면 플러그인이 뜨지 않을 수 있다.
플러그인이 정상으로 뜨면 시작할 때 `.old*`와 기록 파일을 지운다.

설정 화면에 **"되돌리기 실패"**가 뜨면(DLL 교체 도중 실패했고 원래대로 돌리지도 못한
경우) 플러그인 폴더에 새 DLL과 이전 DLL이 섞여 있을 수 있다. **SimHub을 재시작하기 전에**
화면(과 `teamradio56_backup.txt`)에 나온 파일을 위 3번처럼 바꾼다. 이 상태에선 같은
세션에서 플러그인 업데이트를 다시 하지 못하게 막아 둔다.

`.old`가 이미 지워졌으면 릴리스 페이지에서 이전 버전 zip을 받아 두 DLL을 복사하면 된다.
업데이터는 **더 높은 버전만** 제안하므로 이전 버전으로 내려가는 건 이 수동 복사뿐이다.

## 업데이트 (소스에서 빌드 — 개발자)

저장소를 받아 직접 빌드해 쓰는 경우는 루트의 **`update.bat` 더블클릭** 하나로
끝난다 — pull, 빌드, SimHub 폴더 DLL 복사까지. (PowerShell 기본 보안 정책이
.ps1 직접 실행을 막으므로, bat이 이번 실행에 한해 우회해서 돌려준다.)

터미널에서 돌리려면:

```powershell
cd D:\teamradio56
.\update.bat                                        # 기본 경로 설치면 이대로
.\update.bat -SimHubPath "D:\SimHub"                # 다른 경로면
```

SimHub이 켜져 있으면 DLL이 잠기므로 스크립트가 종료를 기다렸다가 복사한다.

## 릴리스 만들기 (메인테이너)

릴리스는 **GitHub Actions가 만든다** (`.github/workflows/release.yml`). PC에 SimHub이
없어도 된다 — Windows 러너가 SimHub을 무인 설치(버전별 캐시)하고 그 실제 어셈블리로
빌드한다. 스텁(`verify/build-check.sh`)으로 빌드한 DLL은 배포하지 않는다.

1. `TeamRadio56Plugin.Version`을 올린다 (`"0.17.0-…"` — **숫자 x.y.z가 게시된
   최신보다 커야** 사용자에게 업데이트가 뜬다. 뒤 꼬리표는 화면 표시용)
2. 커밋 → **main에 병합·push** (태그 커밋은 main에 있어야 게시된다)
3. 주석 태그를 만들어 push — 태그 메시지 첫 줄이 설정 화면 '변경 사항'이 된다:

```bash
git tag -a v0.17.0 -m "한 줄 요약"
git push origin v0.17.0
```

워크플로가 하는 일 (Actions 탭 → release 실행에서 단계별로 보인다):

1. **preflight** — 태그 = `v<Version 숫자부>`, 태그 커밋이 main에 있음, 게시된 최신보다
   높은 버전, 같은 릴리스 없음. `voice_lines/audio_pack.txt`가 가리키는 `audio-<pack>`
   릴리스가 공개돼 있고 zip 이름·크기·sha256과 **지금 코드가 찾는 멘트 집합**이 팩과 맞는지
   (안 맞으면 게시 거부 — 아래 음성 팩)
2. **gates** — `build-check.sh` + `replay-check.sh` (`TEAMRADIO56_STRICT=1`)
3. **build** (Windows) — SimHub 무인 설치(`simhub/ci/install-simhub.ps1`, zip sha256 고정,
   작업·드라이버 전부 끔) → `release.bat -Ci`로 실제 SimHub 빌드(참조 어셈블리 버전 확인) →
   플러그인 zip + 매니페스트 + 메타 → 아티팩트 `teamradio56-release-<x.y.z>`
4. **verify-artifact** — 게시될 바로 그 파일로 사용자 업데이트 흐름 재현
   (플러그인 교체 + `audio-<pack>`에서 음성 팩 설치)
5. **publish** — 같은 파일을 `gh release create --latest`로 게시하고
   `releases/latest/download/teamradio56-release.txt`가 새 버전을 가리키는지 확인.
   쓰기 권한은 이 잡에만 있다 (SimHub 설치 프로그램을 돌리는 Windows 잡에는 없다)

태그가 버전과 다르거나(예: `v0.16.0-rc1`) main에 없으면 실행이 실패한다 — 태그를 지우고
(`git push origin :refs/tags/<태그>`) 맞게 다시 만든다.

**시험 빌드 (dry run)**: Actions → release → Run workflow → `dry_run` 켠 채(기본) 실행.
게시 없이 빌드·검증까지 하고 결과를 아티팩트로 받을 수 있다. 다른 SimHub 버전은
`simhub_version`으로 (새 버전은 zip sha256을 경고로 찍는다 — 확인 후
`install-simhub.ps1` 표에 더한다). `dry_run`을 끄면 main에서 태그 push 없이 게시한다
(태그는 GitHub이 그 커밋에 만든다, `notes`가 변경 사항).

**캐시**: SimHub 설치본과 코코로 모델은 Actions 캐시에 둔다. 캐시는 ref별이라 태그 실행이
저장한 캐시는 다음 태그가 못 쓴다 — main에서 한 번 dry run을 돌려 데워 두면 이후 태그는
main 캐시를 쓴다.

### 음성 팩 — 전용 `audio-<pack>` 릴리스

음성 팩 zip(약 89 MB)은 플러그인 릴리스에 올리지 않는다. 팩마다 한 번 **`audio-<pack>`
프리릴리스**(latest가 되지 않는다 — `releases/latest`는 항상 플러그인 릴리스)에 올리고,
`voice_lines/audio_pack.txt`가 그 태그와 zip 이름·sha256·크기를 가리킨다. 플러그인
릴리스의 매니페스트는 `audio_tag`로 그 주소를 가리키기만 한다.

```
pack=kokoro-v015
tag=audio-kokoro-v015
asset=teamradio56_kokoro_v015_1of4.zip,<sha256>,<바이트 수>
...
```

- **멘트 집합이 바뀔 때만** 새 팩: Actions → **audio-pack** → Run workflow, `pack`에
  **새 ID**(예: `kokoro-v020`). 렌더 전 게이트 → 코코로 모델(sha256 고정) → 통문장·세그먼트
  렌더(약 1.5~2.5시간) → `tools/pack_audio.py`로 24 MiB 이하 zip 분할 → 렌더 후 게이트(실캐시
  100% + 팩 설치 흐름) → `audio-<pack>` 초안 → 새 `audio_pack.txt`를 **실행한 브랜치에 커밋**
  → 프리릴리스로 공개. `dry_run`은 렌더·zip·아티팩트까지만. 이미 있는 팩 ID는 거부한다.
- 팩 ID가 바뀐 사용자에게만 [음성 팩 업데이트]가 켜진다. `kokoro-v015`는 손으로 배포한 팩과
  **같은 멘트 집합**이라 그 ID를 유지한다 (`pack_audio.py`가 집합 해시로 확인 — 다른 집합에
  배포된 ID를 쓰면 거부).
- 멘트가 바뀌었는데 팩을 안 바꾸면 release preflight가 "멘트 집합이 팩과 다름"으로 게시를
  막는다 (그대로 내면 빠진 멘트가 edge로 나간다).
- **`audio-*` 릴리스는 지우거나 자산을 바꾸지 말 것** — 매니페스트가 sha256으로 고정하고
  있어 모든 사용자의 음성 팩 업데이트가 깨진다.
- 커밋이 막히면(보호 규칙 등) 초안을 지우고 끝난다 — 새 기술자는 아티팩트
  `teamradio56-audio-<pack>`의 `audio_pack.txt`에 있다.

### 필요한 저장소 설정

- Actions 사용, 워크플로 권한은 기본(읽기)이어도 된다 — 잡마다 필요한 권한을 적어 두었다
- audio-pack이 `github-actions[bot]`으로 실행 브랜치(보통 main)에 push한다. main에 보호
  규칙이 있으면 github-actions 우회를 허용하거나 커밋을 손으로 옮긴다
- (선택) Environment `release`에 Required reviewers를 걸면 게시 잡마다 승인을 받는다

### 대체 경로 — `release.bat` (실제 SimHub이 있는 Windows PC)

Actions를 쓸 수 없을 때. 필요한 것: .NET SDK 8, 실제 SimHub, 있으면 편한 GitHub CLI
(`winget install GitHub.cli` → `gh auth login`). 커밋을 push한 뒤 저장소 루트의
**`release.bat` 더블클릭** (또는 옵션과 함께):

```powershell
.\release.bat -Notes "한 줄 요약 — 설정 화면 '변경 사항'에 그대로 보인다"
.\release.bat -SimHubPath "D:\SimHub"                 # SimHub이 다른 경로면
.\release.bat -NoPublish                              # 파일만 만들고 손으로 올릴 때
.\release.bat -AudioDir "D:\packs"                    # 예전 방식 기술자(tag=이번 v태그)일 때만
```

버전 읽기 → 게시된 최신보다 높은지 → 실제 SimHub으로 `Release` 빌드 → 두 DLL zip·sha256 →
`audio_pack.txt`로 매니페스트 → `dist\release\<x.y.z>\` → gh가 로그인돼 있으면 게시
(커밋 push 여부, 같은 릴리스·태그 없음, `audio_tag` 릴리스의 zip 이름·크기 확인), 없으면
새 릴리스 페이지와 결과 폴더를 열고 올릴 파일을 안내한다 ("Set as the latest release" 켠 채).
Actions는 같은 스크립트를 `-Ci`(멈춤·게시 없음, `-OutDir`·`-MetaDir`·`-ExpectTag`·`-NotesFile`)로 부른다.

게시 후 확인: `https://github.com/hyungwookc07/teamradio56/releases/latest/download/teamradio56-release.txt`
가 새 버전을 보여주는지, 그리고 SimHub에서 [업데이트 확인].

### 매니페스트 형식 (`teamradio56-release.txt`)

UTF-8 `key=value` 줄, `#` 주석, 모르는 키는 무시 (뒤 버전이 키를 추가해도 앞 버전이
깨지지 않는다). 형식을 깨는 변경은 `format`을 올린다 — 모르는 format을 받은 플러그인은
"릴리스 페이지에서 직접 받으세요"라고 안내한다.

| 키 | 예 | 의미 |
|---|---|---|
| `format` | `1` | 형식 버전 |
| `version` | `0.16.0` | 숫자 x.y.z — 플러그인 `Version`의 숫자부와 비교 |
| `tag` | `v0.16.0` | 이 릴리스 태그 |
| `plugin_asset` / `plugin_sha256` | `teamradio56-plugin-0.16.0.zip` / 소문자 hex | 두 DLL zip |
| `audio_pack` | `kokoro-v015` | 음성 팩 ID (설치된 표식과 다르면 업데이트 제안) |
| `audio_tag` | `audio-kokoro-v015` | 팩 zip이 올라가 있는 릴리스 태그 |
| `audio_assets` / `audio_sha256` / `audio_size` | 콤마 구분, 같은 순서 | 팩 zip들 (`audio_size`는 선택 — 다운로드 전 크기 표시) |
| `notes` | 한 줄 | 설정 화면 "변경 사항" (선택) |

### 잘못된 릴리스를 냈을 때

- **아직 아무도 안 받았으면**: GitHub에서 그 릴리스를 지우거나 pre-release로 바꾸면
  `latest`가 이전 릴리스로 돌아간다 (`audio-*` 음성 팩 릴리스는 지우지 말 것).
  같은 버전으로 다시 내려면 **태그까지** 지운다 — 웹에서 릴리스만 지우면 태그가 남고,
  다시 만든 릴리스가 그 태그(예전 커밋)에 붙는다:
  `gh release delete v<x.y.z> --repo hyungwookc07/teamradio56 --cleanup-tag`
  (이미 릴리스만 지웠으면 `git push origin :refs/tags/v<x.y.z>`). 남아 있으면 스크립트가 멈춘다.
  Actions로 다시 낼 때도 같다 — 태그를 지운 뒤 다시 push한다.
- **음성 팩 릴리스가 latest가 됐으면**: `gh release edit <v태그> --latest` 로 플러그인
  릴리스를 되돌리고 `gh release edit audio-<pack> --prerelease --latest=false`.
- **audio-pack이 기술자는 커밋했는데 공개에 실패했으면**(초안으로 남음):
  `gh release edit audio-<pack> --draft=false --prerelease --latest=false`. 그 전까지
  release preflight가 게시를 거부한다.
- **이미 받은 사용자가 있으면**: 업데이터는 내려가는 버전을 제안하지 않으므로 고친
  버전을 **더 높은 번호로** 다시 낸다. 플러그인이 아예 안 뜨는 사용자는 위
  [되돌리기 (.old)](#되돌리기-old)로 직전 버전을 살린다.

### 검증

`verify/replay-check.sh`가 릴리스 도구도 검사한다: 음성 팩 기술자와 매니페스트를 Core
파서(`ReleaseManifest`)로 대조하고, pwsh가 있으면 `ps-check.ps1`로 Windows PowerShell 5
호환(파싱, 7 전용 문법, BOM)을 본 뒤 `release.ps1`을 검증 모드(`-TestDllDir`, 빌드·게시
없음)로 돌려 그 결과 폴더를 릴리스 서버처럼 내놓고 사용자 업데이트 흐름(확인 → DLL 교체 →
`.old` 정리)을 그대로 재현한다. `TEAMRADIO56_AUDIO_DIR`에 팩 zip 폴더를 주면 음성 팩 설치까지
돈다(`--audio-dir`로 `audio-<pack>` 릴리스 자리에서 설치). 리눅스에 pwsh가 없으면
`dotnet tool install -g PowerShell`.

`release.ps1 -Ci` 검증 모드(메타 파일·태그 대조·한글 요약 파일, 태그 불일치가 멈춤 없이
실패하는지)와 `tools/pack_audio.py selftest`(분할 경계·zip 이름·기술자 갱신·verify 거부)도
돈다. CI는 환경 변수로 더 엄격하게 돌린다:

| 변수 | 뜻 |
|---|---|
| `TEAMRADIO56_STRICT=1` | pwsh·numpy/soundfile이 없어 건너뛰던 검사를 실패로 |
| `TEAMRADIO56_REQUIRE_CACHE=1` | `audio_cache/*_rfx3.wav`가 없으면 실패 (실캐시 Resolve 필수) |
| `TEAMRADIO56_AUDIO_DIR=폴더` | 음성 팩 zip 폴더 — `release.ps1 -AudioDir` 대조 + 팩 설치 흐름 |

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
종료됩니다. SimHub이 비정상 종료해도 엔진은 Windows 작업 개체와 부모 PID
감시로 함께 종료되고, 이전 버전이 남긴 엔진은 다음 시작 때 명령줄로
찾아 정리합니다.

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
| 업데이트 | 현재/최신 버전, 음성 팩, [업데이트 확인] [플러그인 업데이트] [음성 팩 업데이트] [취소] [릴리스 페이지], 시작 시 확인 |

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
| 소리가 두 번 들림 / 목소리가 겹침 | 0.14부터 구조적으로 차단됨: 독립 실행 엔진(launch_with_lmu.bat)은 플러그인을 감지하면 물러나고, 플러그인 엔진은 SimHub과 함께 종료됩니다. 그래도 겹치면 작업 관리자에서 python.exe가 여러 개인지 확인 — 이전 버전이 남긴 프로세스면 재부팅 한 번으로 정리됩니다. 플러그인을 쓴다면 Steam 시작 옵션의 launch_with_lmu.bat은 지우세요 |

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
순서·타이밍·데이터까지 비교하고, 사전 캐시 멘트 전체 집합(en 1223개)과
문장 세그먼트 집합(단계 1~2 + 트랙 이름 목록, 2327개), 세그먼트 조립 출력 바이트도
diff한다. 세션 시작 브리핑도 양쪽 러너가 첫 틱에 내보내 en/ko 문장을 대조한다.
리플레이마다 en 발화가 전부 통문장 클립이나 문장 세그먼트로 나가는지도 확인한다
(`--coverage --tier 2 --min 100`, 미스면 빠진 문장을 출력). 브리핑 첫 문장의
트랙 이름이 voice_lines/track_names_en.txt에 없으면 점수에서 빼고 따로 경고한다.
`race_end_*` 리플레이는 체커 이후까지 가는 짧은 합성 레이스로, 규칙대로 계산한 정답
(`.truth.json`)과 마지막 랩·체커 콜, 남은 랩 트레이스(`--race-end-trace`, 모든 리플레이에서
파이썬 ↔ C# 대조)를 tools/race_end_oracle.py가 대조한다
(생성기 tools/make_race_end_replays.py, 게이트는 `--check`로 재생성 대조).
`race_end_restart`는 같은 세션에서 ET만 되감아 두 번 달린다 — 러너는 상태가 리셋되면
버스 대기열을 비운다(라이브 워커는 이전 레이스 콜을 진작 다 꺼냈다).
분석기를 고치면 파이썬을 먼저 고치고 이 회귀로 C#을 맞춘다.

**아직 파이썬 엔진에만 있는 것** (builtin에서는 침묵):

- LLM 멘트 (전략 판단/브리지 후속/디브리핑) — lap_analysis 등은 템플릿 폴백만
- 런타임 합성분의 무전 효과 (캐시 항목은 효과 적용본, edge 합성 mp3는 원음)
- 트레이닝(섹터 코치/트랙 히스토리), REST 보조 소스, 레이스 JSON 저장

한국어 멘트는 builtin에도 이식됐다 (Messages.cs + ko 멘트 풀 — kokoro 캐시가
영어 전용이라 ko는 edge 합성으로 발화). 리플레이 회귀는 en/ko 양쪽을 문장
단위까지 파이썬과 대조한다.

그래서 **기본 모드는 여전히 python**이고, builtin은 위 항목이 필요 없는
배포 시나리오(캐시 오디오 + 판단 콜만)에서 이미 쓸 수 있다. 설정 화면
엔진 섹션에서 모드를 `builtin`으로 바꾸면 된다 — 오디오 캐시는 DLL 옆
`audio_cache\` 또는 `teamradio56-engine\audio_cache\`에서 자동으로 찾는다.

파이썬 버전(저장소 루트)은 레퍼런스 겸 실차 튜닝용으로 유지한다 —
버그는 파이썬에서 먼저 잡고 회귀로 C#을 따라오게 하는 게 빠르다.

### 배포 오디오 캐시 (`audio_cache\`)

영어 kokoro 팩은 두 층이다. 런타임 순서: 통문장 클립 → 문장 세그먼트 조립
→ 이전에 저장된 edge mp3 → edge 실시간 합성 → Windows TTS.

| 파일 패턴 | 내용 | 만드는 명령 |
|---|---|---|
| `*_rfx3.wav` | 통문장 (무전 효과 적용, 8kHz PCM16) — 멘트 풀 + 소수 조합 멘트 | `python tools\pregen_audio.py --engine kokoro` |
| `*_seg1.wav` | 문장 세그먼트 (무전 효과 적용, 8kHz μ-law) — 숫자 슬롯 멘트용 | `python tools\pregen_audio.py --segments --tiers 1,2 --tracks voice_lines\track_names_en.txt` |
| `_seg1_*.wav` | 조립 자산 4개 (클릭 / 간격 casual·urgent / 스켈치) | `--segments`가 마지막에 씀 |

- 배포 zip에는 위 세 패턴만 넣는다. 효과 전 원본 `*.wav`(이름이 md5 20자로
  끝나는 파일)는 재생성용이라 **배포하지 않는다**.
- 세그먼트는 멘트의 문장이 **전부** 있을 때만 쓰인다 (한 멘트 안에서 목소리가
  섞이지 않게). 빠진 문장은 SimHub 로그에 `코코로 문장 미스 [톤] 문장`으로
  처음 20건만 남는다 — 그 문장을 사전 생성 도메인에 추가하면 된다.
- 조립 자산이 없으면 세그먼트 조립은 통째로 꺼진다 (로그 `조립 자산 없음`).
- 세그먼트 렌더 규약(속도/효과/간격)을 바꿔 다시 배포할 땐
  `radio_assemble.SEG_VERSION`과 `VoiceCache.SegSuffix`를 함께 올린다.
- 대량 생성 전에 `--ab "멘트" --tone casual --ab-out ab`로 통문장/조립본을
  귀로 비교한다.

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
