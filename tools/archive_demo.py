# -*- coding: utf-8 -*-
"""archive_demo.py — 把「一键归档」的输入/输出拼成一张前后对比图，用于人眼验收。

用法:
    python tools/archive_demo.py <输入文件夹> <输出文件夹> <对比图输出路径> [单行高度]

为什么需要它：
    三道门禁守的是"数字对不对"（尺寸、色阶、笔画数），但不守"看起来对不对"。
    算法改动后用这张图扫一眼，比读任何日志都快。
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

FONT_CANDIDATES = [
    r"C:\Windows\Fonts\msyh.ttc",
    r"C:\Windows\Fonts\msyhbd.ttc",
    r"C:\Windows\Fonts\simhei.ttf",
    r"C:\Windows\Fonts\simsun.ttc",
]


def load_font(size):
    for p in FONT_CANDIDATES:
        if os.path.isfile(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                pass
    return ImageFont.load_default()


def fit(im, h):
    r = h / float(im.height)
    return im.resize((max(1, int(im.width * r)), h), Image.LANCZOS)


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    src_dir, dst_dir, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
    row_h = int(sys.argv[4]) if len(sys.argv) > 4 else 380

    exts = (".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff")
    names = [n for n in sorted(os.listdir(src_dir)) if n.lower().endswith(exts)]

    pad, gap, label_h = 16, 14, 30
    f_lab = load_font(20)
    f_small = load_font(16)

    rows = []
    for n in names:
        p_in = os.path.join(src_dir, n)
        p_out = os.path.join(dst_dir, n)
        if not os.path.isfile(p_out):
            continue
        a = fit(Image.open(p_in).convert("RGB"), row_h)
        b = fit(Image.open(p_out).convert("RGB"), row_h)
        rows.append((n, a, b))

    if not rows:
        print("没有可对比的成对文件")
        return 1

    cell_w = max(max(a.width, b.width) for _, a, b in rows)
    W = pad + cell_w + gap + cell_w + pad
    H = pad + sum(label_h + row_h + pad for _ in rows)
    canvas = Image.new("RGB", (W, H), (245, 245, 242))
    d = ImageDraw.Draw(canvas)

    y = pad
    for n, a, b in rows:
        d.text((pad, y + 4), "处理前  " + n, font=f_lab, fill=(40, 40, 40))
        d.text((pad + cell_w + gap, y + 4), "处理后（一键归档）", font=f_lab, fill=(15, 110, 86))
        y += label_h
        canvas.paste(a, (pad + (cell_w - a.width) // 2, y))
        canvas.paste(b, (pad + cell_w + gap + (cell_w - b.width) // 2, y))
        d.rectangle([pad - 1, y - 1, pad + cell_w, y + row_h],
                    outline=(200, 200, 196))
        d.rectangle([pad + cell_w + gap - 1, y - 1, pad + cell_w + gap + cell_w, y + row_h],
                    outline=(15, 110, 86))
        y += row_h + pad

    canvas.save(out_path)
    print("已生成: %s  (%dx%d, %d 组)" % (out_path, W, H, len(rows)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
