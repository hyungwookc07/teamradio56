# LMU AI 크루치프 🏁🎙️

Le Mans Ultimate의 텔레메트리를 실시간으로 읽어, 상황을 **판단**하고 자연스러운
한국어 음성으로 크루치프 멘트를 재생하는 Windows 스탠드얼론 앱.

기존 Crew Chief류 앱과의 차이: 기계적인 반복 멘트가 아니라 **LLM 기반의 맥락 있는
판단형 멘트**를 지향합니다. "스포터/계산기"가 아니라 "판단하는 레이스 엔지니어".

> "연료는 10랩 분량인데 타이어가 8랩쯤이면 절벽이야. 9랩에 같이 해결하자."

## 특징

- **단방향 음성 출력 전용** — STT/대화 없음 (확장 가능하도록 설계만 분리)
- **UI 없음** — 콘솔 로그 + `config.yaml`로 운영
- **이원화된 멘트 생성**
  - 긴급 콜(트래픽/연료/박스/데미지/페널티): 사전 생성된 한국어 변형 풀 + 오디오 캐시 → 지연 0
  - 비긴급 멘트(랩 분석/전략/서사): Anthropic API(claude-haiku) 실시간 생성 → 3~5초 지연 허용
- **발화 억제 규칙** — 말할 필요 없으면 침묵. 랩마다 떠들지 않음. 유형별 쿨다운
- **멀티클래스 대응** — 상위 클래스(하이퍼카) 접근 예고 콜
- **게임 성능 보호** — 5Hz 폴링, 무거운 처리(LLM/TTS)는 별도 스레드, GPU 사용 로컬 TTS 금지

## 빠른 시작

설치(플러그인 포함)는 [docs/INSTALL.md](docs/INSTALL.md) 참고.

```bat
python -m venv venv
venv\Scripts\activate
pip install -r requirements.txt
copy config.yaml.example config.yaml
python main.py
```

게임 없이 테스트:

```bat
python tools\make_test_replay.py data\test.jsonl
python main.py --replay data\test.jsonl --speed 5
```

## 구조

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
events.py          # 이벤트 우선순위 큐 (중복 제거, 쿨다운)
voice.py           # 멘트 생성 (사전 생성 풀 / 실시간 LLM 이원화)
tts.py             # TTS 재생 스레드 + 오디오 캐시
config.py          # config.yaml 로드
```

## 개발 마일스톤

- [x] v0.1 — 공유 메모리 연결, 연료/랩타임/갭 콘솔 출력, 게임 미실행 시 대기, 리플레이 모드
- [x] v0.2 — 랩 완료 분석(연료/페이스) + 이벤트 큐 + 템플릿 멘트 TTS
- [x] v0.3 — 트래픽 분석기 + 사전 생성 변형 풀 + 오디오 캐시
- [x] v0.4 — 실시간 LLM 멘트 (Anthropic API, 페르소나/서사)
- [x] v0.5 — 타이어 분석기, 레이스 JSON 저장, PyInstaller 패키징

### 자연스러움 개선 (v0.6)

- [x] 트래픽 per-car 상태 머신 — 상태 전이 시에만 발화, 같은 차 멘트는 서사로 연결
  ("붙는다" → "옆이야" → "지나갔어"), 다중 차량은 위협도 순 한 문장 종합
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
- [x] 클래스 순위 — 멀티클래스에서 진짜 순위(클래스 내) 계산, 변동 시에만 콜
- [x] 라이벌 인텔 — 동클래스 경쟁자 피트 진입 감지(언더컷/오버컷 판단),
  클래스 앞뒤 차와 페이스 비교("랩당 0.7초 빨라, 8랩쯤 뒤에 온다")
- [x] 차량 컨디션 — 수온/유온/과열, 브레이크 온도 경고
- [x] 연료 세이브 코칭 — 노피트 경계 상황에서 목표 소모량 대비 델타 지시
- [x] 웻/드라이 크로스오버 — 노면 웻니스 기준 타이어 전환 판단 트리거

플러그인 한계로 미지원: LMU 가상 에너지(Virtual Energy), 날씨 예보, 하이브리드 배터리
— 표준 rF2 공유 메모리에 없음. LMU 전용 확장 플러그인 등장 시 확장 포인트.

이후 확장 아이디어: STT 양방향 무전, 멀티 스틴트 연료 계획 최적화.

## 요구사항

- Windows 10/11 (공유 메모리 모드) — 리플레이 모드는 OS 무관
- Python 3.11+
- LMU + [rF2 Shared Memory Map Plugin](https://github.com/TheIronWolfModding/rF2SharedMemoryMapPlugin) (The Iron Wolf)

## 크레딧

- 공유 메모리 플러그인/레이아웃: The Iron Wolf
- 파이썬 구조체 매핑 참조: [pyRfactor2SharedMemory](https://github.com/TonyWhitley/pyRfactor2SharedMemory)
