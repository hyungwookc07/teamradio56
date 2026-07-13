"""
LMU 내장 REST API 프로브 — 게임 실행 중에 한 번 돌려서 실제 응답을 수집한다.

사용법 (게임이 세션에 들어가 있는 상태에서):
    pipenv run python tools/probe_rest.py

하는 일:
  - 후보 포트(6397, 5397)에서 후보 엔드포인트들을 전부 호출
  - 응답 요약을 콘솔에 출력 (상태/크기/앞부분)
  - 전체 응답을 data/rest_probe/ 에 JSON으로 저장
    → 이 폴더 내용(또는 콘솔 출력)을 공유하면 resttelemetry.py의
      TODO(가상 에너지/날씨 예보/피트 전략 파싱)를 확정할 수 있다.
"""

import io
import json
import os
import sys
import urllib.request

sys.stdout.reconfigure(encoding="utf-8", errors="replace") \
    if hasattr(sys.stdout, "reconfigure") else None

PORTS = (6397, 5397)
PATHS = [
    "/rest/sessions",
    "/rest/sessions/weather",
    "/rest/watch/standings",
    "/rest/watch/sessionInfo",
    "/rest/strategy/pitstop-estimate",
    "/rest/garage/getPlayerGarageData",
    "/rest/garage/UIScreen/RepairAndRefuel",
    "/rest/race/car",
    "/navigation/state",
]

OUT_DIR = os.path.join("data", "rest_probe")


def fetch(url: str):
    try:
        req = urllib.request.Request(url, headers={"Accept": "application/json"})
        with urllib.request.urlopen(req, timeout=3.0) as resp:
            return resp.status, resp.read()
    except Exception as e:
        return None, str(e).encode()


def main() -> None:
    os.makedirs(OUT_DIR, exist_ok=True)
    found_port = None
    for port in PORTS:
        base = f"http://localhost:{port}"
        status, _ = fetch(base + "/rest/sessions")
        if status is not None:
            found_port = port
            print(f"✅ 포트 {port} 응답 있음 — 이 포트로 전체 프로브")
            break
        print(f"   포트 {port}: 연결 안 됨")
    if found_port is None:
        print("❌ REST 서버를 찾지 못했습니다. 게임이 실행 중인지 확인하세요.")
        print("   (다른 포트를 쓰는 버전이면 PORTS에 추가해서 다시 실행)")
        return

    base = f"http://localhost:{found_port}"
    for path in PATHS:
        status, body = fetch(base + path)
        name = path.strip("/").replace("/", "_")
        if status == 200:
            try:
                parsed = json.loads(body.decode("utf-8", errors="replace"))
                pretty = json.dumps(parsed, ensure_ascii=False, indent=2)
            except ValueError:
                pretty = body.decode("utf-8", errors="replace")
            out = os.path.join(OUT_DIR, f"{name}.json")
            with io.open(out, "w", encoding="utf-8") as f:
                f.write(pretty)
            head = pretty.replace("\n", " ")[:160]
            print(f"✅ {path}  ({len(body)}B → {out})")
            print(f"     {head}")
        else:
            print(f"   {path}  응답 없음 ({body[:60].decode(errors='replace')})")

    print(f"\n저장 완료: {OUT_DIR}/ — 이 폴더의 파일들을 공유해 주세요.")


if __name__ == "__main__":
    main()
