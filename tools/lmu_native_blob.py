"""
LMU 내장 공유 메모리(LMU_Data) 레이아웃 교차 검증 — C# 리더와 독립된 쪽.

docs/lmu_native_layout.json(기계가 읽는 레이아웃 명세)만 보고 동작한다. C# 코드는 읽지 않는다.

  verify-spec   명세 자체 검사: pack(4) 규칙으로 필드 오프셋·구조체 크기를 다시 계산해 적힌 값과
                대조하고, V01 레코드(ScoringInfo·VehicleScoring·VehicleTelemetry·Wheel)의 rF2 공통
                필드가 이 저장소의 rf2data.py(ctypes) 오프셋과 같은지 본다 (LMU 추가 필드는 rF2의
                mExpansion 자리를 정확히 채워야 한다).
  pack --dir D  같은 레이스 상태를 담은 합성 덤프를 만든다:
                  native.bin         LMU_Data 전체 (324820 B) — 매핑 안 된 바이트는 잡음
                  rf2_scoring.bin    $rFactor2SMMP_Scoring$   (같은 레코드 바이트)
                  rf2_telemetry.bin  $rFactor2SMMP_Telemetry$ (같은 레코드 바이트)
                  rf2_extended.bin   $rFactor2SMMP_Extended$  (보충 필드만)
                  expected.tsv       스냅샷 필드 기대값 (Extended 보충 포함)
                  expected_noext.tsv Extended가 없을 때 (피트 제한속도 0, 메시지 빈 문자열)
                기대값은 명세 오프셋으로 덤프를 다시 읽어 SnapshotBuilder와 같은 변환을 적용해
                만든다. replay-check.sh가 C# 러너(--lmu-native-dump / --rf2-dump) 출력과 대조한다.
  check --expect E --got G   두 덤프(키\\t값)를 키 단위로 대조.

사용법: python3 -I tools/lmu_native_blob.py verify-spec
        python3 -I tools/lmu_native_blob.py pack --dir out/
        python3 -I tools/lmu_native_blob.py check --expect out/expected.tsv --got cs.tsv
"""

from __future__ import annotations

import argparse
import json
import math
import os
import random
import re
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SPEC_PATH = os.path.join(ROOT, "docs", "lmu_native_layout.json")

PRIM = {
    "i8": (1, "b"), "u8": (1, "B"), "char": (1, "B"),
    "i16": (2, "h"), "u16": (2, "H"),
    "i32": (4, "i"), "u32": (4, "I"), "f32": (4, "f"),
    "i64": (8, "q"), "u64": (8, "Q"), "f64": (8, "d"),
}

RF2_SCORING_SIZE = 12 + 548 + 128 * 584
RF2_TELEMETRY_SIZE = 16 + 128 * 1888


def load_spec() -> dict:
    with open(SPEC_PATH, encoding="utf-8") as f:
        return json.load(f)


def parse_type(t: str) -> tuple[str, int, bool]:
    """'f64[3]' → ('f64', 3, True), 'Vec3' → ('Vec3', 1, False)."""
    m = re.fullmatch(r"(\w+)(?:\[(\d+)\])?", t)
    if not m:
        raise ValueError("타입 형식: " + t)
    return m.group(1), int(m.group(2) or 1), m.group(2) is not None


