"""Checks the exported SAM 2.1 tiny video graphs against the official PyTorch video predictor (D061 spike, risk T-8).

The streaming loop below is the reference for the C# port (`src/Vision/People/Sam2VideoSegmenter.cs`): the same memory
selection (the prompted frame + the last 6 frames, temporal encodings), the same object pointers (up to 16, signed
temporal distance / 15) and the same prompt handling. Both sides get the same preprocessed frames, so the comparison
measures the graphs and the loop, not the resizing.

  python -I validate_sam2_video.py <onnx folder> <sam2.1_hiera_tiny.pt> <video> <start s> <seconds> <rate Hz> <out.json>
         [x0,y0,x1,y1 box in 0..1] [providers: dml|cpu]

Prints per-frame IoU of the masks (low resolution logits > 0) and the timings of each graph.
"""
import json
import os
import subprocess
import sys
import tempfile
import time

import numpy as np
import onnxruntime as ort
import torch
import torch.nn.functional as F

MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)
NUM_MASKMEM = 7
MAX_PTRS = 16


def read_frames(video, start, seconds, rate, long_side=1024):
    probe = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:stream_side_data=rotation",
                            "-of", "json", video], capture_output=True, text=True, check=True)
    s = json.loads(probe.stdout)["streams"][0]
    w, h = s["width"], s["height"]
    rot = 0
    for sd in s.get("side_data_list", []):
        rot = int(sd.get("rotation", 0))
    if abs(rot) % 180 == 90:
        w, h = h, w
    if w >= h:
        ow, oh = long_side, int(round(h * long_side / w / 2)) * 2
    else:
        oh, ow = long_side, int(round(w * long_side / h / 2)) * 2
    cmd = ["ffmpeg", "-v", "error", "-ss", str(start), "-t", str(seconds), "-i", video, "-vf", f"fps={rate},scale={ow}:{oh}",
           "-pix_fmt", "rgb24", "-f", "rawvideo", "-"]
    raw = subprocess.run(cmd, capture_output=True, check=True).stdout
    n = len(raw) // (ow * oh * 3)
    return np.frombuffer(raw, dtype=np.uint8)[: n * ow * oh * 3].reshape(n, oh, ow, 3)


def preprocess(frame):
    t = torch.from_numpy(frame).permute(2, 0, 1).float()[None] / 255.0
    t = F.interpolate(t, size=(1024, 1024), mode="bilinear", align_corners=False, antialias=True)
    t = (t - torch.from_numpy(MEAN)[None, :, None, None]) / torch.from_numpy(STD)[None, :, None, None]
    return t.numpy().astype(np.float32)


