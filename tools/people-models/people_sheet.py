"""Contact sheets of the people analysis (D061 benchmark): frames of a video at given times with the detected people (boxes,
track ids, the torso centre), each person's mask in its own colour, the followed person (green) and the Follow window —
the part of the source the canvas shows (yellow).

  python -I people_sheet.py <video> <bench dir> <name> <out.jpg> <scale> <t1,t2,...> [canvas WxH]

Reads <name>.tracks.json, <name>.masks.json (optional) and <name>.follow.json (optional) written by people-bench.
"""
import base64
import json
import os
import subprocess
import sys
import zlib

import cv2
import numpy as np

PALETTE = [(82, 82, 255), (255, 196, 64), (64, 215, 255), (174, 240, 105), (251, 64, 224), (64, 171, 255), (89, 255, 178), (254, 109, 83)]


def frame_at(video, t, height=640):
    cmd = ["ffmpeg", "-v", "error", "-ss", f"{t:.3f}", "-i", video, "-frames:v", "1", "-vf", f"scale=-2:{height}", "-f", "image2pipe",
           "-vcodec", "png", "-"]
    data = subprocess.run(cmd, capture_output=True, check=True).stdout
    return cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)


def nearest(samples, t):
    return min(samples, key=lambda s: abs(s["t"] - t)) if samples else None


def main():
    video, bench, name, out, scale, times = sys.argv[1:7]
    cw, ch = (int(v) for v in (sys.argv[7] if len(sys.argv) > 7 else "1080x1920").split("x"))
    scale = float(scale)
    times = [float(t) for t in times.split(",")]
    tracks = json.load(open(os.path.join(bench, name + ".tracks.json")))
    masks_path = os.path.join(bench, name + ".masks.json")
    masks = json.load(open(masks_path)) if os.path.exists(masks_path) else None
    follow_path = os.path.join(bench, name + ".follow.json")
    follow = json.load(open(follow_path)) if os.path.exists(follow_path) else None
    chain = set((follow or {}).get("summary", {}).get("chain") or [])
    tiles = []
    for t in times:
        img = frame_at(video, t)
        h, w = img.shape[:2]
        overlay = img.copy()
        if masks:
            s = nearest(masks["samples"], t)
            if s and abs(s["t"] - t) <= 0.1:
                for p in s["people"]:
                    crop = np.frombuffer(zlib.decompress(base64.b64decode(p["data"]), -15), np.uint8).reshape(p["h"], p["w"])
                    grid = np.zeros((masks["height"], masks["width"]), np.uint8)
                    grid[p["Y"]:p["Y"] + p["h"], p["X"]:p["X"] + p["w"]] = crop
                    m = cv2.resize(grid, (w, h), interpolation=cv2.INTER_LINEAR) > 127
                    overlay[m] = (0.45 * np.array(PALETTE[p["id"] % len(PALETTE)]) + 0.55 * overlay[m]).astype(np.uint8)
        s = nearest(tracks["samples"], t)
        if s and abs(s["t"] - t) <= 0.1:
            for p in s["people"]:
                x0, y0, x1, y1 = (int(round(v * (w if i % 2 == 0 else h))) for i, v in enumerate(p["box"]))
                color = (0, 230, 0) if p["id"] in chain else (255, 255, 255) if p["id"] >= 0 else (128, 128, 128)
                cv2.rectangle(overlay, (x0, y0), (x1, y1), color, 2)
                cv2.putText(overlay, f"#{p['id']} {p['score']:.2f}", (x0 + 2, max(12, y0 + 12)), cv2.FONT_HERSHEY_SIMPLEX, 0.4, color, 1)
                if p.get("torso"):
                    cv2.circle(overlay, (int(p["torso"][0] * w), int(p["torso"][1] * h)), 4, (0, 0, 255), -1)
        if follow:
            r = nearest(follow["rows"], t)
            if r is not None:
                # The canvas in source coordinates: picture of size src*fit*scale centred at canvas/2 + (x, y).
                sw, sh = 2160, 3840
                fit = min(cw / sw, ch / sh)
                k = fit * scale
                cx, cy = cw / 2 + r["x"], ch / 2 + r["y"]
                left = (0 - (cx - sw * k / 2)) / k / sw
                top = (0 - (cy - sh * k / 2)) / k / sh
                ww, wh = cw / k / sw, ch / k / sh
                cv2.rectangle(overlay, (int(left * w), int(top * h)), (int((left + ww) * w), int((top + wh) * h)), (0, 255, 255), 2)
                if not r["seen"]:
                    cv2.putText(overlay, "LOST", (8, h - 12), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 0, 255), 2)
        cv2.putText(overlay, f"{t:.2f}s", (6, 18), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 255, 255), 2)
        tiles.append(overlay)
    th = max(t.shape[0] for t in tiles)
    tiles = [cv2.copyMakeBorder(t, 0, th - t.shape[0], 0, 4, cv2.BORDER_CONSTANT) for t in tiles]
    per_row = 6
    rows = [np.hstack(tiles[i:i + per_row] + [np.zeros_like(tiles[0])] * (per_row - len(tiles[i:i + per_row]))) for i in range(0, len(tiles), per_row)]
    cv2.imwrite(out, np.vstack(rows), [cv2.IMWRITE_JPEG_QUALITY, 85])
    print("wrote", out)


if __name__ == "__main__":
    main()
