"""D067 §7: draws the application icon — a climbing hold (an orange, rounded, slightly uneven blob with two bolt holes'
worth of texture removed for clarity) on a dark rounded square, a white Play triangle on the hold — and writes
src/UI/Assets/app.ico (16 … 256 px) and src/UI/Assets/app-256.png.

    python -I tools/icon/make_icon.py <repository root>

Pillow only; drawn at 1024 px and downscaled per size (antialiased).
"""
import math
import pathlib
import sys

from PIL import Image, ImageDraw, ImageFilter

S = 1024
BACKGROUND = (30, 36, 42, 255)        # the editor's dark grey-blue
HOLD = (245, 124, 0, 255)             # the route orange
HOLD_SHADE = (196, 88, 0, 255)
EDGE = (76, 175, 80, 255)             # the route green, as the outline of a marked hold
PLAY = (255, 255, 255, 255)


def blob(cx, cy, r, wobble, points=180, phase=0.0):
    pts = []
    for i in range(points):
        a = 2 * math.pi * i / points
        k = 1 + wobble * (0.55 * math.sin(3 * a + phase) + 0.30 * math.sin(5 * a + 1.3 + phase) + 0.15 * math.sin(2 * a + 0.4))
        pts.append((cx + r * k * math.cos(a), cy + r * 0.9 * k * math.sin(a)))
    return pts


def draw() -> Image.Image:
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((32, 32, S - 32, S - 32), radius=200, fill=BACKGROUND)

    cx, cy, r = S / 2, S / 2 + 20, 330
    # The green outline the editor draws around a marked hold.
    d.polygon(blob(cx, cy, r + 44, 0.07), fill=EDGE)
    d.polygon(blob(cx, cy, r + 14, 0.07), fill=BACKGROUND)
    # The hold, with a darker lower side for volume.
    d.polygon(blob(cx, cy, r, 0.07), fill=HOLD_SHADE)
    d.polygon(blob(cx - 10, cy - 26, r - 22, 0.07), fill=HOLD)

    # Play: a rounded triangle, optically centred.
    t = 230
    tri = [(cx - t * 0.42, cy - t * 0.62), (cx - t * 0.42, cy + t * 0.62), (cx + t * 0.66, cy)]
    play = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(play).polygon(tri, fill=PLAY)
    play = play.filter(ImageFilter.GaussianBlur(10))
    alpha = play.split()[3].point(lambda v: 255 if v > 110 else 0)
    play.putalpha(alpha)
    img.alpha_composite(play)
    return img


def main() -> None:
    root = pathlib.Path(sys.argv[1])
    out = root / 'src' / 'UI' / 'Assets'
    out.mkdir(parents=True, exist_ok=True)
    big = draw()
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [big.resize((n, n), Image.LANCZOS) for n in sizes]
    frames[-1].save(out / 'app-256.png')
    frames[-1].save(out / 'app.ico', format='ICO', sizes=[(n, n) for n in sizes], append_images=frames[:-1])
    print(out / 'app.ico')


if __name__ == '__main__':
    main()