class OnnxVideoSegmenter:
    def __init__(self, folder, providers):
        opts = ort.SessionOptions()
        if providers[0] == "DmlExecutionProvider":
            opts.enable_mem_pattern = False
            opts.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        p = lambda n: os.path.join(folder, f"sam2.1_tiny_video.{n}.onnx")
        self.enc = ort.InferenceSession(p("encoder"), opts, providers=providers)
        self.att = ort.InferenceSession(p("memory_attention"), opts, providers=providers)
        self.dec_m = ort.InferenceSession(p("decoder_multi"), opts, providers=providers)
        self.dec_s = ort.InferenceSession(p("decoder_single"), opts, providers=providers)
        self.menc = ort.InferenceSession(p("memory_encoder"), opts, providers=providers)
        self.ppos = ort.InferenceSession(p("pointer_pos"), opts, providers=providers)
        raw = open(os.path.join(folder, "sam2.1_tiny_video.constants.bin"), "rb").read()
        assert raw[:8] == b"SAM2CST1"
        off = 8
        arrays = []
        while off < len(raw):
            n = int.from_bytes(raw[off:off + 4], "little"); off += 4
            arrays.append(np.frombuffer(raw[off:off + 4 * n], dtype="<f4").copy()); off += 4 * n
        self.no_mem = arrays[0].reshape(256)
        self.tpos = arrays[1].reshape(NUM_MASKMEM, 64)
        self.timing = {k: [] for k in ["enc", "att", "dec", "menc"]}

    def _t(self, key, fn):
        t0 = time.perf_counter()
        r = fn()
        self.timing[key].append((time.perf_counter() - t0) * 1000)
        return r

    def encode(self, image):
        vf, vp, h0, h1 = self._t("enc", lambda: self.enc.run(None, {"image": image}))
        return {"vf": vf, "vp": vp, "h0": h0, "h1": h1}

    def step(self, obj, f, feats, box=None):
        """One frame of one object. obj: {"cond": {f: out}, "prev": {f: out}}. box: (x0,y0,x1,y1) in 1024 space for a
        prompted frame."""
        if box is not None:
            pix = feats["vf"] + self.no_mem.reshape(1, 256, 1, 1)
            coords = np.array([[[box[0], box[1]], [box[2], box[3]]]], dtype=np.float32)
            labels = np.array([[2, 3]], dtype=np.int32)
            dec = self.dec_s
        else:
            mem, mem_pos, ptr, ptr_pos = self.memory(obj, f)
            curr = feats["vf"].reshape(256, 4096).T.reshape(4096, 1, 256)
            curr_pos = feats["vp"].reshape(256, 4096).T.reshape(4096, 1, 256)
            pix = self._t("att", lambda: self.att.run(None, {"curr": curr, "curr_pos": curr_pos, "memory_spatial": mem,
                                                              "memory_spatial_pos": mem_pos, "memory_ptr": ptr, "memory_ptr_pos": ptr_pos}))[0]
            coords = np.zeros((1, 1, 2), dtype=np.float32)
            labels = -np.ones((1, 1), dtype=np.int32)
            dec = self.dec_m
        mask, iou, ptr_out, score = self._t("dec", lambda: dec.run(None, {"pix_feat": pix, "high_res_0": feats["h0"], "high_res_1": feats["h1"],
                                                                         "point_coords": coords, "point_labels": labels}))
        mf, mp = self._t("menc", lambda: self.menc.run(None, {"vision_feat": feats["vf"], "low_res_mask": mask, "object_score_logits": score,
                                                              "binarize": np.full((1, 1, 1, 1), 1.0 if box is not None else 0.0, np.float32)}))
        out = {"mf": mf, "mp": mp, "ptr": ptr_out, "mask": mask, "score": float(score.reshape(-1)[0])}
        (obj["cond"] if box is not None else obj["prev"])[f] = out
        for k in [k for k in obj["prev"] if k < f - MAX_PTRS]:
            del obj["prev"][k]
        return out

    def memory(self, obj, f):
        cond = obj["cond"]
        spatial, spatial_pos = [], []
        entries = [(0, out) for out in cond.values()]
        for t_pos in range(1, NUM_MASKMEM):
            prev = f - (NUM_MASKMEM - t_pos)
            out = obj["prev"].get(prev)
            entries.append((t_pos, out))
        for t_pos, out in entries:
            if out is None:
                continue
            spatial.append(out["mf"].reshape(64, 4096).T)
            spatial_pos.append(out["mp"].reshape(64, 4096).T + self.tpos[NUM_MASKMEM - t_pos - 1])
        pos_and_ptrs = [(f - t, out["ptr"]) for t, out in cond.items() if t <= f]
        for t_diff in range(1, MAX_PTRS):
            t = f - t_diff
            if t < 0:
                break
            out = obj["prev"].get(t)
            if out is not None:
                pos_and_ptrs.append((t_diff, out["ptr"]))
        t_norm = np.array([p for p, _ in pos_and_ptrs], dtype=np.float32) / (MAX_PTRS - 1)
        obj_pos = self.ppos.run(None, {"t_norm": t_norm})[0]                      # [P, 64]
        ptrs = np.stack([p.reshape(256) for _, p in pos_and_ptrs])                # [P, 256]
        ptr_tokens = ptrs.reshape(-1, 4, 64).reshape(-1, 1, 64)                    # [P*4, 1, 64]
        ptr_pos = np.repeat(obj_pos, 4, axis=0).reshape(-1, 1, 64)
        mem = np.concatenate(spatial).reshape(-1, 1, 64).astype(np.float32)
        mem_pos = np.concatenate(spatial_pos).reshape(-1, 1, 64).astype(np.float32)
        return mem, mem_pos, ptr_tokens.astype(np.float32), ptr_pos.astype(np.float32)


