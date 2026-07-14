"""
변형 멘트 풀 일괄 생성 — Anthropic API로 이벤트×톤별 한국어 변형 20~30개를
생성해 voice_lines/urgent_ko.yaml을 갱신한다.

흐름: 이 스크립트로 텍스트 생성 → tools/pregen_audio.py로 오디오 사전 캐시.

사용법:
    python scripts/generate_variants.py                  # 전체 풀 재생성
    python scripts/generate_variants.py --pools alongside pit_call
    python scripts/generate_variants.py --dry-run        # YAML 안 쓰고 출력만
    python scripts/generate_variants.py --count 25 --model claude-opus-4-8

주의:
  - 슬롯({cls}, {gap}, {fuel_laps})은 이산값만 허용된다. 생성 결과에서
    허용 외 플레이스홀더가 섞인 라인은 자동으로 걸러낸다.
  - 기존 YAML의 다른 풀은 건드리지 않는다 (지정 풀만 교체).
"""

from __future__ import annotations

# Windows 콘솔(cp949) 인코딩 가드
import sys
if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import yaml  # noqa: E402

from config import load_config  # noqa: E402
from voice import URGENT_LINES_FILE  # noqa: E402

# 풀 사양: (상황 설명, 톤별 지시, 허용 슬롯)
POOL_SPECS: dict[str, dict] = {
    "traffic_approach": {
        "situation": "멀티클래스 내구레이스. 뒤에서 상위 클래스 차량이 접근 중이고 {gap}초 뒤에 도달 예정. {cls}에 클래스 이름이 들어간다.",
        "slots": ["cls", "gap"],
        "required": ["cls", "gap"],
        "tones": {
            "casual": "평상시 톤. 정보 전달 + 짧은 조언.",
            "urgent": "배틀 중이거나 상황이 급함. 더 짧고 긴박하게.",
        },
    },
    "alongside": {
        "situation": "상대 차가 지금 옆에 나란히 있다. 좌우는 불명. 접촉 방지가 목적인 초긴급 콜.",
        "slots": [], "required": [],
        "tones": {"urgent": "극도로 짧게. 2~6단어. 스포터 콜처럼."},
    },
    "alongside_left": {
        "situation": "상대 차가 지금 왼쪽에 나란히 있다. 접촉 방지 초긴급 콜.",
        "slots": [], "required": [],
        "tones": {"urgent": "극도로 짧게. 2~6단어. '왼쪽'이 반드시 들어가야 함."},
    },
    "alongside_right": {
        "situation": "상대 차가 지금 오른쪽에 나란히 있다. 접촉 방지 초긴급 콜.",
        "slots": [], "required": [],
        "tones": {"urgent": "극도로 짧게. 2~6단어. '오른쪽'이 반드시 들어가야 함."},
    },
    "nearby_behind": {
        "situation": "상대 차가 뒤 50m 안에 붙었다. {cls}는 클래스 이름(생략 가능).",
        "slots": ["cls"], "required": [],
        "tones": {
            "casual": "차분하게 알리고 라인 유지 조언.",
            "urgent": "배틀 상황. 짧고 단호하게, 방어 조언.",
        },
    },
    "pass_complete": {
        "situation": "방금 상대 차가 추월을 완료해 지나갔다. 서사를 마무리하고 페이스 회복을 독려. {cls} 생략 가능.",
        "slots": ["cls"], "required": [],
        "tones": {"casual": "안도+독려 톤."},
    },
    "dropped": {
        "situation": "배틀하던 뒤차가 떨어져 나갔다. 칭찬하고 관리 모드 전환 조언. {cls} 생략 가능.",
        "slots": ["cls"], "required": [],
        "tones": {"casual": "칭찬+차분한 톤."},
    },
    "backmarker_ahead": {
        "situation": "전방에 백마커(랩 뒤진 차 또는 하위 클래스 트래픽)를 잡았다. 배틀이 아니므로 리듬을 유지하며 안전하게 추월하라는 조언. {cls} 생략 가능.",
        "slots": ["cls"], "required": [],
        "tones": {
            "casual": "차분한 안내 + 추월 조언.",
            "urgent": "배틀 중 백마커 처리. 짧고 집중된 주의.",
        },
    },
    "blue_flag": {
        "situation": "드라이버에게 블루 플래그가 게시됐다 (랩 앞선 차가 뒤에서 접근). 라인을 유지하며 손해 최소로 양보하라는 지시.",
        "slots": [], "required": [],
        "tones": {
            "casual": "차분한 안내. 코너 말고 직선에서 보내라는 조언.",
            "urgent": "즉시 양보 필요. 짧고 단호하게.",
        },
    },
    "fuel_warning": {
        "situation": "연료가 앞으로 {fuel_laps}랩 분량 남았다. 아직 여유는 있음.",
        "slots": ["fuel_laps"], "required": ["fuel_laps"],
        "tones": {
            "casual": "정보 전달 + 계획 언급.",
            "urgent": "배틀 중. 짧게, 세이브 우선순위 언급.",
        },
    },
    "fuel_critical": {
        "situation": "연료가 {fuel_laps}랩이면 바닥나는 긴급 상황.",
        "slots": ["fuel_laps"], "required": ["fuel_laps"],
        "tones": {"urgent": "긴급. 세이브/박스 지시 포함."},
    },
    "pit_call": {
        "situation": "이번 랩에 피트로 들어오라는 박스 콜. '박스'라는 단어가 대부분 들어감.",
        "slots": [], "required": [],
        "tones": {"casual": "표준 박스 콜.", "urgent": "윈도우 마감 직전. 짧고 단호."},
    },
    "damage": {
        "situation": "차량에 충격이 감지됐다. 몇 초 뒤 자동 점검 리포트가 따라오므로, "
                     "드라이버에게 상태 확인을 시키지 말고 '우리가 데이터를 보고 있고 "
                     "곧 결과를 불러준다'는 안심+예고. 대답을 요구하는 질문 금지 (단방향 무전).",
        "slots": [], "required": [],
        "tones": {"urgent": "걱정되지만 침착한 톤."},
    },
    "penalty": {
        "situation": "페널티가 부여됐다. 사실 전달 + 처리 계획 언급.",
        "slots": [], "required": [],
        "tones": {"casual": "드라이버가 흥분하지 않게 차분히."},
    },
    "race_start": {
        "situation": "레이스 스타트, 그린 플래그가 나왔다.",
        "slots": [], "required": [],
        "tones": {"urgent": "짧고 에너지 있게. 첫 랩 조심 당부 섞기."},
    },
    "fcy_start": {
        "situation": "풀코스옐로(세이프티카)가 발동됐다. 감속/추월금지 + 전략 기회라는 뉘앙스.",
        "slots": [], "required": [],
        "tones": {"urgent": "즉각적이지만 침착하게."},
    },
    "fcy_pit_open": {
        "situation": "FCY 중 피트가 열렸다. 지금 피트하면 시간 손실이 최소인 기회.",
        "slots": [], "required": [],
        "tones": {"urgent": "기회를 놓치지 않게 단호하게. '박스' 포함 권장."},
    },
    "green_flag": {
        "situation": "FCY가 끝나고 리스타트 그린이 나왔다.",
        "slots": [], "required": [],
        "tones": {"urgent": "짧게. 리스타트 집중 당부."},
    },
    "race_end": {
        "situation": "체커드 플래그. 클래스 {place}등으로 완주했다.",
        "slots": ["place"], "required": ["place"],
        "tones": {"casual": "수고 치하 + 결과 언급."},
    },
    "position_up": {
        "situation": "클래스 순위가 올라 {place}등이 됐다.",
        "slots": ["place"], "required": ["place"],
        "tones": {"casual": "짧은 칭찬 + 유지 독려."},
    },
    "position_down": {
        "situation": "클래스 순위가 내려가 {place}등이 됐다.",
        "slots": ["place"], "required": ["place"],
        "tones": {"casual": "탓하지 않고 침착하게 만회 독려."},
    },
    "pit_limiter": {
        "situation": "피트레인인데 스피드 리미터가 안 켜져 있다. 페널티 직전 초긴급 경고.",
        "slots": [], "required": [],
        "tones": {"urgent": "극도로 짧게, 2~5단어."},
    },
}

