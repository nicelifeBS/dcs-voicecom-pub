"""Writes src/DcsVoiceCom.App/Assets/app.ico: an amber disc with a dark microphone, 16/32/48/64 px.

No dependencies: pixels are computed with 4x supersampling and stored as 32-bit BMPs inside the ICO.
"""
import math
import struct
from pathlib import Path

AMBER = (0xF2, 0xA9, 0x3B)
DARK = (0x10, 0x13, 0x16)


def coverage(x: float, y: float) -> tuple[float, float]:
    """(disc, glyph) coverage at a point in unit coordinates (0..1)."""
    cx = cy = 0.5
    disc = 1.0 if (x - cx) ** 2 + (y - cy) ** 2 <= 0.48 ** 2 else 0.0
    # microphone capsule: rounded rect
    glyph = 0.0
    if 0.40 <= x <= 0.60 and 0.20 <= y <= 0.56:
        dy = max(0.30 - y, y - 0.46, 0)
        if dy == 0 or (x - 0.5) ** 2 + dy ** 2 <= 0.10 ** 2:
            glyph = 1.0
    # holder arc
    r = math.hypot(x - 0.5, y - 0.46)
    if y >= 0.46 and 0.15 <= r <= 0.19:
        glyph = 1.0
    # stem and base
    if 0.475 <= x <= 0.525 and 0.64 <= y <= 0.76:
        glyph = 1.0
    if 0.38 <= x <= 0.62 and 0.74 <= y <= 0.79:
        glyph = 1.0
    return disc, glyph * disc


def image(size: int) -> bytes:
    rows = []
    n = 4
    for py in range(size - 1, -1, -1):  # BMP rows bottom-up
        row = bytearray()
        for px in range(size):
            d = g = 0.0
            for sy in range(n):
                for sx in range(n):
                    a, b = coverage((px + (sx + 0.5) / n) / size, (py + (sy + 0.5) / n) / size)
                    d += a
                    g += b
            d /= n * n
            g /= n * n
            color = [round(AMBER[i] * (d - g) / d + DARK[i] * g / d) if d else 0 for i in range(3)]
            row += bytes((color[2], color[1], color[0], round(255 * d)))
        rows.append(bytes(row))
    pixels = b"".join(rows)
    mask = b"\0" * (((size + 31) // 32) * 4 * size)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(pixels) + len(mask), 0, 0, 0, 0)
    return header + pixels + mask


def main() -> None:
    sizes = [16, 32, 48, 64]
    images = [image(s) for s in sizes]
    out = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    for size, data in zip(sizes, images):
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    out += b"".join(images)
    target = Path(__file__).resolve().parent.parent / "src" / "DcsVoiceCom.App" / "Assets" / "app.ico"
    target.write_bytes(out)
    print(f"wrote {target} ({len(out)} bytes)")


if __name__ == "__main__":
    main()