class Layout:
    def __init__(self, spec: dict):
        self.spec = spec
        self.pack = spec["pack"]
        self.structs = spec["structs"]
        self._cache: dict[str, tuple[int, int]] = {}

    def size_align(self, base: str) -> tuple[int, int]:
        if base in PRIM:
            size = PRIM[base][0]
            return size, min(size, self.pack)
        return self.struct_size_align(base)

    def struct_size_align(self, name: str) -> tuple[int, int]:
        if name in self._cache:
            return self._cache[name]
        off, max_align = 0, 1
        for entry in self.structs[name]:
            fname, ftype, declared = entry[0], entry[1], entry[2]
            base, count, _ = parse_type(ftype)
            size, align = self.size_align(base)
            off = (off + align - 1) // align * align
            if off != declared:
                raise AssertionError(f"{name}.{fname}: pack({self.pack}) 오프셋 {off} ≠ 명세 {declared}")
            off += size * count
            max_align = max(max_align, align)
        total = (off + max_align - 1) // max_align * max_align
        self._cache[name] = (total, max_align)
        return self._cache[name]

    def field(self, struct_name: str, path: str) -> tuple[int, str]:
        """'mWheels[2].mTemperature' → (구조체 안 오프셋, 타입)."""
        off = 0
        cur = struct_name
        parts = path.split(".")
        for i, part in enumerate(parts):
            m = re.fullmatch(r"(\w+)(?:\[(\d+)\])?", part)
            name, idx = m.group(1), m.group(2)
            for entry in self.structs[cur]:
                if entry[0] == name:
                    break
            else:
                raise KeyError(f"{cur}.{name} 없음")
            base, count, is_array = parse_type(entry[1])
            off += entry[2]
            if idx is not None:
                if not is_array or int(idx) >= count:
                    raise IndexError(path)
                off += int(idx) * self.size_align(base)[0]
                ftype = base
            else:
                ftype = entry[1]
            if i < len(parts) - 1:
                cur = ftype
        return off, ftype


# -- verify-spec ---------------------------------------------------------------

def verify_spec() -> list[str]:
    spec = load_spec()
    lay = Layout(spec)
    bad: list[str] = []
    for name, expected in spec["struct_sizes"].items():
        try:
            size, _ = lay.struct_size_align(name)
        except AssertionError as e:
            bad.append(str(e))
            continue
        if size != expected:
            bad.append(f"{name}: 계산 크기 {size} ≠ 명세 {expected}")
    off = 0
    for name, sname, declared in spec["top_level"]:
        size, align = lay.struct_size_align(sname)
        off = (off + align - 1) // align * align
        if off != declared:
            bad.append(f"최상위 {name}: 오프셋 {off} ≠ 명세 {declared}")
        off += size
    if off != spec["total_size"]:
        bad.append(f"전체 크기 {off} ≠ 명세 {spec['total_size']}")
    nveh = spec["max_vehicles"]
    if f"VehicleScoring[{nveh}]" not in [e[1] for e in spec["structs"]["ScoringBlock"]]:
        bad.append("ScoringBlock 차량 배열 길이가 max_vehicles와 다름")
    if f"VehicleTelemetry[{nveh}]" not in [e[1] for e in spec["structs"]["TelemetryBlock"]]:
        bad.append("TelemetryBlock 차량 배열 길이가 max_vehicles와 다름")

    # rF2 공통 접두부 — 이 저장소의 rf2data.py(ctypes, 실차로 검증된 rF2 경로)와 대조
    sys.path.insert(0, ROOT)
    import rf2data  # noqa: E402  (저장소 자체 모듈)

    matched = 0
    for lmu_name, rf2_name in spec["rf2_equivalents"].items():
        ct = getattr(rf2data, rf2_name)
        import ctypes
        if ctypes.sizeof(ct) != spec["struct_sizes"][lmu_name]:
            bad.append(f"{lmu_name}: rf2data {rf2_name} 크기 {ctypes.sizeof(ct)} ≠ {spec['struct_sizes'][lmu_name]}")
            continue
        fields = {e[0]: e for e in spec["structs"][lmu_name]}
        for fname, _ in ct._fields_:
            desc = getattr(ct, fname)
            if fname in fields:
                entry = fields[fname]
                base, count, _ = parse_type(entry[1])
                size = lay.size_align(base)[0] * count
                if entry[2] != desc.offset or size != desc.size:
                    bad.append(f"{lmu_name}.{fname}: 명세 +{entry[2]}({size}B) ≠ rf2data +{desc.offset}({desc.size}B)")
                elif len(entry) > 3 and entry[3] == "lmu":
                    bad.append(f"{lmu_name}.{fname}: rF2에 있는 필드에 lmu 표시")
                else:
                    matched += 1
            elif fname == "mExpansion":
                # LMU 추가 필드가 rF2 mExpansion 자리를 빈틈없이 정확히 채워야 한다
                lo, hi = desc.offset, desc.offset + desc.size
                cover = 0
                for e in spec["structs"][lmu_name]:
                    base, count, _ = parse_type(e[1])
                    size = lay.size_align(base)[0] * count
                    inside = lo <= e[2] < hi
                    if inside and not (len(e) > 3 and e[3].startswith("lmu")):
                        bad.append(f"{lmu_name}.{e[0]}: rF2 mExpansion 자리인데 lmu 표시 없음")
                    if inside:
                        if e[2] + size > hi:
                            bad.append(f"{lmu_name}.{e[0]}: rF2 mExpansion 범위를 넘음")
                        cover += size
                if cover != desc.size:
                    bad.append(f"{lmu_name}: LMU 추가 필드 {cover}B ≠ rF2 mExpansion {desc.size}B")
            else:
                bad.append(f"{lmu_name}: rf2data 필드 {fname}가 명세에 없음")
    if matched < 150:
        bad.append(f"rf2data와 맞춰 본 필드가 너무 적음 ({matched})")

    # 스냅샷 매핑이 가리키는 원천 필드가 실제로 있는지
    for group, entries in spec["snapshot"].items():
        if group.startswith("_"):
            continue
        for key, src, *_ in entries:
            sname, path = src.split(".", 1)
            if sname == "rF2Extended":
                if not hasattr(rf2data.rF2Extended, path):
                    bad.append(f"snapshot.{group}.{key}: rF2Extended.{path} 없음")
                continue
            try:
                lay.field(sname, path)
            except (KeyError, IndexError) as e:
                bad.append(f"snapshot.{group}.{key}: {e}")
    if not bad:
        print(f"✅ LMU 내장 공유 메모리 명세 — 구조체 {len(spec['struct_sizes'])}개 pack(4) 재계산 일치, "
              f"전체 {spec['total_size']} B, rF2 공통 필드 {matched}개가 rf2data.py와 일치")
    return bad