SYSTEM = """당신은 한국어 심레이싱 크루치프의 무전 대사 작가다.
규칙:
- 짧은 구어체 반말. 무전 특유의 간결한 호흡. 한 멘트 3~15단어.
- 서로 표현이 충분히 달라야 한다 (어순, 어휘, 뉘앙스 다양화).
- 플레이스홀더는 지정된 것만, 중괄호 그대로 사용: 예) {cls}, {gap}, {fuel_laps}
- 지정 외 플레이스홀더 금지. 숫자 직접 쓰기 금지(슬롯 사용).
- 출력은 JSON 문자열 배열 하나만. 설명/마크다운 금지."""


def generate_pool(client, model: str, pool: str, tone: str, spec: dict,
                  count: int) -> list[str]:
    tone_note = spec["tones"][tone]
    slots = ", ".join("{%s}" % s for s in spec["slots"]) or "없음"
    prompt = (
        f"상황: {spec['situation']}\n"
        f"톤: {tone} — {tone_note}\n"
        f"허용 슬롯: {slots}\n"
        f"이 상황의 크루치프 무전 멘트 변형을 {count}개 생성해라."
    )
    response = client.messages.create(
        model=model,
        max_tokens=2000,
        system=SYSTEM,
        messages=[{"role": "user", "content": prompt}],
    )
    text = "".join(b.text for b in response.content if b.type == "text").strip()
    if text.startswith("```"):
        text = text.strip("`").lstrip("json").strip()
    lines = json.loads(text)

    # 검증: 허용 슬롯 외 플레이스홀더 제거, 필수 슬롯 누락 제거
    ok = []
    allowed = set(spec["slots"])
    for line in lines:
        if not isinstance(line, str) or not line.strip():
            continue
        try:
            used = {name for _, name, _, _ in __import__("string").Formatter().parse(line)
                    if name}
        except ValueError:
            continue
        if not used.issubset(allowed):
            continue
        if any(("{%s}" % r) not in line for r in spec["required"]):
            continue
        ok.append(line.strip())
    return ok


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default="config.yaml")
    parser.add_argument("--pools", nargs="*", help="재생성할 풀 (기본: 전체)")
    parser.add_argument("--count", type=int, default=25, help="풀×톤당 변형 수 (20~30)")
    parser.add_argument("--model", default="claude-opus-4-8",
                        help="생성 모델 (일회성 작업이라 상위 모델 권장)")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    cfg = load_config(args.config)
    api_key = cfg.anthropic_api_key
    if not api_key:
        print("ANTHROPIC_API_KEY가 필요합니다 (환경변수 또는 config llm.api_key)")
        return 1
    import anthropic
    client = anthropic.Anthropic(api_key=api_key, timeout=60.0)

    with open(URGENT_LINES_FILE, "r", encoding="utf-8") as f:
        pools = yaml.safe_load(f) or {}

    targets = args.pools or list(POOL_SPECS)
    for pool in targets:
        spec = POOL_SPECS.get(pool)
        if spec is None:
            print(f"알 수 없는 풀: {pool}")
            continue
        for tone in spec["tones"]:
            print(f"생성 중: {pool} / {tone} ...")
            try:
                lines = generate_pool(client, args.model, pool, tone, spec, args.count)
            except Exception as e:
                print(f"  실패: {e}")
                continue
            print(f"  {len(lines)}개 통과")
            if args.dry_run:
                for line in lines[:5]:
                    print("   ", line)
                continue
            entry = pools.setdefault(pool, {})
            if isinstance(entry, list):          # 구형 포맷이면 톤 구조로 변환
                entry = {tone: entry}
                pools[pool] = entry
            entry[tone] = lines

    if not args.dry_run:
        with open(URGENT_LINES_FILE, "w", encoding="utf-8") as f:
            f.write("# scripts/generate_variants.py로 생성됨. 수동 편집 가능.\n")
            f.write("# 슬롯 규칙: {cls}/{gap}/{fuel_laps}만, 이산값 전제(오디오 사전 캐시).\n")
            yaml.safe_dump(pools, f, allow_unicode=True, sort_keys=False,
                           default_flow_style=False)
        print(f"저장: {URGENT_LINES_FILE}")
        print("다음 단계: python tools/pregen_audio.py  (오디오 사전 캐시 재빌드)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
