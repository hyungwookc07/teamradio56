"""
차량 클래스 정규화 — 게임이 주는 클래스 문자열(mVehicleClass)을 계열(family)로 묶는
단일 출처. 트래픽(빠른 차/백마커 판정), 다중 차량 종합, 체커 뒤 쿨다운 콜, 음성 렌더가
모두 이 모듈만 본다. C# VehicleClass.cs와 1:1 (replay-check가 tools/class_check.py
덤프로 바이트 대조).

사용자 제보: "하이퍼카나 LMP를 타고 있어도 뒤에서 오는 차가 다 '패스터 카'라고 콜이 옴".
예전에는 서열표(부분 문자열 "hyper"…)와 호칭표("hypercar"…)가 따로 놀아서, 실제 LMU 문자열이
"Hyper"처럼 서열표에만 맞으면 하이퍼카 뒤 하이퍼카가 "faster car right behind"로 나갔고,
어느 표에도 없는 문자열(LMP3 등)은 서열 불명 → 무조건 '빠른 차' + "faster car"로 불렸다.

규칙:
  · 정규화 — ASCII 대문자만 소문자로, [a-z0-9] 밖의 문자는 구분자. 구분자로 자른 토큰을 앞에서부터,
    그다음 전부 이어 붙인 문자열을 본다 ("LM P2" → "lmp2"). 패턴은 토큰 전체와 맞아야 한다
    (부분 문자열 매칭 없음 — "hy"는 토큰 "hy"일 때만, "gte"가 다른 낱말 속에 있어도 안 맞는다)
  · 계열 — hypercar(hyper*, hy, lmh, lmdh) · lmp2(lmp2, lmp2elms, lmp2proam…) · lmp3 ·
    gte(gte, lmgte, gtepro, lmgteam…) · gt3(gt3, lmgt3, gt3proam…), 나머지는 unknown
  · 서열 — hypercar 5 > lmp2 4 > lmp3 3 > gte 2 > gt3 1, unknown 0
  · 호칭 — Hypercar / LMP2 / LMP3 / GTE / GT3. unknown은 ""(클래스 이름 없이 말한다 —
    예전처럼 "faster car"라고 부르지 않는다). 다중 차량 종합만 중립어 "car"(messages.multi_car)
  · 빠른 클래스 판정(class_relation) — 같은 클래스(문자열이 같거나 같은 계열)면 0, 둘 다 아는
    계열이면 서열 비교, 하나라도 모르면 관측 페이스(클래스 문자열별 베스트 랩 중앙값)가 2% 이상
    빠르거나 느릴 때만, 그 밖엔 0 (= 빠른 차도 백마커도 아니다)
"""

from __future__ import annotations

import re
from typing import Optional

UNKNOWN = "unknown"

# 계열 서열 — 숫자가 클수록 빠른 클래스.
# LMP3를 GTE/GT3 위에 두는 근거 (대표 랩타임, 대략): 르망 서킷에서 하이퍼카 3:23~3:27,
# LMP2 3:32~3:35, LMP3(Road to Le Mans) 3:41~3:46, LMGTE(2023) 3:50~3:54, LMGT3(2024)
# 3:54~3:58 — LMP3가 GTE보다 랩당 2~4%, GT3보다 3~5% 빠르다. ELMS 일반 서킷(바르셀로나,
# 포르티망)도 LMP3가 LMGTE/LMGT3보다 랩당 4~6초 빠르다. LMU의 ELMS 콘텐츠도 같은 서열이다.
FAMILY_RANK = {"hypercar": 5, "lmp2": 4, "lmp3": 3, "gte": 2, "gt3": 1}

FAMILY_DISPLAY = {
    "en": {"hypercar": "Hypercar", "lmp2": "LMP2", "lmp3": "LMP3", "gte": "GTE", "gt3": "GT3"},
    "ko": {"hypercar": "하이퍼카", "lmp2": "엘엠피 투", "lmp3": "엘엠피 쓰리",
           "gte": "GTE", "gt3": "GT3"},
}

