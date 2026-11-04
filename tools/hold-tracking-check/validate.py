"""Independent check of the product's hold-tracking track (HoldTrackingCheck JSON) on real frames.

Ground truth = SIFT correspondences of wall points between the reference frame and frame t (1920 px wide), filtered by a
loose RANSAC homography (8 px) only to drop gross mismatches. Each reference point is mapped by the product's track the
way the app does (CameraMotionTrack.At: linear blend of two reliable samples <= 0.25 s apart, hidden otherwise) and
compared with where SIFT found it. Reports, per time: hidden or the error (median / p95, px at 1920).
Times: the research grid (every 0.5 s) and mid-sample times (+0.05 s) to see the interpolation between samples.

Usage: python validate.py <track.json> <clip.mp4> <out.json> [step_s]
"""
import json, subprocess, sys
import numpy as np, cv2

track_path, clip, out_path = sys.argv[1:4]
step = float(sys.argv[4]) if len(sys.argv) > 4 else 0.5
track = json.load(open(track_path, encoding="utf-8"))
samples = track["track"]
ts = np.array([s["t"] for s in samples])
W = 1920


def frame(t):
    raw = subprocess.check_output(["ffmpeg", "-v", "error", "-ss", f"{t:.4f}", "-i", clip, "-frames:v", "1",
                                   "-vf", f"scale={W}:-2:flags=area", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"])
    h = len(raw) // W
    return np.frombuffer(raw, np.uint8).reshape(h, W)


def at(t):
    """CameraMotionTrack.At with the defaults (gap 0.25 s, edge 0.1 s)."""
    if t < ts[0]:
        return None if t < ts[0] - 0.1 else (np.array(samples[0]["m"]) if samples[0]["reliable"] else "hidden")
    if t > ts[-1]:
        return None if t > ts[-1] + 0.1 else (np.array(samples[-1]["m"]) if samples[-1]["reliable"] else "hidden")
    i = int(np.searchsorted(ts, t, side="right")) - 1
    a = samples[i]
    if abs(a["t"] - t) < 1e-9 or i == len(samples) - 1:
        return np.array(a["m"]) if a["reliable"] else "hidden"
    b = samples[i + 1]
    if not (a["reliable"] and b["reliable"]) or b["t"] - a["t"] > 0.25 + 1e-9:
        return "hidden"
    f = (t - a["t"]) / (b["t"] - a["t"])
    return (1 - f) * np.array(a["m"]) + f * np.array(b["m"])


sift = cv2.SIFT_create(nfeatures=4000)
ref_t = track["reference_s"]
ref = frame(ref_t)
H = ref.shape[0]
kr, dr = sift.detectAndCompute(ref, None)
matcher = cv2.BFMatcher(cv2.NORM_L2)

times = []
t = track["start_s"]
while t <= track["end_s"] + 1e-9:
    times.append(round(t, 3))
    if t + 0.05 <= track["end_s"]:
        times.append(round(t + 0.05, 3))
    t += step

rows = []
for t in times:
    if abs(t - ref_t) < 1e-6:
        continue
    m = at(t)
    img = frame(t)
    kf, df = sift.detectAndCompute(img, None)
    row = {"t": t}
    if df is None or len(kf) < 20:
        row["truth"] = "none"
    else:
        pairs = [p for p in matcher.knnMatch(dr, df, k=2) if len(p) == 2 and p[0].distance < 0.75 * p[1].distance]
        src = np.float32([kr[p[0].queryIdx].pt for p in pairs])
        dst = np.float32([kf[p[0].trainIdx].pt for p in pairs])
        if len(src) < 30:
            row["truth"] = "none"
        else:
            _, mask = cv2.findHomography(src, dst, cv2.RANSAC, 8.0)
            keep = mask.ravel().astype(bool) if mask is not None else np.zeros(len(src), bool)
            src, dst = src[keep], dst[keep]
            row["truth_points"] = int(len(src))
            if len(src) < 30:
                row["truth"] = "none"
    if isinstance(m, str):
        row["product"] = "hidden"
    elif m is None:
        row["product"] = "nodata"
    else:
        row["product"] = "aligned"
        if "truth" not in row:
            mm = (m / m[8]).reshape(3, 3)
            n = np.c_[src[:, 0] / W, src[:, 1] / H, np.ones(len(src))] @ mm.T
            p = np.c_[n[:, 0] / n[:, 2] * W, n[:, 1] / n[:, 2] * img.shape[0]]
            e = np.linalg.norm(p - dst, axis=1)
            row["err_median"] = round(float(np.median(e)), 2)
            row["err_p95"] = round(float(np.percentile(e, 95)), 2)
            # Without tracking: the contour stays where it was marked.
            e0 = np.linalg.norm(src - dst, axis=1)
            row["untracked_median"] = round(float(np.median(e0)), 1)
    rows.append(row)

shown = [r for r in rows if r["product"] == "aligned" and "err_median" in r]
summary = {
    "clip": track["clip"], "reference_s": ref_t, "window": [track["start_s"], track["end_s"]], "model": track["model"],
    "times": len(rows), "hidden": sum(r["product"] == "hidden" for r in rows),
    "no_truth": sum(r.get("truth") == "none" for r in rows),
    "shown_checked": len(shown),
    "median_of_medians": round(float(np.median([r["err_median"] for r in shown])), 2) if shown else None,
    "worst_median": max((r["err_median"] for r in shown), default=None),
    "worst_p95": max((r["err_p95"] for r in shown), default=None),
    "shown_over_3_6": sum(r["err_median"] > 3 or r["err_p95"] > 6 for r in shown),
    "untracked_worst_median": max((r["untracked_median"] for r in shown), default=None),
}
json.dump({"summary": summary, "rows": rows}, open(out_path, "w", encoding="utf-8"), indent=1)
print(json.dumps(summary, ensure_ascii=False))
