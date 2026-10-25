"""生成插件图标：深蓝圆角底 + 白色盾牌 + 中间一个小手柄剪影（256x256 PNG）。"""
from PIL import Image, ImageDraw

S = 256
SS = 4  # 超采样
W = S * SS

img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# 圆角底：竖向渐变 #22345c -> #0d1526
top = (34, 52, 92)
bottom = (13, 21, 38)
grad = Image.new("RGBA", (W, W))
gd = ImageDraw.Draw(grad)
for y in range(W):
    t = y / (W - 1)
    gd.line(
        [(0, y), (W, y)],
        fill=(
            round(top[0] + (bottom[0] - top[0]) * t),
            round(top[1] + (bottom[1] - top[1]) * t),
            round(top[2] + (bottom[2] - top[2]) * t),
            255,
        ),
    )
mask = Image.new("L", (W, W), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, W - 1, W - 1], radius=int(W * 0.22), fill=255)
img.paste(grad, (0, 0), mask)

# 盾牌
cx, cy = W * 0.5, W * 0.5
sw, sh = W * 0.46, W * 0.54
shield = [
    (cx, cy - sh * 0.52),
    (cx + sw * 0.5, cy - sh * 0.30),
    (cx + sw * 0.5, cy + sh * 0.10),
    (cx, cy + sh * 0.52),
    (cx - sw * 0.5, cy + sh * 0.10),
    (cx - sw * 0.5, cy - sh * 0.30),
]
d.polygon(shield, fill=(255, 255, 255, 240))

# 盾牌内的手柄剪影（圆角矩形 + 左右十字）
pad_w, pad_h = sw * 0.62, sh * 0.32
pad = [cx - pad_w / 2, cy - pad_h / 2, cx + pad_w / 2, cy + pad_h / 2]
d.rounded_rectangle(pad, radius=pad_h * 0.42, fill=(34, 52, 92, 255))

bar = pad_h * 0.13
# 左侧十字
lx = cx - pad_w * 0.22
d.rounded_rectangle([lx - bar * 3.2, cy - bar, lx + bar * 3.2, cy + bar], radius=bar, fill=(255, 255, 255, 255))
d.rounded_rectangle([lx - bar, cy - bar * 3.2, lx + bar, cy + bar * 3.2], radius=bar, fill=(255, 255, 255, 255))
# 右侧两个按钮
rx = cx + pad_w * 0.22
r = bar * 1.9
d.ellipse([rx - r, cy - r * 2.2, rx + r, cy - r * 0.2], fill=(255, 255, 255, 255))
d.ellipse([rx - r, cy + r * 0.2, rx + r, cy + r * 2.2], fill=(255, 255, 255, 255))

img = img.resize((S, S), Image.LANCZOS)
img.save("icon.png")
print("icon.png written")
