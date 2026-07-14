# 전체 구조

## 계층 다이어그램

```
[데이터 소스 계층]
  rf2data.py          ctypes 구조체 (플러그인과 바이트 일치 — 수정 금지)
  telemetry.py        공유 메모리 mmap 읽기 → Snapshot (순수 dict, JSON 직렬화 가능)
                      ReplayTelemetry(--replay) / SnapshotRecorder(--record)
  resttelemetry.py    LMU 내장 REST 폴러 (3초, 자동 비활성) — 가상 에너지/예보/피트 전략

[상태 계층]
  state.py            SessionState — 랩 히스토리(LapRecord), 이슈(issues),
                      레이스 서사(narrative), 클래스 순위/동클래스 갭 계산
                      → LLM 프롬프트의 문맥이 여기서 나온다

[분석 계층]  (Snapshot + SessionState → Event)
  analyzers/
    traffic.py        per-car 상태 머신, 랩핑/백마커/정지차, 다중 차량 종합
    racecontrol.py    페이즈/플래그(FCY·섹터·블루)/패널티(메시지 파싱)/
                      마일스톤/피트 리미터/클래스 순위 변동
    health.py         충격→자동 점검 리포트, 얼라인/윙/리어 불안정, 슬로우 펑처, 온도
    fuel.py           소모 평균/피트 윈도우/세이브 코칭
    tyres.py          마모 수명/온도 불균형/펑크
    pace.py           랩타임 편차, 갭 추세, 배틀 갭 리포트
    rivals.py         동클래스 피트(언더컷)/페이스 비교 인텔
    strategy.py       LLM 전략 트리거 (피트 윈도우/갭 반전/강수/웻 크로스오버)
    reporter.py       HUD 대체 정기 무전 (옵션, 기본 꺼짐)
  training.py         섹터 델타 코치 / 트랙별 장기 추세 / 세션 디브리핑

[이벤트 계층]
  events.py           Event(type, priority, data|message, dedup, ttl, tone,
                      bridge, valid_fn), EventBus — 우선순위 힙 + 유형별
                      쿨다운(COOLDOWN_KEY) + TTL/유효성 검사 + 긴급 인터럽트 신호

[멘트 계층]
  voice.py            VoiceGenerator.text_for(Event) → (텍스트, 소스)
                        ① ev.message 있으면 그대로 (분석기가 조립한 문장)
                        ② 렌더러 → PhrasePool (사전 캐시 풀, 이산 슬롯)
                        ③ NONURGENT → CrewChiefLLM (페르소나+상황 프롬프트,
                           캐싱, 시간당 예산, PASS=침묵, 실패→템플릿 폴백)
                      bridge_text() — 긴급 콜 뒤 LLM 후속
  voice_lines/urgent_ko.yaml   풀 × 톤(casual/urgent) 변형 (1549 조합)

[출력 계층]
  tts.py              VoiceWorker 스레드: bus.pop → text_for → synth → play
                      엔진: Edge/ElevenLabs (+RadioFXEngine 래퍼) — 파일 캐시
                      AudioPlayer(pygame, 랜덤 지연/볼륨, 긴급 인터럽트)
                      SpeechLogger(data/speech_log.jsonl)
  radiofx.py          무전기 효과 DSP (새추레이션→밴드패스→노이즈→스켈치)

[오케스트레이션]
  main.py             CrewChiefApp — 5Hz 폴링 루프, 세션 라이프사이클
                      (시작 브리핑 / 종료 시 디브리핑·저장·전 분석기 reset),
                      mInRealtime 게이팅, 콘솔 상태 출력
  config.py           DEFAULTS + config.yaml 딥머지
```

## 스레드 모델

| 스레드 | 역할 | 블로킹 규칙 |
|---|---|---|
| 메인 (5Hz) | 폴링→분석기→이벤트 push | 무거운 작업 금지 — push는 논블로킹 |
| voice-worker | 이벤트 pop→LLM/TTS/재생 | 여기서만 네트워크/재생 대기 |
| bridge-llm (일회성) | 긴급 콜 후속 LLM 생성 | 완료 시 bus에 push, 상황 종료면 폐기 |
| rest-poller | REST 3초 폴링 | 실패 시 자동 비활성 |

## 이벤트 수명 주기

분석기 push → (dedup 검사 → 쿨다운 검사[CRITICAL은 절반 경과 시 통과])
→ 우선순위 힙 대기 → worker pop 시 TTL/valid_fn 재검사 (낡은 정보 폐기)
→ 텍스트 생성 (침묵 가능) → 발화 로그 → 재생 (CRITICAL 대기 중이면
저우선순위 재생 중단)

## 새 콜을 추가하는 법

1. `events.py`에 EventType 추가 (+독립 쿨다운이 필요하면 COOLDOWN_KEY/config)
2. 데이터가 없으면 `telemetry.py`에서 Snapshot에 노출 (rf2data 필드 확인,
   REST 데이터면 `docs/DATA_SOURCES.md` 원칙 참고)
3. 해당 분석기(없으면 신설)에서 조건 감지 → Event push
   - 지연이 치명적이면 사전 캐시 풀: `voice_lines/urgent_ko.yaml`에 풀 추가
     + `voice.py` 렌더러/`iter_pregen_texts` + `scripts/generate_variants.py` 사양
   - 아니면 `message=`로 조립하거나 NONURGENT(LLM)에 등록
4. 분석기에 reset() 구현하고 `main.py` 세션 종료 리셋 목록에 추가
5. mock 검증: 스냅샷 조작 단위 테스트 + `--replay` 회귀
   (버스 쿨다운은 벽시계 기준 — 압축 재생 테스트에선 쿨다운 축소 필요)

## 테스트/운영 도구

- `tools/make_test_replay.py`  합성 리플레이 생성 (시나리오별)
- `tools/diagnose.py`          실차 원시 값 덤프 (필드 부실 진단)
- `tools/probe_rest.py`        REST 엔드포인트 수집 (스키마 확정용)
- `tools/pregen_audio.py`      멘트 풀 전체 오디오 사전 합성
- `scripts/generate_variants.py`  LLM으로 멘트 변형 재생성
- `data/speech_log.jsonl`      발화 로그 (반복감/타이밍/오탐 검토)

## 설계 원칙 요약

1. 침묵이 기본값 — 임계값/전이/쿨다운을 통과한 것만 말한다
2. 확인은 툴이 한다 — 드라이버에게 "체크해봐"라고 미루지 않는다
3. 실주행 증상 우선 — 필드가 없으면 증상(요레이트/조향/페이스)으로 잡는다
4. LMU 필드는 의심부터 — 실차 검증 전 필드는 방어적으로 (README 특성 목록)
5. 소스 분리 — 급한 건 메모리, LMU 고유 개념은 REST, 중복은 메모리 우선
6. 어떤 외부 실패(TTS/LLM/REST)에도 앱은 계속 돈다
