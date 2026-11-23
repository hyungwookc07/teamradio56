# 데이터 소스 구분 원칙 — 공유 메모리 vs LMU REST API

두 소스는 특성이 달라 역할을 명확히 나눈다. 새 기능이 데이터를 필요로
하면 이 표부터 보고 소스를 정한다.

## 특성 비교

|                | 공유 메모리 (LMU 내장 / rF2 플러그인) | LMU 내장 REST API                  |
|----------------|-----------------------------------|-----------------------------------|
| 갱신 주기       | 텔레메트리 ~50Hz(LMU 내장 ~100Hz), 스코어링 ~5Hz | 요청 시 생성 (우리는 3초 폴링)      |
| 읽기 비용       | RAM 복사, 마이크로초. 게임 영향 0  | 게임 프로세스가 JSON 생성 — 남용 시 게임 CPU 소모 |
| 지연           | 물리 엔진 직결, 사실상 0           | 폴링 간격 + UI 갱신 주기            |
| 데이터 성격     | rF2 유산 필드 (물리/원시값)        | LMU UI가 쓰는 값 (게임 고유 개념)   |
| 정확도 리스크   | LMU가 일부 필드를 부실하게 채움 (estLap 동일값, 섹터 플래그 쓰레기, pit_limit 0 등 — [DEVELOPMENT.md](DEVELOPMENT.md#공유-메모리-미지원-항목과-실차-특성) 참고) | UI에 보이는 값 그대로라 개념적으론 정확. 단 스키마 비공개 — 게임 패치로 깨질 수 있음 |
| 신뢰 전략       | 실차 검증된 필드만 사용, 방어적 파싱 | 자동 비활성 + getter는 None 허용 필수 |

## 배분 원칙

1. **초 단위로 변하고 반응이 급한 것 → 공유 메모리.**
   트래픽/근접 콜, 충격, 요레이트, 조향, 공기압, 피트 리미터처럼
   1~2초 늦으면 가치가 없는 데이터는 전부 메모리.
2. **LMU 고유 개념이라 메모리에 없는 것 → REST.**
   가상 에너지, 날씨 예보, 피트 전략(주유량/타이어 선택). 어차피
   분 단위로 변하는 전략 정보라 3초 폴링으로 충분.
3. **양쪽에 다 있는 것은 메모리가 기본.** 두 소스를 섞으면 불일치가
   생긴다 (예: 순위가 서로 다른 시점). REST의 중복 데이터는 교차 검증과
   부실 필드 대체 후보로만 쓴다.
4. **메모리 필드가 부실한 게 확인되면, REST에 같은 정보가 있는지 먼저
   확인하고 명시적으로 교체한다.** (예: pit_limit=0 문제 — REST 세션
   정보에 있으면 그쪽으로.) 암묵적 폴백 금지 — 어디서 온 값인지 항상
   추적 가능해야 한다.
5. **REST 소비자는 None을 항상 처리한다.** 게임 미실행/포트 변경/패치로
   언제든 사라질 수 있는 소스다. REST가 없어도 앱의 모든 기존 기능은
   동작해야 한다 (REST는 부가 기능 전용).

## 현재 배분표

| 데이터                          | 소스   | 소비자                     | 상태 |
|--------------------------------|--------|---------------------------|------|
| 차량 위치/갭/순위/클래스        | 메모리 | traffic, rivals, state    | 운영 |
| 요레이트/횡속도/조향/충격/덴트/공기압 | 메모리 | health                | 운영 |
| 프론트 윙 높이(mFrontWingHeight) | 메모리 | health (속도 구간별 비교) | 운영 (LMU에서 동적인지 실차 확인 필요 — 0·상수면 조용) |
| 연료(리터)/타이어/온도          | 메모리 | fuel, tyres, health       | 운영 |
| 플래그(FCY/섹터/블루)/페이즈    | 메모리 | racecontrol               | 운영 |
| 날씨 현재값(강수/웻니스)        | 메모리 | strategy                  | 운영 |
| **가상 에너지**                 | REST   | fuel (에너지 기반 윈도우)  | 스키마 확정 대기 |
| **날씨 예보**                   | REST   | strategy (선제 웻 콜)      | 스키마 확정 대기 |
| **피트 전략(주유/타이어/수리)** | REST   | 피트 전 브리핑             | 스키마 확정 대기 |
| 세션/순위 (REST 중복분)         | REST   | 교차 검증 전용             | 보류 |

스키마 확정 절차: 게임 실행 중 `tools/probe_rest.py` → `data/rest_probe/`
응답 확보 → `resttelemetry.py`의 TODO getter 확정 → 소비자 연결.

## LMU 내장 공유 메모리 (`LMU_Data`)

LMU 1.2부터 게임이 rF2 플러그인 없이 직접 공유 메모리를 낸다. SimHub 플러그인(C#)은 이걸
우선으로 읽고, 없으면 예전처럼 rF2 Shared Memory Map Plugin 버퍼를 읽는다. 파이썬 앱은
지금도 rF2 버퍼만 읽는다.

**출처와 신뢰도.** 공식 헤더(S397 `SharedMemoryInterface`)는 손에 없고 게임을 돌려 보지도
못했다. 레이아웃은 독립된 공개 구현 둘이 일치하는 사실만 썼다:
[pyLMUSharedMemory](https://github.com/TinyPedal/pyLMUSharedMemory) `lmu_data.py` @e9b71b6
(MIT)와 [lmu-pitwall](https://github.com/Swizzjack/lmu-pitwall) `bridge/src/shared_memory`
@a171b80 (MIT, LMU 1.4에서 실측했다고 밝힘). 차량별 레코드는 이 저장소의 rF2 구조체와도
같다(3중 일치). TinyPedal(GPL-3.0)은 동작 방식만 참고했다. kerb(MIT)는 내장 메모리 구현이
없고, hypertrace는 소스·라이선스가 없어 쓰지 않았다. **코드는 복사하지 않았다** — 필드
순서·타입·크기와 매핑 이름 같은 사실만 옮겨 직접 썼다.

| 항목 | 내용 |
|---|---|
| 매핑 | 이름 있는 파일 매핑 하나 `LMU_Data` (접두어·PID 없음). Extended/Rules/Weather 같은 다른 버퍼, 뮤텍스·이벤트 없음 |
| 여는 법 | `MemoryMappedFile.OpenExisting("LMU_Data", Read)`, 창 크기 0. **절대 만들지 않는다** (0으로 채운 가짜 매핑이 생김) |
| 게임 설정 | Settings → Gameplay → **Enable Plugins** 켜고 게임 완전 재시작 (TinyPedal·lmu-pitwall·HyperTrace README가 일치, pitwall은 꺼져 있으면 매핑이 없다고 실측) |
| 크기 | 324820 B 이상 (C++ 꼬리 패딩 324824도 허용) |
| 정렬 | `#pragma pack(4)`, 리틀 엔디언, 문자열은 NUL로 끝나는 고정 `char[N]` |
| 최상위 | generic 0 (332) · paths 332 (1300) · scoring 1632 (126832) · telemetry 128464 (196356) |
| 차량 배열 | 104칸 (rF2는 128). `S(i) = 2192 + 584·i`, `T(i) = 128468 + 1888·i`, 휠 `T(i) + 848 + 260·w` |
| 레코드 | ScoringInfo 548 · VehicleScoring 584 · VehicleTelemetry 1888 · Wheel 260 — rF2 V01과 크기·앞부분이 바이트까지 같고, LMU는 끝의 `mExpansion`에 필드를 추가했을 뿐 |

전체 필드 표는 기계가 읽는 명세 [lmu_native_layout.json](lmu_native_layout.json)에 있다
(스냅샷 필드 ↔ 원천 필드 매핑 포함).

### 읽는 방식 (`LmuNativeReader`)

- 게임이 제자리에 쓰고 버전 카운터가 없다. 감시 값(gameVersion, mCurrentET, mNumVehicles,
  텔레메트리 헤더 3바이트)을 읽고 → 필요한 범위만
  복사(ScoringInfo, 차량 n대, 플레이어 텔레메트리 슬롯) → 감시 값과 플레이어 슬롯 앞
  24바이트를 다시 읽어 원시 비트를 비교한다. 다르면 최대 4번 다시. 다 실패하면 그 틱은
  직전 정상 프레임으로 스냅샷을 다시 만든다 (엔진은 null을 연결 끊김으로 보고 세션을
  초기화하므로). 최선 노력일 뿐 완전한 일관성은 아니다. SME_UPDATE_SCORING/TELEMETRY는 의미가
  확인되지 않아 이 비교에 넣지 않는다 (매 틱 바뀌는 값이면 모든 프레임이 버려질 수 있다) — 활성
  판정("바뀌면 살아 있음")에만 쓴다.
- 복사한 조각을 **기존 rF2 구조체로 마샬링**해 rF2 버퍼 모양으로 조립하고 **기존
  `SnapshotBuilder.Build`를 그대로** 부른다. 단위·의미가 구성상 rF2 경로와 같다.
- 플레이어 슬롯은 스코어링의 `mIsPlayer` 차량 `mID`로 찾는다 (두 배열은 인덱스가 맞지
  않는다). `playerVehicleIdx`는 mID가 맞을 때만 쓰는 빠른 길.
- 차량 수 일치(`mNumVehicles == activeVehicles`)는 요구하지 않는다 (차가 들어올 때 잠깐 다름).
- 연결: 매핑 열림 + gameVersion > 0 + mCurrentET나 SME_UPDATE_SCORING이 5초 안에 바뀜 +
  5초 안에 정상 프레임.
- 프레임 버림: mNumVehicles가 0~104 밖, activeVehicles > 104, 시간/트랙 길이가 유한하지 않음.
- 값 검증(가드): 플레이어가 차고 밖인 첫 프레임들에서 플레이어 mID 일치, 타이어 온도
  200~600 K, 공기압 0~1000 kPa, 마모 0~1, 유한값. 3프레임 연속 어긋나면(또는 슬롯을
  25프레임 못 찾으면) 그 매핑을 쓰지 않는다. 한 번 실패한 뒤 다시 열면 검증을 통과해야 쓴다.
- 시작 시 `LmuNativeLayout.Verify()`가 상수끼리, 그리고 rF2 구조체 오프셋(`Marshal.OffsetOf`)과
  대조한다. 어긋나면 내장 경로만 끈다.

### 원천 선택 (`TelemetrySource`)

1. 내장 메모리가 쓸 만하면(위 조건 + 가드 실패 없음) 내장. `$rFactor2SMMP_Extended$`도 열려 있으면
   내장 메모리에 없는 **세 필드만** 거기서 보충한다: `Session.PitSpeedLimitKmh`(mCurrentPitSpeedLimit),
   `StatusMessage`(mStatusMessage), `HistoryMessage`(mLastHistoryMessage). 없으면 0 / 빈 문자열
   (rF2 경로에서 Extended가 없을 때와 같음 — 피트 리미터는 80 km/h 기본값, 페널티 콜은 종류 없이).
   그 밖의 모든 필드는 내장 메모리에서 온다.
2. 아니면 rF2 플러그인 경로를 예전 그대로 (DLL만 있는 사용자는 동작이 같다).
3. 둘 다 아니면 연결 없음.

쓰던 원천은 5초 동안 멈추거나 사라질 때까지 유지된다(각 리더의 연결 판정이 5초 기준). 원천이
바뀔 때마다 로그에 한 줄(`텔레메트리 소스: …`, gameVersion 원시값 포함), 가드 실패는 경고로 남고
설정 화면 상태에도 나온다.

진단: 내장 메모리를 쓰는 중에 rF2 DLL 버퍼도 있으면 값 검증 통과 약 5초 뒤(25틱) **한 번** rF2 버퍼도
읽어 두 스냅샷을 `SnapshotCompare`로 대조하고 결과만 로그에 남긴다 (일치 / `교차 확인: … 다름 N건`).
엔진에는 넘기지 않으므로 한 스냅샷 안에서 원천이 섞이지 않는다. 실차 확인용.

### 내장 메모리에 없는 것 (gap)

- 피트 제한속도, 상태/히스토리 메시지(페널티 사유) — 위처럼 rF2 Extended로만 보충 가능.
  LMU REST API에 있을 수도 있지만 확인 안 됨. 페널티 **개수**는 `mNumPenalties`로 그대로 있다.
- `scoringStream`(64 KiB, 절대 62928)은 형식 미공개라 읽지 않는다.
- 그 밖의 Extended/Rules/Weather 데이터는 스냅샷이 원래 쓰지 않는다.

### 확인 안 된 것

SME_* 값의 의미(언제 바뀌고 지워지는지), gameVersion 인코딩(pitwall은 14000 = 1.4, TinyPedal은
첫 자리만 주 버전 — 우리는 "0보다 큼"만 보고 원시값을 로그), `scoringStreamSize` 12바이트의
해석, 1.4 이전의 `playerVehicleIdx`, 내장 경로에서도 `mCurrentSector < 0`이 피트 레인인지(게임이
쓰는 같은 값이라 거의 확실). **출시 전에 실제 LMU 1.2+에서 DLL을 깐 채로 두 원천의 스냅샷을
나란히 로그로 비교해 확인해야 한다.**

### 두 경로에 공통인 기존 의미 문제 (이번 작업 범위 밖, 손대지 않음)

플러그인이 게임의 같은 구조체를 복사하므로 rF2 경로에도 똑같이 해당한다.

- 섹터 옐로: `RaceControlAnalyzer.CheckSectorYellow`(와 `analyzers/racecontrol.py`)는 `0 이하 → 1..2`
  전이만 인정한다. pitwall은 LMU에서 "깨끗함"이 11이라고 실측했고 TinyPedal은 `== 1`을 본다.
  깨끗함이 11이면 섹터 옐로 콜이 나갈 수 없다 (제안: `was != 1 && flag == 1`).
- `mBrakeTemp`: 헤더 주석은 섭씨지만 TinyPedal·pitwall은 273.15를 뺀다(켈빈). 우리는 섭씨로
  그대로 쓴다 — 브레이크 경고 문턱이 약 273도 어긋났을 수 있다.
- 문자열: 우리는 ISO-8859-1, 다른 구현은 UTF-8 (비ASCII 이름에서만 차이).

### 검증

`tools/lmu_native_blob.py`가 명세만 보고(C# 코드와 독립) 합성 덤프와 기대 스냅샷을 만들고,
러너의 `--lmu-native-dump`(실제 리더)·`--rf2-dump`(같은 상태의 rF2 버퍼) 출력과 대조한다.
원천 선택·대체 규칙은 `--lmu-native-selftest`. 모두 `replay-check.sh`에서 돈다. 명세 자체 검사
(`verify-spec`: pack(4) 재계산, rf2data.py와 공통 필드 대조)는 `build-check.sh`에서도 돈다.