# -- pack ------------------------------------------------------------------------

# 시나리오: 스코어링 5대, 텔레메트리 활성 6대. 플레이어(ID 23)는 스코어링 2번, 텔레메트리 4번
# 슬롯 — 두 배열이 인덱스로 정렬돼 있지 않다는 점(실제 LMU)까지 재현한다.
SCORING_IDS = [11, 7, 23, 42, 5]
TELEMETRY_IDS = [42, 5, 11, 99, 23, 7]
PLAYER_SCORING_IDX = 2
PLAYER_SLOT = 4
CLASSES = ["Hyper", "LMP2_ELMS", "GT3", "LMP3", "GTE"]


class Packer:
    def __init__(self, lay: Layout, blob: bytearray):
        self.lay = lay
        self.blob = blob

    def put(self, sname: str, base: int, path: str, value) -> None:
        off, ftype = self.lay.field(sname, path)
        b, count, is_array = parse_type(ftype)
        pos = base + off
        if b == "char":
            raw = value.encode("latin-1") + b"\0"
            if len(raw) > count:
                raise ValueError(path + " 문자열이 김")
            self.blob[pos:pos + len(raw)] = raw     # NUL 뒤는 잡음 그대로 (종료 처리 검사)
            return
        if b == "Vec3":
            for axis, v in zip("xyz", value):
                self.put(sname, base, path + "." + axis, v)
            return
        size, fmt = PRIM[b]
        vals = value if is_array else [value]
        if len(vals) != count:
            raise ValueError(path)
        struct.pack_into("<" + fmt * count, self.blob, pos, *vals)

    def get(self, sname: str, base: int, path: str):
        off, ftype = self.lay.field(sname, path)
        b, count, is_array = parse_type(ftype)
        pos = base + off
        if b == "char":
            raw = bytes(self.blob[pos:pos + count])
            return raw.split(b"\0", 1)[0]
        if b == "Vec3":
            return [self.get(sname, base, path + "." + a) for a in "xyz"]
        size, fmt = PRIM[b]
        vals = list(struct.unpack_from("<" + fmt * count, self.blob, pos))
        return vals if is_array else vals[0]


