"""Hold tracking (R2 → E): a visual check of the product's track (HoldTrackingCheck output) on real frames. The contours
of a hold-candidate run (tools/hold-tracking-research/route_candidates.py, masks at 1920 x 1080 on the reference frame)
are drawn on later frames as marked (red) and moved by the C# track exactly like CompositionDrawPlan does (green):
normalized points through the sample's 3 x 3 matrix, blended linearly between samples. Images go to <out_dir>, outside
the repository.

Usage: python overlay_track.py <track.json> <candidates_run_dir> <labels.json> <clip.mp4> <out_dir> t1 [t2 ...]
"""
import json, os, subprocess, sys
import numpy as np, cv2

track_path, run, labels, clip, out = sys.argv[1:6]
times = [float(t) for t in sys.argv[6:]]
track = json.load(open(track_path))
samples = track["track"]
cand = json.load(open(os.path.join(run, "candidates.json")))
W, H = cand["size"]
masks = np.load(os.path.join(run, "masks.npz"))
route = json.load(open(labels))["route"]
os.makedirs(out, exist_ok=True)


def transform_at(t):
    ts = [s["t"] for s in samples]
    i = max(0, min(len(ts) - 2, int(np.searchsorted(ts, t, side="right")) - 1))
    a, b = samples[i], samples[i + 1]
    if not (a["reliable"] and b["reliable"]):
        return None
    f = 0.0 if b["t"] == a["t"] else min(1.0, max(0.0, (t - a["t"]) / (b["t"] - a["t"])))
    m = (1 - f) * np.array(a["m"]) + f * np.array(b["m"])
    return (m / m[8]).reshape(3, 3)


contours = []
for i in route:
    m = np.unpackbits(masks[f"arr_{i}"])[: W * H].reshape(H, W).astype(np.uint8)
    cs, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    contours += [c.reshape(-1, 2).astype(np.float64) for c in cs]

for t in times:
    raw = subprocess.check_output(["ffmpeg", "-v", "error", "-ss", str(t), "-i", clip, "-frames:v", "1", "-vf",
                                   f"scale={W}:{H}:flags=area", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1"])
    img = np.frombuffer(raw, np.uint8).reshape(H, W, 3).copy()
    m = transform_at(t)
    for c in contours:
        cv2.polylines(img, [c.astype(np.int32)], True, (0, 0, 255), 2)
        if m is None:
            continue
        n = np.c_[c[:, 0] / W, c[:, 1] / H, np.ones(len(c))] @ m.T
        p = np.c_[n[:, 0] / n[:, 2] * W, n[:, 1] / n[:, 2] * H]
        cv2.polylines(img, [p.astype(np.int32)], True, (0, 255, 0), 2)
    cv2.imwrite(os.path.join(out, f"{os.path.splitext(os.path.basename(clip))[0]}_{t:05.1f}.png"), img)
    print(t, "aligned" if m is not None else "hidden")
