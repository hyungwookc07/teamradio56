"""
클래스 정규화 대조 — C# VehicleClass가 파이썬 vehicle_class와 같은 판단을 내는지.

실제 LMU 클래스 문자열 후보(대소문자·구분자·ELMS/Pro-Am 꼬리·별칭)와 헷갈리기 쉬운 문자열
("Hybrid"의 hy, "GT300", "LMP1", 전각·한글, 빈 문자열)을 입력 파일로 쓰고, 파이썬 기대 출력을
따로 쓴다. replay-check.sh가 러너 --class-check 출력과 바이트 단위로 diff.

입력 줄 (탭 구분):
  F <raw>                         → F <raw> <계열> <서열> <호칭> <다중 종합 이름>
  R <mine> <theirs> <내 페이스> <상대 페이스>  (0 = 페이스 없음)
                                  → R … <same_class 0/1> <class_relation>
  D <raw> <mine>                  → D … <진단 로그 줄 (traffic.class_diag_line)>
  M <cls>=<베스트 랩>;…            → M … <cls>=<중앙값 .6f>;… (클래스 문자열 정렬)

사용법: python tools/class_check.py --in classes_in.tsv --expect classes_py.tsv
"""

from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

RAWS = [
    # 하이퍼카 — 실제 문자열 후보와 별칭
    "Hypercar", "HYPERCAR", "hypercar", "Hyper", "HY", "hy", " Hy ", "Hyper Car",
    "Hypercar (WEC)", "LMH", "LMDh", "LMH/LMDh", "LMDH_2024",
    # LMP2 / LMP3 — 꼬리(ELMS, Pro-Am), 구분자, 서로 섞이지 않게
    "LMP2", "lmp2", "LMP2_ELMS", "LMP2 ELMS", "LMP2 Pro-Am", "LMP2-ProAm", "LM P2", "LMP2ELMS",
    "LMP3", "LMP3_ELMS", "LMP3 Pro-Am", "lmp3", "LM-P3",
    # GTE / GT3
    "GTE", "LMGTE", "LMGTE Pro", "LMGTE Am", "GTE-Am", "GTEPro", "LMGTEAM", "GTE2019",
    "LMGT3", "GT3", "GT3 Pro-Am", "LMGT3_ELMS", "GT3R", "LMGT3 #91",
    # 모르는 클래스 — 부분 문자열 함정, 다른 시리즈, 비ASCII, 빈 값
    "Hybrid", "Thy", "Vintage", "GT300", "GT4", "LMP1", "LMP23", "P2", "Mystery", "Mystery Class",
    "Formula", "Cup", "Ｈｙｐｅｒ", "하이퍼카", "", "   ", "-",
]

PAIR_RAWS = ["Hypercar", "Hyper", "HY", "LMP2", "LMP2_ELMS", "LMP3", "GTE", "LMGT3", "GT3",
             "Mystery", "GT4", ""]
PACES = [(0.0, 0.0), (100.0, 97.9), (100.0, 98.0), (100.0, 98.1), (97.9, 100.0),
         (98.0, 100.0), (100.0, 100.0), (0.0, 90.0), (90.0, 0.0)]

MEDIANS = [
    [("Hyper", 207.5), ("Hyper", 208.25), ("LMP2", 214.0), ("Mystery", 0.0)],
    [("A", 100.0), ("A", 101.0), ("A", 103.5), ("B", 99.999), ("B", 98.0)],
    [("", 120.1), ("GT4", 125.0), ("GT4", -1.0), ("GT4", 124.0), ("GT4", 126.0), ("GT4", 130.0)],
]


def lines_in() -> list[str]:
    out = [f"F\t{r}" for r in RAWS]
    for a in PAIR_RAWS:
        for b in PAIR_RAWS:
            for pa, pb in PACES:
                out.append(f"R\t{a}\t{b}\t{pa!r}\t{pb!r}")
    for r in RAWS:
        for mine in ("Hyper", "LMP3", "Mystery"):
            out.append(f"D\t{r}\t{mine}")
    for case in MEDIANS:
        out.append("M\t" + ";".join(f"{c}={b!r}" for c, b in case))
    return out


def evaluate(line: str) -> str:
    """입력 한 줄 → 출력 한 줄 (C# Program.ClassCheckLine 미러)."""
    from analyzers.traffic import class_diag_line
    from messages import multi_class_name, set_language
    import vehicle_class as vc
    set_language("en")
    f = line.split("\t")
    if f[0] == "F":
        raw = f[1]
        return "\t".join([line, vc.class_family(raw), str(vc.class_rank(raw)),
                          vc.class_display(raw), multi_class_name(raw)])
    if f[0] == "R":
        a, b, pa, pb = f[1], f[2], float(f[3]), float(f[4])
        pace = {}
        if pa > 0:
            pace[a] = pa
        if pb > 0 and b not in pace:
            pace[b] = pb
        return "\t".join([line, "1" if vc.same_class(a, b) else "0",
                          str(vc.class_relation(a, b, pace))])
    if f[0] == "D":
        return "\t".join([line, class_diag_line(f[1], f[2]), class_diag_line(f[1])])
    if f[0] == "M":
        cars = []
        for item in f[1].split(";"):
            c, b = item.rsplit("=", 1)
            cars.append({"cls": c, "best_lap": float(b)})
        med = vc.class_pace(cars)
        return "\t".join([line, ";".join(f"{c}={med[c]:.6f}" for c in sorted(med))])
    raise ValueError(line)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", required=True)
    ap.add_argument("--expect", required=True)
    args = ap.parse_args()
    lines = lines_in()
    with open(args.inp, "w", encoding="utf-8", newline="\n") as fi, \
            open(args.expect, "w", encoding="utf-8", newline="\n") as fe:
        for line in lines:
            fi.write(line + "\n")
            fe.write(evaluate(line) + "\n")
    print(f"클래스 정규화 대조 {len(lines)}줄")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