def bases(spec: dict) -> dict:
    lay = Layout(spec)
    top = {name: off for name, _, off in spec["top_level"]}
    scoring = top["scoring"]
    telem = top["telemetry"]
    info_off = scoring + lay.field("ScoringBlock", "scoringInfo")[0]
    veh0 = scoring + lay.field("ScoringBlock", "vehScoringInfo")[0]
    slot0 = telem + lay.field("TelemetryBlock", "telemInfo")[0]
    return {
        "generic": top["generic"], "info": info_off, "veh0": veh0,
        "veh_stride": spec["struct_sizes"]["VehicleScoring"],
        "telem": telem, "slot0": slot0,
        "slot_stride": spec["struct_sizes"]["VehicleTelemetry"],
    }


def build_native(spec: dict) -> tuple[bytearray, dict]:
    lay = Layout(spec)
    rng = random.Random(56)
    blob = bytearray(rng.getrandbits(8) for _ in range(spec["total_size"]))
    p = Packer(lay, blob)
    b = bases(spec)

    # 헤더: 이벤트 카운터·게임 버전 (값 의미는 미확정 — 리더는 "0보다 큼"·"바뀜"만 본다)
    for i, (name, _, _) in enumerate(spec["structs"]["Generic"][:16]):
        p.put("Generic", b["generic"], name, 3 + i)
    p.put("Generic", b["generic"], "SME_UPDATE_SCORING", 1717)
    p.put("Generic", b["generic"], "SME_UPDATE_TELEMETRY", 25600)
    p.put("Generic", b["generic"], "gameVersion", 14000)

    info = b["info"]
    p.put("ScoringInfo", info, "mTrackName", "Circuit de la Sarthe")
    p.put("ScoringInfo", info, "mSession", 10)
    p.put("ScoringInfo", info, "mCurrentET", 1234.5)
    p.put("ScoringInfo", info, "mEndET", 7200.25)
    p.put("ScoringInfo", info, "mMaxLaps", 2147483647)      # 시간제 레이스의 i32 최댓값
    p.put("ScoringInfo", info, "mLapDist", 13626.0)
    p.put("ScoringInfo", info, "mNumVehicles", len(SCORING_IDS))
    p.put("ScoringInfo", info, "mGamePhase", 5)
    p.put("ScoringInfo", info, "mYellowFlagState", 3)
    p.put("ScoringInfo", info, "mSectorFlag", [11, 1, 11])
    p.put("ScoringInfo", info, "mInRealtime", 2)             # 0이 아니면 참
    p.put("ScoringInfo", info, "mDarkCloud", 0.375)
    p.put("ScoringInfo", info, "mRaining", 0.125)
    p.put("ScoringInfo", info, "mAmbientTemp", 21.5)
    p.put("ScoringInfo", info, "mTrackTemp", 33.75)
    p.put("ScoringInfo", info, "mAvgPathWetness", 0.0625)

    places = [3, 1, 2, 5, 4]
    sectors = [1, 2, 0, 1, 2]
    pit_states = [0, 0, 0, 3, 0]
    pitstops = [1, 0, 2, 1, 0]
    penalties = [0, 1, 0, 0, 2]
    finish = [0, 0, 0, 0, 1]
    flags = [0, 6, 0, 1, 0]
    for r, vid in enumerate(SCORING_IDS):
        base = b["veh0"] + r * b["veh_stride"]
        put = lambda path, v: p.put("VehicleScoring", base, path, v)  # noqa: E731
        put("mID", vid)
        put("mDriverName", f"Driver {vid}")
        put("mVehicleName", f"Car #{vid} Team")
        put("mVehicleClass", CLASSES[r])
        put("mIsPlayer", 1 if r == PLAYER_SCORING_IDX else 0)
        put("mPlace", places[r])
        put("mTotalLaps", 10 + r)
        put("mLapDist", 1000.25 + 500 * r)
        put("mPathLateral", -1.5 + 0.75 * r)
        put("mSector", sectors[r])
        put("mLastLapTime", 210.125 + r)
        put("mBestLapTime", 208.5 + r)
        put("mLastSector1", 70.25 + r)
        put("mLastSector2", 140.5 + r)
        put("mTimeBehindNext", 1.25 * r)
        put("mLapsBehindNext", r % 2)
        put("mTimeBehindLeader", 3.5 * r)
        put("mInPits", 4 if r == 3 else 0)
        put("mPitState", pit_states[r])
        put("mNumPitstops", pitstops[r])
        put("mNumPenalties", penalties[r])
        put("mFinishStatus", finish[r])
        put("mFlag", flags[r])
        put("mEstimatedLapTime", 209.75 + r)
        put("mTimeIntoLap", 30.5 + 10 * r)
        put("mLapStartET", -1.0 if r == 4 else 1200.25 + r)   # 첫 랩 전 -1 (rF2와 같음)
        put("mInGarageStall", 1 if r == 4 else 0)
        put("mPos", [100.5 + r, 2.25, -50.75 - r])

    p.put("TelemetryBlock", b["telem"], "activeVehicles", len(TELEMETRY_IDS))
    p.put("TelemetryBlock", b["telem"], "playerVehicleIdx", PLAYER_SLOT)
    p.put("TelemetryBlock", b["telem"], "playerHasVehicle", 1)
    for i, vid in enumerate(TELEMETRY_IDS):
        p.put("VehicleTelemetry", b["slot0"] + i * b["slot_stride"], "mID", vid)

    slot = b["slot0"] + PLAYER_SLOT * b["slot_stride"]
    put = lambda path, v: p.put("VehicleTelemetry", slot, path, v)  # noqa: E731
    put("mLapNumber", 12)
    put("mLocalVel", [-3.0, 4.0, 12.0])         # |v| = 13 m/s
    put("mEngineRPM", 7250.5)
    put("mEngineMaxRPM", 8500.0)
    put("mGear", 4)
    put("mFuel", 45.25)
    put("mFuelCapacity", 90.0)
    put("mEngineWaterTemp", 88.5)
    put("mEngineOilTemp", 101.25)
    put("mOverheating", 0)
    put("mDetached", 3)
    put("mFrontWingHeight", 0.046875)
    put("mDentSeverity", [0, 1, 2, 0, 1, 2, 0, 1])
    put("mLastImpactET", 1180.5)
    put("mLastImpactMagnitude", 2500.75)
    put("mCurrentSector", -2147483646)          # 피트 레인: 부호 비트 (rF2 관례)
    put("mSpeedLimiter", 1)
    put("mUnfilteredSteering", -0.25)
    put("mLocalRot.y", 0.375)
    for w in range(4):
        wp = f"mWheels[{w}]."
        put(wp + "mBrakeTemp", 400.5 + 10 * w)
        put(wp + "mPressure", 165.25 + w)
        put(wp + "mTemperature", [353.25 + w, 356.5 + w, 359.75 + w])
        put(wp + "mTireCarcassTemperature", 345.5 + w)
        put(wp + "mWear", 0.875 - 0.0625 * w)
        put(wp + "mFlat", 1 if w == 3 else 0)
        put(wp + "mDetached", 0)
    return blob, b


