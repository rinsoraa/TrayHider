"""生成 TrayHider 应用图标（真正的多尺寸 .ico，手工组装 ICONDIR）。"""
import io
import os
import struct

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "TrayHider", "Assets", "app.ico")
PNG_OUT = os.path.join(HERE, "..", "src", "TrayHider", "Assets", "app.png")
os.makedirs(os.path.dirname(OUT), exist_ok=True)

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

ACCENT_TOP = (0x4C, 0xC2, 0xFF)
ACCENT_BOTTOM = (0x00, 0x78, 0xD4)
WHITE = (255, 255, 255, 255)


def rounded_mask(size, radius):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=radius, fill=255
    )
    return mask


def render(size):
    s = size * 8
    canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    grad = Image.new("RGBA", (1, s))
    for y in range(s):
        t = y / max(1, s - 1)
        grad.putpixel((0, y), (
            round(ACCENT_TOP[0] + (ACCENT_BOTTOM[0] - ACCENT_TOP[0]) * t),
            round(ACCENT_TOP[1] + (ACCENT_BOTTOM[1] - ACCENT_TOP[1]) * t),
            round(ACCENT_TOP[2] + (ACCENT_BOTTOM[2] - ACCENT_TOP[2]) * t),
            255,
        ))
    grad = grad.resize((s, s), Image.NEAREST)

    plate = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    plate.paste(grad, (0, 0), rounded_mask(s, round(s * 0.22)))
    canvas = Image.alpha_composite(canvas, plate)

    d = ImageDraw.Draw(canvas)
    pad = s * 0.23
    tray_top = s * 0.54
    tray_bottom = s * 0.79

    d.line(
        [(pad, tray_top), (pad, tray_bottom - s * 0.05),
         (s - pad, tray_bottom - s * 0.05), (s - pad, tray_top)],
        fill=WHITE,
        width=max(1, round(s * 0.058)),
        joint="curve",
    )

    box = s * 0.078
    gap = s * 0.058
    total = box * 3 + gap * 2
    start_x = (s - total) / 2
    box_y = s * 0.30

    for i in range(3):
        x = start_x + i * (box + gap)
        alpha = 105 if i == 1 else 255
        d.rounded_rectangle(
            [x, box_y, x + box, box_y + box],
            radius=box * 0.24,
            fill=(255, 255, 255, alpha),
        )

    cx = start_x + (box + gap)
    d.line(
        [(cx - box * 0.18, box_y + box * 0.88), (cx + box * 1.18, box_y - box * 0.18)],
        fill=WHITE,
        width=max(1, round(s * 0.048)),
    )

    return canvas.resize((size, size), Image.LANCZOS)


def png_bytes(img):
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(images, path):
    """手工组装 ICO：每个条目都存 PNG 数据（Vista+ 支持）。"""
    entries = []
    for img in images:
        w, h = img.size
        entries.append((
            0 if w >= 256 else w,
            0 if h >= 256 else h,
            png_bytes(img),
        ))

    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)

    dir_parts = []
    data_parts = []
    for w, h, data in entries:
        dir_parts.append(struct.pack(
            "<BBBBHHII", w, h, 0, 0, 1, 32, len(data), offset
        ))
        data_parts.append(data)
        offset += len(data)

    with open(path, "wb") as f:
        f.write(header)
        for p in dir_parts:
            f.write(p)
        for p in data_parts:
            f.write(p)


frames = [render(s) for s in SIZES]
write_ico(frames, OUT)
frames[-1].save(PNG_OUT, format="PNG")

print("ico:", OUT, os.path.getsize(OUT), "bytes")
print("png:", PNG_OUT, os.path.getsize(PNG_OUT), "bytes")
print("sizes:", SIZES)
