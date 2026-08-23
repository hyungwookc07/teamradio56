"""
SimHub 좌측 메뉴 아이콘 PNG 생성 → base64 (C# 소스에 박아 넣는다).

리소스 파일(.resx)이나 SimHub의 ToIcon 확장에 의존하지 않기 위해,
아이콘을 base64 문자열로 코드에 직접 넣는다. 의존성이 없어야
빌드 실패 지점이 줄어든다.

사용법:
    python tools/gen_icon.py          # base64 출력 + C# 파일 갱신
"""

import base64
import io
import math
import os
import struct
import zlib

SIZE = 32
OUT_CS = os.path.join("simhub", "src", "TeamRadio56.SimHub", "PluginIcon.cs")


def png_chunk(tag: bytes, data: bytes) -> bytes:
    return (struct.pack(">I", len(data)) + tag + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))


def make_png(pixels, width, height) -> bytes:
    """pixels: [(r,g,b,a), ...] 행 우선."""
    raw = b""
    for y in range(height):
        raw += b"\x00"      # 필터 타입 0
        for x in range(width):
            raw += bytes(pixels[y * width + x])
    return (b"\x89PNG\r\n\x1a\n"
            + png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + png_chunk(b"IDAT", zlib.compress(raw, 9))
            + png_chunk(b"IEND", b""))


def coverage(px, py, cx, cy, radii, thickness, dot_r):
    """(px,py)가 송신 심볼(모서리 점 + 사분원 호)에 덮이는 정도 0..1."""
    dx, dy = px - cx, py - cy
    r = math.hypot(dx, dy)

    # 송신점 (왼쪽 아래 모서리)
    dot = _band(dot_r - r)
    if dot >= 1.0:
        return 1.0

    # 1사분면(오른쪽 위)에서만 호를 그린다
    if dx < 0 or dy > 0:
        return dot

    best = dot
    for arc_r in radii:
        best = max(best, _band(thickness - abs(r - arc_r)))
    return best


def _band(signed_dist, feather=1.0):
    """경계에서 feather 픽셀에 걸쳐 부드럽게 (안티에일리어싱)."""
    if signed_dist >= feather:
        return 1.0
    if signed_dist <= -feather:
        return 0.0
    return (signed_dist + feather) / (2 * feather)


def draw_icon():
    """무전 송신 심볼 — 왼쪽 아래 점에서 오른쪽 위로 퍼지는 사분원 호 3개."""
    cx, cy = 5.0, 27.0
    radii = (9.0, 15.0, 21.0)
    pixels = []
    for y in range(SIZE):
        for x in range(SIZE):
            a = coverage(x + 0.5, y + 0.5, cx, cy, radii,
                         thickness=1.4, dot_r=3.0)
            pixels.append((255, 255, 255, int(min(a, 1.0) * 255)))
    return pixels


def main():
    png = make_png(draw_icon(), SIZE, SIZE)
    b64 = base64.b64encode(png).decode("ascii")
    # C# 소스에 넣기 좋게 90자씩 자름
    lines = [b64[i:i + 90] for i in range(0, len(b64), 90)]
    joined = "\n".join('            "%s"%s' % (l, " +" if i < len(lines) - 1 else "")
                       for i, l in enumerate(lines))

    cs = '''using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// SimHub 좌측 메뉴 아이콘.
    ///
    /// .resx 리소스나 SimHub의 ToIcon 확장에 의존하지 않도록 PNG를 base64로
    /// 코드에 직접 넣는다 (빌드 실패 지점 최소화).
    /// tools/gen_icon.py 로 생성 — 직접 수정하지 말 것.
    /// </summary>
    public static class PluginIcon
    {
        private static ImageSource _cached;

        private const string Base64 =
%s;

        public static ImageSource Get()
        {
            if (_cached != null)
                return _cached;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(Convert.FromBase64String(Base64));
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                _cached = image;
            }
            catch (Exception)
            {
                _cached = null;   // 아이콘 때문에 플러그인이 죽지 않게
            }
            return _cached;
        }
    }
}
''' % joined

    os.makedirs(os.path.dirname(OUT_CS), exist_ok=True)
    with io.open(OUT_CS, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(cs)
    print("PNG %d bytes → base64 %d chars" % (len(png), len(b64)))
    print("생성: %s" % OUT_CS)


if __name__ == "__main__":
    main()