EXTENDED = {
    "mCurrentPitSpeedLimit": 22.5,              # m/s → 81 km/h
    "mStatusMessage": "Drive Through Penalty: Speeding In Pitlane",
    "mLastHistoryMessage": "Track limits warning",
}


def build_rf2(spec: dict, native: bytearray, b: dict) -> tuple[bytes, bytes, bytes]:
    """같은 상태를 rF2 플러그인 버퍼로 — V01 레코드는 바이트까지 같으니 그대로 옮긴다."""
    rng = random.Random(5656)
    n = spec["max_vehicles"]
    info_size = spec["struct_sizes"]["ScoringInfo"]
    vs, ts = b["veh_stride"], b["slot_stride"]

    sc = bytearray(rng.getrandbits(8) for _ in range(RF2_SCORING_SIZE))
    struct.pack_into("<iii", sc, 0, 101, 101, 0)
    sc[12:12 + info_size] = native[b["info"]:b["info"] + info_size]
    veh_base = 12 + info_size
    sc[veh_base:veh_base + n * vs] = native[b["veh0"]:b["veh0"] + n * vs]

    te = bytearray(rng.getrandbits(8) for _ in range(RF2_TELEMETRY_SIZE))
    active = native[b["telem"]]
    struct.pack_into("<iiii", te, 0, 55, 55, 0, active)
    te[16:16 + n * ts] = native[b["slot0"]:b["slot0"] + n * ts]

    sys.path.insert(0, ROOT)
    import rf2data  # noqa: E402
    ext = rf2data.rF2Extended()
    ext.mVersionUpdateBegin = ext.mVersionUpdateEnd = 9
    ext.mCurrentPitSpeedLimit = EXTENDED["mCurrentPitSpeedLimit"]
    for name in ("mStatusMessage", "mLastHistoryMessage"):
        raw = EXTENDED[name].encode("latin-1")
        getattr(ext, name)[:len(raw)] = raw
    return bytes(sc), bytes(te), bytes(ext)


