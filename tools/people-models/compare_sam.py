"""Compares the C# video segmenter (people-bench samcheck) with the official PyTorch SAM 2.1 tiny video predictor on the same
segment and prompt (D061, risk T-8 end to end: the app's decoder, preprocessing, the ONNX graphs, the C# memory loop).

  python -I compare_sam.py <cs.bin> <sam2.1_hiera_tiny.pt> <video> <start s> <seconds> <rate Hz> <x0,y0,x1,y1> <out.json>

The reference gets frames decoded by ffmpeg at the same size (a different scaler than the app's decoder — a small part of
the difference); its low-resolution logits are upsampled to the C# grid (bilinear, align corners false) and thresholded at
0; the C# coverage at 128.
"""
import json
import os
import sys

import numpy as np
import torch
import torch.nn.functional as F

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from validate_sam2_video import read_frames, reference_masks  # noqa: E402


def main():
    cs_path, ckpt, video, start, seconds, rate, box, out = sys.argv[1:9]
    raw = open(cs_path, "rb").read()
    gw, gh = np.frombuffer(raw[:8], dtype="<i4")
    cs = np.frombuffer(raw[8:], dtype=np.uint8).reshape(-1, gh, gw)
    frames = read_frames(video, float(start), float(seconds), float(rate))
    n = min(len(frames), len(cs))
    b = [float(v) * 1024 for v in box.split(",")]
    ref, _ = reference_masks(ckpt, frames[:n], b, frames.shape[1:3])
    ious = []
    for f in range(n):
        logits = torch.from_numpy(ref[f].reshape(1, 1, 256, 256))
        up = F.interpolate(logits, size=(int(gh), int(gw)), mode="bilinear", align_corners=False)[0, 0].numpy() > 0
        mine = cs[f] >= 128
        u = np.logical_or(up, mine).sum()
        ious.append(1.0 if u == 0 else np.logical_and(up, mine).sum() / u)
    res = {"frames": n, "grid": [int(gw), int(gh)], "mean_iou": float(np.mean(ious)), "median_iou": float(np.median(ious)),
           "p05_iou": float(np.percentile(ious, 5)), "min_iou": float(np.min(ious))}
    print(json.dumps(res))
    json.dump({**res, "ious": [float(x) for x in ious]}, open(out, "w"), indent=1)


if __name__ == "__main__":
    main()