# 토큰 전체와 맞아야 하는 패턴 (계열 순서 = 우선순위). C# VehicleClass.Patterns와 같은 문자열
FAMILY_PATTERNS = (
    ("hypercar", r"hyper[a-z0-9]*|hy|lmh[0-9]*|lmdh[0-9]*"),
    ("lmp2", r"lmp2(?:[a-z][a-z0-9]*)?"),
    ("lmp3", r"lmp3(?:[a-z][a-z0-9]*)?"),
    ("gte", r"(?:lm)?gte(?:pro|am)*[0-9]*"),
    ("gt3", r"(?:lm)?gt3(?:[a-z][a-z0-9]*)?"),
)
_COMPILED = tuple((fam, re.compile(pat)) for fam, pat in FAMILY_PATTERNS)

# 관측 페이스 비교 여유 — 클래스 문자열별 베스트 랩 중앙값이 이만큼 이상 차이 나야 다른 서열로
# 본다. 인접 클래스 간격은 랩당 3% 이상(하이퍼카↔LMP2 약 3~4%, LMP3↔GTE 2~4%,
# LMP2↔LMP3 약 4%)인데, 같은 클래스 안 차들의 베스트 랩 중앙값 차이(BoP, 드라이버·AI 실력
# 차)는 보통 1.5% 안쪽이다. 2%면 같은 페이스 무리를 '빠른 차'로 부르지 않으면서 클래스 한 칸
# 차이는 잡는다.
PACE_MARGIN = 0.02


def _tokens(raw: str) -> list[str]:
    """ASCII 대문자만 소문자로 바꾸고 [a-z0-9] 밖의 문자(비ASCII 포함)는 구분자로."""
    out, cur = [], []
    for ch in raw or "":
        if "A" <= ch <= "Z":
            ch = chr(ord(ch) + 32)
        if "a" <= ch <= "z" or "0" <= ch <= "9":
            cur.append(ch)
        elif cur:
            out.append("".join(cur))
            cur = []
    if cur:
        out.append("".join(cur))
    return out


def class_family(raw: str) -> str:
    """클래스 문자열 → 계열 (hypercar/lmp2/lmp3/gte/gt3/unknown)."""
    toks = _tokens(raw)
    if not toks:
        return UNKNOWN
    for cand in toks + ["".join(toks)]:
        for fam, pat in _COMPILED:
            if pat.fullmatch(cand):
                return fam
    return UNKNOWN


def class_rank(raw: str) -> int:
    """계열 서열 (0 = 모름)."""
    return FAMILY_RANK.get(class_family(raw), 0)


def class_display(raw: str, lang: str = "en") -> str:
    """무전 호칭 — 모르는 클래스는 "" (클래스 이름 없이 말한다)."""
    return FAMILY_DISPLAY["ko" if lang == "ko" else "en"].get(class_family(raw), "")


def same_class(a: str, b: str) -> bool:
    """같은 클래스 — 문자열이 같거나 같은 (아는) 계열 ("LMP2" ↔ "LMP2_ELMS")."""
    if (a or "") == (b or ""):
        return True
    fa = class_family(a)
    return fa != UNKNOWN and fa == class_family(b)


def class_pace(vehicles) -> dict:
    """클래스 문자열 → 베스트 랩 중앙값 (best_lap > 0인 차만, 짝수면 가운데 둘 평균)."""
    laps: dict = {}
    for v in vehicles:
        b = v["best_lap"]
        if b > 0:
            laps.setdefault(v["cls"] or "", []).append(b)
    out = {}
    for cls, xs in laps.items():
        xs.sort()
        n = len(xs)
        out[cls] = xs[n // 2] if n % 2 else (xs[n // 2 - 1] + xs[n // 2]) / 2
    return out


def class_relation(mine: str, theirs: str, pace: Optional[dict] = None) -> int:
    """
    상대 클래스가 내 클래스보다 빠른가: +1 빠른 클래스, -1 느린 클래스, 0 같은 클래스·모름.
    하나라도 계열을 모르면 관측 페이스(class_pace)로만 가르고, 페이스도 없으면 0.
    """
    if same_class(mine, theirs):
        return 0
    rm, rt = class_rank(mine), class_rank(theirs)
    if rm > 0 and rt > 0:
        return 1 if rt > rm else (-1 if rt < rm else 0)
    pm = (pace or {}).get(mine or "", 0.0)
    pt = (pace or {}).get(theirs or "", 0.0)
    if pm > 0 and pt > 0:
        if pt <= pm * (1.0 - PACE_MARGIN):
            return 1
        if pm <= pt * (1.0 - PACE_MARGIN):
            return -1
    return 0