def num(x: float) -> str:
    return f"{x:.6f}"


def convert(conv: str, v, max_vehicles: int) -> str:
    if conv == "str":
        return v.decode("latin-1").strip()
    if conv == "int":
        return str(int(v))
    if conv == "num":
        return num(v)
    if conv == "bool":
        return "1" if v != 0 else "0"
    if conv == "bytes":
        return ",".join(str(int(x)) for x in v)
    if conv == "flag_blue":
        return "1" if v == 6 else "0"
    if conv == "negative":
        return "1" if v < 0 else "0"
    if conv == "clamp_vehicles":
        return str(min(int(v), max_vehicles))
    if conv == "mps_to_kmh":
        return num(v * 3.6)
    if conv == "speed_kmh":
        x, y, z = v
        return num(math.sqrt(x * x + y * y + z * z) * 3.6)
    if conv == "vec3":
        return ",".join(num(c) for c in v)
    if conv == "k2c":
        return num(v - 273.15)
    if conv == "k2c_list":
        return ",".join(num(c - 273.15) for c in v)
    raise ValueError("변환: " + conv)


def expected(spec: dict, native: bytearray, b: dict, with_ext: bool) -> list[tuple[str, str]]:
    lay = Layout(spec)
    p = Packer(lay, native)
    maxv = spec["max_vehicles"]
    snap = spec["snapshot"]
    out: list[tuple[str, str]] = []
    nveh = 0
    for key, src, conv, *gap in snap["session"]:
        sname, path = src.split(".", 1)
        if sname == "rF2Extended":
            if with_ext:
                v = EXTENDED[path]
                v = v.encode("latin-1") if isinstance(v, str) else struct.unpack("<f", struct.pack("<f", v))[0]
                out.append(("session." + key, convert(conv, v, maxv)))
            else:
                out.append(("session." + key, num(0.0) if conv == "mps_to_kmh" else ""))
            continue
        val = convert(conv, p.get(sname, b["info"], path), maxv)
        if key == "num_vehicles":
            nveh = int(val)
        out.append(("session." + key, val))
    out.append(("in_session", "1" if nveh > 0 else "0"))
    out.append(("vehicles.count", str(nveh)))
    player_id = -1
    for r in range(nveh):
        base = b["veh0"] + r * b["veh_stride"]
        for key, src, conv, *_ in snap["vehicle"]:
            sname, path = src.split(".", 1)
            out.append((f"vehicle.{r}.{key}", convert(conv, p.get(sname, base, path), maxv)))
        if p.get("VehicleScoring", base, "mIsPlayer") != 0:
            player_id = p.get("VehicleScoring", base, "mID")
    active = min(native[b["telem"]], maxv)
    slot = None
    for i in range(active):
        base = b["slot0"] + i * b["slot_stride"]
        if player_id >= 0 and p.get("VehicleTelemetry", base, "mID") == player_id:
            slot = base
            break
    if slot is None:
        out.append(("player.present", "0"))
        return out
    out.append(("player.present", "1"))
    for key, src, conv, *_ in snap["player"]:
        sname, path = src.split(".", 1)
        out.append(("player." + key, convert(conv, p.get(sname, slot, path), maxv)))
    out.append(("player.has_steer_data", "1"))
    out.append(("player.wheels.count", "4"))
    for w in range(4):
        for key, src, conv, *_ in snap["wheel"]:
            _, path = src.split(".", 1)
            v = p.get("VehicleTelemetry", slot, f"mWheels[{w}].{path}")
            out.append((f"player.wheel.{w}.{key}", convert(conv, v, maxv)))
    return out


