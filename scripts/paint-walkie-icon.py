"""Usage: python3 scripts/paint-walkie-icon.py DarkwoodMP.Mod/Resources/walkie_talkie.png

Paint the walkie icon in Darkwood's inventory style: greyscale worn metal and rubber, lit from
the top left, slanted with the aerial to the top right, soft dark shadow. Painted at 1024 and
shrunk to 256."""
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageChops

S = 1024
rng = np.random.default_rng(7)
LIGHT = np.array([-0.6, -0.8])  # from the top left (x, y image space)


def mask_new():
    return Image.new("L", (S, S), 0)


def noise(scale, amp=1.0, seed=None):
    r = np.random.default_rng(seed) if seed is not None else rng
    small = r.random((max(2, S // scale), max(2, S // scale)))
    im = Image.fromarray((small * 255).astype(np.uint8)).resize((S, S), Image.BICUBIC)
    return (np.asarray(im, np.float32) / 255.0 - 0.5) * 2 * amp


def bevel(mask, radius, strength):
    """Light on the edges facing the light, dark on the others, from the mask's blurred slope."""
    m = np.asarray(mask.filter(ImageFilter.GaussianBlur(radius)), np.float32) / 255.0
    gy, gx = np.gradient(m)
    n = np.hypot(gx, gy) + 1e-6
    lit = -(gx * LIGHT[0] + gy * LIGHT[1]) / n * np.clip(n * radius * 2.0, 0, 1)
    return lit * strength


def part(mask, tone, bev=10, bev_k=0.35, grad=0.12, grain=0.05, grime=0.10, seed=0):
    """A painted surface: base tone, a top-left to bottom-right falloff, bevelled edges, grain."""
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32) / S
    v = np.full((S, S), tone, np.float32)
    v += (0.5 - (xx * 0.4 + yy * 0.6)) * grad * 2
    v += bevel(mask, bev, bev_k)
    v += noise(3, grain, seed + 1) + noise(40, grime, seed + 2)
    return np.clip(v, 0, 1)


layers = []  # (value array, mask image)


def add(mask, val):
    layers.append((val, mask))


def rrect(d, box, r, fill=255):
    d.rounded_rectangle(box, r, fill=fill)


cx = S // 2
# ---------------------------------------------------------------- body
body_box = (cx - 150, 470, cx + 150, 900)
m = mask_new(); d = ImageDraw.Draw(m); rrect(d, body_box, 38)
add(m, part(m, 0.25, bev=14, bev_k=0.85, grime=0.14, seed=10))

# side rail: a darker band down the left for depth
m2 = mask_new(); d = ImageDraw.Draw(m2); rrect(d, (body_box[0], body_box[1], body_box[0] + 34, body_box[3]), 30)
m2 = ImageChops.multiply(m2, m)
add(m2, part(m2, 0.15, bev=6, bev_k=0.3, seed=11))

# top cap (thicker block the knobs sit on)
m = mask_new(); d = ImageDraw.Draw(m); rrect(d, (cx - 120, 430, cx + 120, 500), 22)
add(m, part(m, 0.30, bev=8, bev_k=0.8, seed=12))

# knobs: volume (big, ridged) and channel (small)
for (kx, kr, ky) in ((cx - 62, 34, 400), (cx + 30, 22, 412)):
    m = mask_new(); d = ImageDraw.Draw(m); rrect(d, (kx - kr, ky - 6, kx + kr, 450), 8)
    v = part(m, 0.22, bev=6, bev_k=0.55, seed=13)
    # ridges
    xs = np.arange(S)
    ridge = (np.sin((xs - kx) / kr * np.pi * 5) * 0.06)[None, :]
    add(m, np.clip(v + ridge, 0, 1))
    m = mask_new(); d = ImageDraw.Draw(m); d.ellipse((kx - kr, ky - 14, kx + kr, ky + 12), fill=255)
    add(m, part(m, 0.48, bev=5, bev_k=0.5, seed=14))

# aerial base (metal collar)
m = mask_new(); d = ImageDraw.Draw(m); rrect(d, (cx + 62, 370, cx + 108, 450), 10)
add(m, part(m, 0.55, bev=6, bev_k=0.5, seed=15))

# flexible aerial: rubber, segmented, slightly bent
pts = [(cx + 85 + 6 * np.sin(t * 2.2) * (t), 380 - t * 250) for t in np.linspace(0, 1, 60)]
m = mask_new(); d = ImageDraw.Draw(m)
for i in range(len(pts) - 1):
    w = int(34 - 10 * i / len(pts))
    d.line([pts[i], pts[i + 1]], fill=255, width=w)
for p in pts[::1]:
    pass
av = part(m, 0.22, bev=7, bev_k=0.7, grime=0.05, seed=16)
yy = np.mgrid[0:S, 0:S][0].astype(np.float32)
av += (np.sin(yy / 9.0) > 0.55) * 0.08  # rings of the gooseneck
add(m, np.clip(av, 0, 1))
# aerial tip
tip = pts[-1]
m = mask_new(); d = ImageDraw.Draw(m); rrect(d, (tip[0] - 16, tip[1] - 30, tip[0] + 16, tip[1] + 8), 12)
add(m, part(m, 0.26, bev=5, bev_k=0.5, seed=17))

# speaker grille: recessed panel with slots
gb = (cx - 92, 520, cx + 100, 700)
m = mask_new(); d = ImageDraw.Draw(m); rrect(d, gb, 16)
add(m, part(m, 0.12, bev=8, bev_k=-0.6, seed=18))  # negative bevel: sunk in
for i in range(7):
    y = gb[1] + 22 + i * 22
    m = mask_new(); d = ImageDraw.Draw(m); rrect(d, (gb[0] + 18, y, gb[2] - 18, y + 9), 4)
    add(m, part(m, 0.07, bev=3, bev_k=-0.3, grime=0.02, seed=19 + i))

# electrical tape band, wrapped crooked round the middle (made at a bench from scrap)
m = mask_new(); d = ImageDraw.Draw(m)
d.polygon([(body_box[0] - 8, 742), (body_box[2] + 8, 724), (body_box[2] + 8, 786), (body_box[0] - 8, 806)], fill=255)
tv = part(m, 0.03, bev=6, bev_k=0.9, grime=0.04, seed=30)
tv += (np.sin((yy + np.mgrid[0:S, 0:S][1] * 0.07) / 3.0) * 0.015)  # tape sheen streaks
add(m, np.clip(tv, 0, 1))
# a loose tape end
m = mask_new(); d = ImageDraw.Draw(m)
d.polygon([(body_box[2] - 4, 728), (body_box[2] + 34, 744), (body_box[2] + 26, 790), (body_box[2] + 2, 784)], fill=255)
add(m, part(m, 0.16, bev=5, bev_k=0.35, seed=31))

# push-to-talk bar on the left side
m = mask_new(); d = ImageDraw.Draw(m); rrect(d, (body_box[0] - 26, 560, body_box[0] + 6, 690), 12)
add(m, part(m, 0.24, bev=6, bev_k=0.5, seed=32))

# label plate with screws, lower body
lb = (cx - 80, 822, cx + 80, 892)
m = mask_new(); d = ImageDraw.Draw(m); rrect(d, lb, 8)
add(m, part(m, 0.60, bev=5, bev_k=0.6, grime=0.18, seed=33))
for sx in (lb[0] + 14, lb[2] - 14):
    m = mask_new(); d = ImageDraw.Draw(m); d.ellipse((sx - 8, 849, sx + 8, 865), fill=255)
    add(m, part(m, 0.32, bev=3, bev_k=0.6, seed=34))
# scratched marks on the plate
m = mask_new(); d = ImageDraw.Draw(m)
for i in range(3):
    y = 840 + i * 14
    d.line([(lb[0] + 34, y), (lb[0] + 34 + 40 + 25 * (i % 2), y)], fill=255, width=4)
add(m, part(m, 0.25, bev=2, bev_k=0.2, seed=35))

# coiled cord hanging from the top to a small mic clipped on the side
coil = []
for t in np.linspace(0, 1, 600):
    bx = body_box[0] - 4 - 70 * np.sin(t * np.pi)
    by = 480 + t * 300
    a = t * np.pi * 2 * 15
    coil.append((bx + 15 * np.cos(a), by + 9 * np.sin(a)))
m = mask_new(); d = ImageDraw.Draw(m)
d.line(coil, fill=255, width=10, joint="curve")
add(m, part(m, 0.16, bev=3, bev_k=0.8, grime=0.03, seed=36))

# ---------------------------------------------------------------- compose (grey)
val = np.zeros((S, S), np.float32)
alpha = np.zeros((S, S), np.float32)
for v, mk in layers:
    a = np.asarray(mk, np.float32) / 255.0
    val = val * (1 - a) + v * a
    alpha = np.maximum(alpha, a)

# wear: scratches light up the metal, a few dark smudges
scr = mask_new(); d = ImageDraw.Draw(scr)
for _ in range(70):
    x = rng.integers(cx - 140, cx + 140); y = rng.integers(430, 930)
    ln = rng.integers(8, 40); ang = rng.uniform(-0.5, 0.5) + 0.6
    d.line([(x, y), (x + ln * np.cos(ang), y + ln * np.sin(ang))], fill=int(rng.integers(90, 200)), width=2)
s = np.asarray(scr.filter(ImageFilter.GaussianBlur(0.8)), np.float32) / 255.0
val = np.clip(val + s * 0.22, 0, 1)
smudge = np.clip(noise(12, 1.0, 99), 0, 1) ** 2
val = np.clip(val - smudge * 0.10, 0, 1)
# old dried blood, faint, near the bottom left like the game's radio (darker, warm)
blood = (np.clip(noise(48, 1.0, 5) + noise(6, 0.6, 6) - 0.55, 0, 1) * 2.2).clip(0, 1) * (np.mgrid[0:S, 0:S][0] > 700) * (np.mgrid[0:S, 0:S][1] < 560)

val = np.clip((val - 0.03) / 0.68, 0, 1) ** 0.85
grey = (val * 255)
rgb = np.stack([grey * 1.02, grey * 1.0, grey * 0.96], -1)  # a breath of warm, like the atlas
rgb *= 1 - blood[..., None] * np.array([0.45, 0.78, 0.76])
item = Image.fromarray(np.dstack([np.clip(rgb, 0, 255), alpha * 255]).astype(np.uint8), "RGBA")

# a slight lean, top to the right, as the game stands its boxy items (the 9V battery, the
# cassette radio); only long thin items lie on the diagonal
item = item.rotate(-12, resample=Image.BICUBIC, center=(cx, 650), expand=True)

# soft dark shadow halo, as on the atlas icons
a = item.split()[3]
sh = a.filter(ImageFilter.GaussianBlur(26)).point(lambda p: int(p * 0.75))
shadow = Image.new("RGBA", item.size, (0, 0, 0, 0)); shadow.putalpha(sh)
shadow = ImageChops.offset(shadow, 10, 14)
out = Image.alpha_composite(shadow, item)

bbox = out.split()[3].point(lambda p: 255 if p > 6 else 0).getbbox()
out = out.crop(bbox)
side = int(max(out.size) * 1.04)
sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
sq.alpha_composite(out, ((side - out.width) // 2, (side - out.height) // 2))
final = sq.resize((256, 256), Image.LANCZOS)
final.save(sys.argv[1] if len(sys.argv) > 1 else "walkie_new.png")
