"""S1 (D066): compare two person detectors (the same interface as PersonDetector.cs) on a folder of frames.

    python -I tools/people-models/compare_detector.py <old.onnx> <new.onnx> <frames folder>

Each frame is stretched to 576 x 576 (bilinear), RGB / 255 with the ImageNet mean / deviation, as the application does; people
are class 1 through a sigmoid. Per frame the confident people (score >= 0.5, PersonTracker.HighScore) of both detectors are
matched by IoU; prints the counts, the matched share, the IoU of matched boxes and the frames where the counts differ.
"""
import pathlib
import sys

import numpy as np
import onnxruntime as ort
from PIL import Image

MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)
HIGH = 0.5


def people(session, tensor):
    dets, labels = session.run(['dets', 'labels'], {'input': tensor})
    scores = 1 / (1 + np.exp(-labels[0, :, 1]))
    keep = scores >= HIGH
    boxes = []
    for (cx, cy, w, h), s in zip(dets[0][keep], scores[keep]):
        boxes.append((max(0, cx - w / 2), max(0, cy - h / 2), min(1, cx + w / 2), min(1, cy + h / 2), float(s)))
    return nms(boxes)


def iou(a, b):
    x0, y0, x1, y1 = max(a[0], b[0]), max(a[1], b[1]), min(a[2], b[2]), min(a[3], b[3])
    inter = max(0, x1 - x0) * max(0, y1 - y0)
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0


def nms(boxes, threshold=0.7):
    kept = []
    for b in sorted(boxes, key=lambda b: -b[4]):
        if all(iou(b, k) < threshold for k in kept):
            kept.append(b)
    return kept


def main() -> None:
    old = ort.InferenceSession(sys.argv[1], providers=['CPUExecutionProvider'])
    new = ort.InferenceSession(sys.argv[2], providers=['CPUExecutionProvider'])
    frames = sorted(pathlib.Path(sys.argv[3]).glob('*.png'))
    total_old = total_new = matched = 0
    ious, differ = [], []
    for f in frames:
        image = Image.open(f).convert('RGB').resize((576, 576), Image.BILINEAR)
        x = ((np.asarray(image, dtype=np.float32) / 255 - MEAN) / STD).transpose(2, 0, 1)[None].astype(np.float32)
        a, b = people(old, x), people(new, x)
        total_old += len(a)
        total_new += len(b)
        used = set()
        for box in a:
            best = max(((iou(box, c), j) for j, c in enumerate(b) if j not in used), default=(0, -1))
            if best[0] >= 0.5:
                used.add(best[1])
                matched += 1
                ious.append(best[0])
        if len(a) != len(b):
            differ.append(f'{f.name}: old {len(a)}, new {len(b)}')
    print(f'frames {len(frames)}; confident people: old {total_old}, new {total_new}; matched (IoU >= 0.5) {matched}')
    if ious:
        print(f'IoU of matched boxes: mean {np.mean(ious):.3f}, min {np.min(ious):.3f}')
    print(f'frames with a different count: {len(differ)}')
    for line in differ[:20]:
        print('  ' + line)


if __name__ == '__main__':
    main()