def write_tsv(path: str, rows: list[tuple[str, str]]) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        for k, v in rows:
            f.write(f"{k}\t{v}\n")


def pack(out_dir: str) -> int:
    spec = load_spec()
    os.makedirs(out_dir, exist_ok=True)
    native, b = build_native(spec)
    sc, te, ext = build_rf2(spec, native, b)
    files = {
        "native.bin": bytes(native), "rf2_scoring.bin": sc,
        "rf2_telemetry.bin": te, "rf2_extended.bin": ext,
    }
    for name, data in files.items():
        with open(os.path.join(out_dir, name), "wb") as f:
            f.write(data)
    full = expected(spec, native, b, True)
    write_tsv(os.path.join(out_dir, "expected.tsv"), full)
    write_tsv(os.path.join(out_dir, "expected_noext.tsv"), expected(spec, native, b, False))
    print(f"합성 덤프: native {len(native)} B, rF2 scoring {len(sc)} / telemetry {len(te)} / "
          f"extended {len(ext)} B, 기대 필드 {len(full)}개 → {out_dir}")
    return 0


def read_tsv(path: str) -> dict[str, str]:
    rows: dict[str, str] = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            k, _, v = line.partition("\t")
            rows[k] = v
    return rows


def check(expect_path: str, got_path: str, label: str) -> int:
    exp, got = read_tsv(expect_path), read_tsv(got_path)
    bad = []
    for k in sorted(set(exp) | set(got)):
        if exp.get(k) != got.get(k):
            bad.append(f"   {k}: 기대 {exp.get(k)!r} / 실제 {got.get(k)!r}")
    if not exp:
        bad.append("   기대값이 비어 있음")
    if bad:
        print(f"❌ {label}: {len(bad)}개 필드 불일치")
        print("\n".join(bad[:30]))
        return 1
    print(f"✅ {label}: 스냅샷 필드 {len(exp)}개 일치")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("verify-spec")
    pk = sub.add_parser("pack")
    pk.add_argument("--dir", required=True)
    ck = sub.add_parser("check")
    ck.add_argument("--expect", required=True)
    ck.add_argument("--got", required=True)
    ck.add_argument("--label", default="LMU 내장 공유 메모리")
    args = ap.parse_args()
    if args.cmd == "verify-spec":
        bad = verify_spec()
        if bad:
            print("❌ LMU 내장 공유 메모리 명세(docs/lmu_native_layout.json):")
            for line in bad:
                print("   " + line)
            return 1
        return 0
    if args.cmd == "pack":
        return pack(args.dir)
    return check(args.expect, args.got, args.label)


if __name__ == "__main__":
    sys.exit(main())