def reference_masks(ckpt, frames_np, box1024, size_hw):
    from sam2.build_sam import build_sam2_video_predictor
    from PIL import Image
    predictor = build_sam2_video_predictor("configs/sam2.1/sam2.1_hiera_t.yaml", ckpt, device="cuda")
    with tempfile.TemporaryDirectory() as d:
        for i, fr in enumerate(frames_np):
            Image.fromarray(fr).save(os.path.join(d, f"{i:05d}.jpg"), quality=95)
        with torch.inference_mode():
            state = predictor.init_state(d)
            state["images"] = torch.from_numpy(np.concatenate([preprocess(fr) for fr in frames_np])).to("cuda")
            h, w = size_hw
            box = np.array([box1024[0] * w / 1024, box1024[1] * h / 1024, box1024[2] * w / 1024, box1024[3] * h / 1024], dtype=np.float32)
            predictor.add_new_points_or_box(state, frame_idx=0, obj_id=1, box=box)
            masks = {}
            t0 = time.perf_counter()
            for f, _, _ in predictor.propagate_in_video(state):
                masks[f] = state["output_dict_per_obj"][0]["cond_frame_outputs" if f == 0 else "non_cond_frame_outputs"][f]["pred_masks"].float().cpu().numpy()
            elapsed = time.perf_counter() - t0
    return masks, elapsed


def main():
    folder, ckpt, video, start, seconds, rate, out_path = sys.argv[1:8]
    box_n = [float(v) for v in sys.argv[8].split(",")] if len(sys.argv) > 8 else None
    prov = sys.argv[9] if len(sys.argv) > 9 else "dml"
    providers = ["DmlExecutionProvider"] if prov == "dml" else ["CPUExecutionProvider"]
    frames = read_frames(video, float(start), float(seconds), float(rate))
    n, h, w, _ = frames.shape
    box1024 = [box_n[0] * 1024, box_n[1] * 1024, box_n[2] * 1024, box_n[3] * 1024]
    seg = OnnxVideoSegmenter(folder, providers)
    obj = {"cond": {}, "prev": {}}
    onnx_masks = {}
    t0 = time.perf_counter()
    for f in range(n):
        feats = seg.encode(preprocess(frames[f]))
        out = seg.step(obj, f, feats, box1024 if f == 0 else None)
        onnx_masks[f] = out["mask"]
    onnx_elapsed = time.perf_counter() - t0
    ref, ref_elapsed = reference_masks(ckpt, frames, box1024, (h, w))
    ious, agree = [], []
    for f in range(n):
        a = onnx_masks[f].reshape(256, 256) > 0
        b = ref[f].reshape(256, 256) > 0
        u = np.logical_or(a, b).sum()
        ious.append(1.0 if u == 0 else np.logical_and(a, b).sum() / u)
        agree.append((a == b).mean())
    res = {"frames": n, "size": [w, h], "mean_iou": float(np.mean(ious)), "min_iou": float(np.min(ious)),
           "p05_iou": float(np.percentile(ious, 5)), "pixel_agreement": float(np.mean(agree)),
           "onnx_fps": n / onnx_elapsed, "torch_cuda_fps": n / ref_elapsed,
           "ms": {k: float(np.median(v)) if v else None for k, v in seg.timing.items()},
           "empty_onnx": int(sum(1 for f in range(n) if not (onnx_masks[f] > 0).any())),
           "empty_ref": int(sum(1 for f in range(n) if not (ref[f] > 0).any()))}
    print(json.dumps(res, indent=1))
    json.dump({**res, "ious": [float(x) for x in ious]}, open(out_path, "w"), indent=1)


if __name__ == "__main__":
    main()
