#!/usr/bin/env python3
"""
art/xrayproxy-logo.png (иконка Android-версии, §13.4 «иконка та же») → src/XGates.Win/Assets/app.ico.
Без сторонних библиотек: разбор PNG (RGBA 8 бит, без чересстрочности), уменьшение усреднением с учётом альфы,
ICO с PNG-вложениями 16/24/32/48/64/256 (так Windows берёт резкую иконку для трея, панели задач и проводника).
"""
import struct, sys, zlib


def read_png(path):
    data = open(path, "rb").read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "не PNG"
    pos, idat, w = 8, b"", None
    while pos < len(data):
        ln, typ = struct.unpack(">I4s", data[pos:pos + 8]); body = data[pos + 8:pos + 8 + ln]; pos += 12 + ln
        if typ == b"IHDR":
            w, h, depth, ctype, _, _, inter = struct.unpack(">IIBBBBB", body)
            assert depth == 8 and ctype == 6 and inter == 0, "нужен RGBA 8 бит без чересстрочности"
        elif typ == b"IDAT": idat += body
        elif typ == b"IEND": break
    raw = zlib.decompress(idat); bpp = 4; stride = w * bpp
    rows, prev, i = [], bytearray(stride), 0
    for _ in range(h):
        f = raw[i]; line = bytearray(raw[i + 1:i + 1 + stride]); i += 1 + stride
        for x in range(stride):
            a = line[x - bpp] if x >= bpp else 0; b = prev[x]; c = prev[x - bpp] if x >= bpp else 0
            if f == 1: line[x] = (line[x] + a) & 255
            elif f == 2: line[x] = (line[x] + b) & 255
            elif f == 3: line[x] = (line[x] + ((a + b) >> 1)) & 255
            elif f == 4:
                p = a + b - c; pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line); prev = line
    return w, h, rows


def resize(w, h, rows, n):
    out = []
    for oy in range(n):
        y0, y1 = oy * h // n, max(oy * h // n + 1, (oy + 1) * h // n)
        line = bytearray()
        for ox in range(n):
            x0, x1 = ox * w // n, max(ox * w // n + 1, (ox + 1) * w // n)
            r = g = b = a = 0; cnt = 0
            for y in range(y0, y1):
                row = rows[y]
                for x in range(x0, x1):
                    pr, pg, pb, pa = row[4 * x:4 * x + 4]
                    r += pr * pa; g += pg * pa; b += pb * pa; a += pa; cnt += 1
            line += bytes([r // a, g // a, b // a, a // cnt]) if a else b"\0\0\0\0"
        out.append(line)
    return out


def write_png(n, rows):
    raw = b"".join(b"\0" + bytes(r) for r in rows)
    def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", n, n, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main(src, dst):
    w, h, rows = read_png(src)
    sizes = [16, 24, 32, 48, 64, 256]
    pngs = [write_png(n, resize(w, h, rows, n)) for n in sizes]
    out = struct.pack("<HHH", 0, 1, len(sizes)); off = 6 + 16 * len(sizes)
    for n, p in zip(sizes, pngs):
        out += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(p), off); off += len(p)
    open(dst, "wb").write(out + b"".join(pngs))
    print(f"{dst}: {', '.join(map(str, sizes))} px")


if __name__ == "__main__":
    main(*(sys.argv[1:3] if len(sys.argv) >= 3 else ["art/xrayproxy-logo.png", "src/XGates.Win/Assets/app.ico"]))
