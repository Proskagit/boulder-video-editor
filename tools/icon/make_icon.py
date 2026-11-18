"""D068 §2 (variant C, chosen by the owner): draws the application icon — a green pinch hold with a lit top face, a grip
pocket and a bolt hole, inside the white contour the editor draws around a marked hold, on the editor's dark rounded
square — and writes src/UI/Assets/app.ico (16 … 256 px) and src/UI/Assets/app-256.png.

    python -I tools/icon/make_icon.py <repository root>

Pillow only. The shapes are the cubic Bézier paths of the owner's chosen sketch (a 100 × 100 box), drawn at 1024 px and
downscaled per size (antialiased).
"""
import pathlib
import sys

from PIL import Image, ImageDraw

S = 1024
K = S / 100                            # the sketch's 100-unit box → pixels
BACKGROUND = (30, 36, 42, 255)         # the editor's dark grey-blue
HOLD = (67, 160, 71, 255)              # the route green
HOLD_LIGHT = (102, 187, 106, 255)
HOLD_SHADE = (27, 94, 32, 255)
POCKET = (15, 36, 22, 255)
CONTOUR = (255, 255, 255, 255)         # the outline of a marked hold

# The hold's outline: a start point and cubic segments (c1, c2, end).
HOLD_PATH = ((26, 40), [((30, 28), (46, 22), (60, 26)), ((74, 30), (80, 44), (74, 58)),
                        ((68, 72), (50, 78), (38, 72)), ((26, 66), (22, 52), (26, 40))])
LIGHT_PATH = ((30, 40), [((34, 31), (46, 27), (58, 30)), ((67, 32), (73, 38), (74, 45)),
                         ((66, 38), (56, 35), (46, 36)), ((40, 37), (34, 38), (30, 40))])
POCKET_PATH = ((34, 52), [((40, 46), (52, 44), (64, 48)), ((60, 52), (50, 54), (42, 55)),
                          ((38, 55), (35, 54), (34, 52))])


def points(path, scale=1.0, dx=0.0, dy=0.0, steps=48):
    """The path sampled into a polygon in pixels, scaled about the box's centre and shifted."""
    def px(p):
        return ((50 + (p[0] - 50) * scale + dx) * K, (50 + (p[1] - 50) * scale + dy) * K)

    start, segments = path
    pts = [px(start)]
    p0 = start
    for c1, c2, p3 in segments:
        for i in range(1, steps + 1):
            t = i / steps
            u = 1 - t
            x = u ** 3 * p0[0] + 3 * u * u * t * c1[0] + 3 * u * t * t * c2[0] + t ** 3 * p3[0]
            y = u ** 3 * p0[1] + 3 * u * u * t * c1[1] + 3 * u * t * t * c2[1] + t ** 3 * p3[1]
            pts.append(px((x, y)))
        p0 = p3
    return pts


def draw() -> Image.Image:
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((4 * K, 4 * K, 96 * K, 96 * K), radius=22 * K, fill=BACKGROUND)

    # The contour the editor draws around a marked hold, a little larger than the hold.
    # A ring (two filled outlines) rather than a wide line: no seams at the joints.
    d.polygon(points(HOLD_PATH, scale=1.235), fill=CONTOUR)
    d.polygon(points(HOLD_PATH, scale=1.165), fill=BACKGROUND)

    # The hold: its shadow (lower side), body, lit top face, the grip pocket and the bolt hole.
    d.polygon(points(HOLD_PATH, dy=4), fill=HOLD_SHADE)
    d.polygon(points(HOLD_PATH), fill=HOLD)
    d.polygon(points(LIGHT_PATH), fill=HOLD_LIGHT)
    d.polygon(points(POCKET_PATH), fill=POCKET)
    r = 3.5 * K
    d.ellipse((52 * K - r, 63 * K - r, 52 * K + r, 63 * K + r), fill=POCKET)
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
