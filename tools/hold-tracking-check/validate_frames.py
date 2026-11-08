"""Frame-exact independent check of the product's hold-tracking track (HoldTrackingCheck JSON) on real frames.

Every frame of the track's window (or every <stride>-th) is decoded in order at its own PTS (ffprobe gives the PTS; ffmpeg
decodes the window once, no frame dropped or duplicated), so the product's transform is evaluated at exactly the time of
the decoded frame — as the app does with the shown frame's own PTS.

Ground truth = SIFT correspondences of wall points between the reference frame and the frame (1920 px wide), filtered by
a loose RANSAC homography (8 px) only to drop gross mismatches. Each reference point is mapped by the product's track the
way the app does (CameraMotionTrack.At: a blend of two reliable samples <= 0.25 s apart, hidden otherwise) and compared
with where SIFT found it. Errors are in pixels of a 1920-wide frame (the contour's own displacement on a 1080p canvas; x2
on a 4K canvas, where the stroke is twice as wide too).

Usage: python validate_frames.py <track.json> <clip.mp4> <out.json> [stride]
Output: per frame — t, analysed (with the estimator's diagnostics), state (aligned / hidden / nodata), err_median,
err_p95, untracked_median, truth_points; and a summary.
"""
import json, subprocess, sys
import numpy as np, cv2

track_path, clip, out_path = sys.argv[1:4]
stride = int(sys.argv[4]) if len(sys.argv) > 4 else 1
track = json.load(open(track_path, encoding="utf-8"))
samples = track["track"]
ts = np.array([s["t"] for s in samples])
diag = {round(d["t"], 4): d for d in track.get("diagnostics", [])}
W = 1920


def at(t):
    """CameraMotionTrack.At with the defaults (gap 0.25 s, edge 0.1 s)."""
    if t < ts[0]:
        return None if t < ts[0] - 0.1 else (np.array(samples[0]["m"]) if samples[0]["reliable"] else "hidden")
    if t > ts[-1]:
        return None if t > ts[-1] + 0.1 else (np.array(samples[-1]["m"]) if samples[-1]["reliable"] else "hidden")
    i = int(np.searchsorted(ts, t, side="right")) - 1
    a = samples[i]
    if abs(a["t"] - t) < 1e-6 or i == len(samples) - 1:
        return np.array(a["m"]) if a["reliable"] else "hidden"
    b = samples[i + 1]
    if not (a["reliable"] and b["reliable"]) or b["t"] - a["t"] > 0.25 + 1e-9:
        return "hidden"
    f = (t - a["t"]) / (b["t"] - a["t"])
    return (1 - f) * np.array(a["m"]) + f * np.array(b["m"])


def pts_list(start, end):
    out = subprocess.check_output(["ffprobe", "-v", "error", "-select_streams", "v:0", "-read_intervals",
                                   f"{max(0, start - 2):.3f}%{end + 1:.3f}", "-show_entries", "packet=pts_time",
                                   "-of", "csv=p=0", clip]).decode()
    p = sorted(float(x.strip().strip(",")) for x in out.split() if x.strip().strip(",") not in ("", "N/A"))
    return [x for x in p if start - 1e-6 <= x <= end + 1e-6]


def frames(first, count, h):
    """count frames from the frame at PTS first, in decode order (= PTS order after ffmpeg's reordering)."""
    proc = subprocess.Popen(["ffmpeg", "-v", "error", "-ss", f"{first - 0.002:.6f}", "-i", clip, "-frames:v", str(count),
                             "-fps_mode", "passthrough", "-vf", f"scale={W}:-2:flags=area", "-f", "rawvideo",
                             "-pix_fmt", "gray", "pipe:1"], stdout=subprocess.PIPE)
    size = W * h
    for _ in range(count):
        raw = proc.stdout.read(size)
        if len(raw) < size:
            break
        yield np.frombuffer(raw, np.uint8).reshape(h, W)
    proc.stdout.close()
    proc.wait()


def single(t):
    raw = subprocess.check_output(["ffmpeg", "-v", "error", "-ss", f"{t - 0.002:.6f}", "-i", clip, "-frames:v", "1",
                                   "-vf", f"scale={W}:-2:flags=area", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"])
    return np.frombuffer(raw, np.uint8).reshape(len(raw) // W, W)


sift = cv2.SIFT_create(nfeatures=4000)
ref_t = track["reference_s"]
ref = single(ref_t)
H = ref.shape[0]
kr, dr = sift.detectAndCompute(ref, None)
matcher = cv2.BFMatcher(cv2.NORM_L2)

pts = pts_list(track["start_s"], track["end_s"])
rows = []
for k, (t, img) in enumerate(zip(pts, frames(pts[0], len(pts), H))):
    if k % stride != 0 or abs(t - ref_t) < 1e-4:
        continue
    row = {"t": round(t, 4)}
    d = diag.get(round(t, 4))
    if d is not None:
        row["analysed"] = d
    m = at(t)
    row["state"] = "hidden" if isinstance(m, str) else ("nodata" if m is None else "aligned")
    kf, df = sift.detectAndCompute(img, None)
    if df is not None and len(kf) >= 20:
        pairs = [p for p in matcher.knnMatch(dr, df, k=2) if len(p) == 2 and p[0].distance < 0.75 * p[1].distance]
        if len(pairs) >= 30:
            src = np.float32([kr[p[0].queryIdx].pt for p in pairs])
            dst = np.float32([kf[p[0].trainIdx].pt for p in pairs])
            _, mask = cv2.findHomography(src, dst, cv2.RANSAC, 8.0)
            keep = mask.ravel().astype(bool) if mask is not None else np.zeros(len(src), bool)
            src, dst = src[keep], dst[keep]
            row["truth_points"] = int(len(src))
            if len(src) >= 30:
                row["untracked_median"] = round(float(np.median(np.linalg.norm(src - dst, axis=1))), 2)
                if row["state"] == "aligned":
                    mm = (m / m[8]).reshape(3, 3)
                    n = np.c_[src[:, 0] / W, src[:, 1] / H, np.ones(len(src))] @ mm.T
                    p = np.c_[n[:, 0] / n[:, 2] * W, n[:, 1] / n[:, 2] * img.shape[0]]
                    e = np.linalg.norm(p - dst, axis=1)
                    row["err_median"] = round(float(np.median(e)), 2)
                    row["err_p95"] = round(float(np.percentile(e, 95)), 2)
    rows.append(row)

shown = [r for r in rows if "err_median" in r]
med = np.array([r["err_median"] for r in shown]) if shown else np.array([0.0])
summary = {
    "clip": track["clip"], "reference_s": ref_t, "window": [track["start_s"], track["end_s"]], "model": track["model"],
    "interval_s": track.get("interval_s"), "densify_px": track.get("densify_px"), "analysed": track.get("frames"),
    "densified": track.get("densified"), "frames": len(rows), "hidden": sum(r["state"] == "hidden" for r in rows),
    "nodata": sum(r["state"] == "nodata" for r in rows), "checked": len(shown),
    "p50": round(float(np.percentile(med, 50)), 2), "p95": round(float(np.percentile(med, 95)), 2),
    "p99": round(float(np.percentile(med, 99)), 2), "max": round(float(med.max()), 2),
    "over": {str(x): int((med > x).sum()) for x in (2, 3, 4, 6, 10)},
}
json.dump({"summary": summary, "rows": rows}, open(out_path, "w", encoding="utf-8"), indent=1)
print(json.dumps(summary, ensure_ascii=False))
