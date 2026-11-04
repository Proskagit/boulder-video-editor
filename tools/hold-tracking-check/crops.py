"""Zoomed visual check: for 6 wall points of the reference frame, a 240 x 240 crop (1920-px scale) around the point on the
reference frame (top) and around the product track's mapping of it on frame t (bottom), a green cross at the centre of
both. When the track is right, the same piece of wall sits under both crosses. Output: one PNG.

Usage: python crops.py <track.json> <clip.mp4> <t_s> <out.png>
"""
import json, subprocess, sys
import numpy as np, cv2

track_path, clip, t, out = sys.argv[1], sys.argv[2], float(sys.argv[3]), sys.argv[4]
track = json.load(open(track_path, encoding="utf-8"))
samples = track["track"]
ts = np.array([s["t"] for s in samples])
W, R = 1920, 120


def frame(t):
    raw = subprocess.check_output(["ffmpeg", "-v", "error", "-ss", f"{t:.4f}", "-i", clip, "-frames:v", "1",
                                   "-vf", f"scale={W}:-2:flags=area", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1"])
    h = len(raw) // (W * 3)
    return np.frombuffer(raw, np.uint8).reshape(h, W, 3).copy()


def at(t):
    i = max(0, min(len(ts) - 1, int(np.searchsorted(ts, t, side="right")) - 1))
    a = samples[i]
    if abs(a["t"] - t) < 1e-9 or i == len(samples) - 1:
        return np.array(a["m"]) if a["reliable"] else None
    b = samples[i + 1]
    if not (a["reliable"] and b["reliable"]) or b["t"] - a["t"] > 0.25 + 1e-9:
        return None
    f = (t - a["t"]) / (b["t"] - a["t"])
    return (1 - f) * np.array(a["m"]) + f * np.array(b["m"])


def crop(img, c):
    pad = cv2.copyMakeBorder(img, R, R, R, R, cv2.BORDER_CONSTANT, value=(40, 40, 40))
    x, y = int(round(c[0])) + R, int(round(c[1])) + R
    piece = pad[y - R:y + R, x - R:x + R].copy()
    cv2.drawMarker(piece, (R, R), (0, 255, 0), cv2.MARKER_CROSS, 30, 2)
    return piece


ref = frame(track["reference_s"])
img = frame(t)
H = ref.shape[0]
m = at(t)
if m is None:
    sys.exit("hidden at this time")
mm = (m / m[8]).reshape(3, 3)
gray = cv2.cvtColor(ref, cv2.COLOR_BGR2GRAY)
pts = cv2.goodFeaturesToTrack(gray, 6, 0.05, H // 4, mask=None).reshape(-1, 2)
n = np.c_[pts[:, 0] / W, pts[:, 1] / H, np.ones(len(pts))] @ mm.T
q = np.c_[n[:, 0] / n[:, 2] * W, n[:, 1] / n[:, 2] * img.shape[0]]
top = np.hstack([crop(ref, p) for p in pts])
bottom = np.hstack([crop(img, p) for p in q])
sheet = np.vstack([top, np.full((8, top.shape[1], 3), 255, np.uint8), bottom])
cv2.imwrite(out, sheet)
print(out)
