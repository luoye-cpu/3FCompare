import struct, sys, zlib

def bmp_to_png(src, dst):
    with open(src, 'rb') as f:
        data = f.read()
    off = struct.unpack_from('<I', data, 10)[0]
    w = struct.unpack_from('<i', data, 18)[0]
    h = struct.unpack_from('<i', data, 22)[0]
    bpp = struct.unpack_from('<H', data, 28)[0]
    assert bpp == 32, bpp
    topdown = h < 0
    h = abs(h)
    px = data[off:]
    rows = []
    for y in range(h):
        src_y = y if topdown else (h - 1 - y)
        row = px[src_y * w * 4: src_y * w * 4 + w * 4]
        # BGRA -> RGB
        rows.append(b'\x00' + bytes(b for i in range(w) for b in (row[i*4+2], row[i*4+1], row[i*4+0])))
    raw = b''.join(rows)

    def chunk(tag, payload):
        return (struct.pack('>I', len(payload)) + tag + payload
                + struct.pack('>I', zlib.crc32(tag + payload) & 0xffffffff))

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(raw, 9))
    png += chunk(b'IEND', b'')
    with open(dst, 'wb') as f:
        f.write(png)
    print(f'{src} -> {dst} ({w}x{h})')

for name in sys.argv[1:]:
    bmp_to_png(name, name.rsplit('.', 1)[0] + '.png')
