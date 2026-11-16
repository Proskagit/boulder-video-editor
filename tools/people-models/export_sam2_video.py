"""Exports SAM 2.1 Hiera tiny in its *video* mode to five ONNX graphs for the Occlusion analysis (D061).

The official package runs the video mode as one Python loop around four networks; ONNX Runtime from C# needs each
network as its own graph, and the loop (the memory bank, the object pointers, the temporal encodings) is rewritten in C#
(`src/Vision/People/Sam2VideoSegmenter.cs`). Graphs:

  sam2.1_tiny_video.encoder.onnx         image [1,3,1024,1024] -> vision_feat [1,256,64,64], vision_pos [1,256,64,64],
                                         high_res_0 [1,32,256,256], high_res_1 [1,64,128,128]
  sam2.1_tiny_video.memory_attention.onnx  the current frame conditioned on the memory bank (RoPE rewritten without complex
                                         numbers; any number of spatial memories N and object pointers P)
  sam2.1_tiny_video.decoder_multi.onnx   prompt encoder + mask decoder, 3 masks, the best by IoU (tracking frames, 1 click)
  sam2.1_tiny_video.decoder_single.onnx  the same with one mask (a box prompt), with the stability fallback
  sam2.1_tiny_video.memory_encoder.onnx  a frame's mask -> its memory (maskmem features + positional encoding)
  sam2.1_tiny_video.constants.bin        no_mem_embed (256 float32, little endian)

The model is built exactly as `build_sam2_video_predictor` builds it (the same hydra overrides), from the official
checkpoint `sam2.1_hiera_tiny.pt` (Apache-2.0). Run with the research venv's Python (torch, sam2 1.1.0, onnx):

  python -I export_sam2_video.py <sam2.1_hiera_tiny.pt> <output folder>

Nothing is downloaded. The produced files are pinned by size and SHA-256 in `src/Vision/People/PeopleModel.cs`.
"""
import math
import os
import struct
import sys

import torch
import torch.nn.functional as F
from torch import nn

from sam2.build_sam import build_sam2_video_predictor
from sam2.modeling.position_encoding import compute_axial_cis
from sam2.modeling.sam2_base import NO_OBJ_SCORE

OPSET = 17


def rope_cos_sin(dim, end_x, end_y, theta=10000.0):
    cis = compute_axial_cis(dim=dim, end_x=end_x, end_y=end_y, theta=theta)  # complex [HW, dim/2]
    return cis.real.float().contiguous(), cis.imag.float().contiguous()


def rotate(x, cos, sin):
    """x [B, h, L, d] with L = r * HW: rotates each pair (x0, x1) by the axial angle of its position (repeated r times)."""
    b, h, l, d = x.shape
    hw = cos.shape[0]
    x = x.reshape(b, h, -1, hw, d // 2, 2)
    x0, x1 = x[..., 0], x[..., 1]
    c = cos.view(1, 1, 1, hw, d // 2)
    s = sin.view(1, 1, 1, hw, d // 2)
    out = torch.stack([x0 * c - x1 * s, x0 * s + x1 * c], dim=-1)
    return out.reshape(b, h, l, d)


def attention(att, q, k, v):
    q = att._separate_heads(q, att.num_heads)
    k = att._separate_heads(k, att.num_heads)
    v = att._separate_heads(v, att.num_heads)
    scale = 1.0 / math.sqrt(q.shape[-1])
    w = torch.softmax(torch.matmul(q, k.transpose(-2, -1)) * scale, dim=-1)
    return att.out_proj(att._recombine_heads(torch.matmul(w, v)))


class MemoryAttentionGraph(nn.Module):
    """The memory attention of SAM2Base._prepare_memory_conditioned_features for a non-initial frame, with the memory
    given in two parts: the spatial memories (RoPE on their keys) and the object-pointer tokens (no RoPE).

    Inputs (sequence first, batch 1):
      curr [4096, 1, 256], curr_pos [4096, 1, 256]
      memory_spatial [N*4096, 1, 64], memory_spatial_pos [N*4096, 1, 64]  (pos already includes the temporal encoding)
      memory_ptr [P*4, 1, 64], memory_ptr_pos [P*4, 1, 64]
    Output: pix_feat_with_mem [1, 256, 64, 64]."""

    def __init__(self, model):
        super().__init__()
        self.ma = model.memory_attention
        layer0 = self.ma.layers[0]
        d = layer0.self_attn.internal_dim // layer0.self_attn.num_heads
        cos, sin = rope_cos_sin(d, 64, 64)
        self.register_buffer("cos_sa", cos)
        self.register_buffer("sin_sa", sin)
        dc = layer0.cross_attn_image.internal_dim // layer0.cross_attn_image.num_heads
        cos, sin = rope_cos_sin(dc, 64, 64)
        self.register_buffer("cos_ca", cos)
        self.register_buffer("sin_ca", sin)

    def forward(self, curr, curr_pos, memory_spatial, memory_spatial_pos, memory_ptr, memory_ptr_pos):
        output = curr + 0.1 * curr_pos  # pos_enc_at_input
        output = output.transpose(0, 1)
        query_pos = curr_pos.transpose(0, 1)
        ms = memory_spatial.transpose(0, 1)
        mps = memory_spatial_pos.transpose(0, 1)
        mp = memory_ptr.transpose(0, 1)
        mpp = memory_ptr_pos.transpose(0, 1)
        for layer in self.ma.layers:
            # self attention (RoPE on q and k, no positional encoding added)
            t2 = layer.norm1(output)
            sa = layer.self_attn
            q = sa._separate_heads(sa.q_proj(t2), sa.num_heads)
            k = sa._separate_heads(sa.k_proj(t2), sa.num_heads)
            v = sa._separate_heads(sa.v_proj(t2), sa.num_heads)
            q = rotate(q, self.cos_sa, self.sin_sa)
            k = rotate(k, self.cos_sa, self.sin_sa)
            scale = 1.0 / math.sqrt(q.shape[-1])
            w = torch.softmax(torch.matmul(q, k.transpose(-2, -1)) * scale, dim=-1)
            output = output + sa.out_proj(sa._recombine_heads(torch.matmul(w, v)))
            # cross attention to the memory: keys = memory + pos, RoPE on the spatial keys only (repeated per memory)
            t2 = layer.norm2(output)
            ca = layer.cross_attn_image
            q = ca._separate_heads(ca.q_proj(t2), ca.num_heads)
            ks = ca._separate_heads(ca.k_proj(ms + mps), ca.num_heads)
            kp = ca._separate_heads(ca.k_proj(mp + mpp), ca.num_heads)
            v = ca._separate_heads(ca.v_proj(torch.cat([ms, mp], dim=1)), ca.num_heads)
            q = rotate(q, self.cos_ca, self.sin_ca)
            ks = rotate(ks, self.cos_ca, self.sin_ca)
            k = torch.cat([ks, kp], dim=2)
            scale = 1.0 / math.sqrt(q.shape[-1])
            w = torch.softmax(torch.matmul(q, k.transpose(-2, -1)) * scale, dim=-1)
            output = output + ca.out_proj(ca._recombine_heads(torch.matmul(w, v)))
            # MLP
            t2 = layer.norm3(output)
            output = output + layer.linear2(layer.activation(layer.linear1(t2)))
        out = self.ma.norm(output)  # [1, 4096, 256]
        return out.permute(0, 2, 1).reshape(1, 256, 64, 64)


class EncoderGraph(nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, image):
        out = self.model.forward_image(image)
        fpn = out["backbone_fpn"]
        pos = out["vision_pos_enc"]
        return fpn[2], pos[2], fpn[0], fpn[1]


class DecoderGraph(nn.Module):
    """SAM2Base._forward_sam_heads: point_coords in the 1024 input space, labels int32 (1 positive, 0 negative, 2 / 3 box
    corners, -1 padding). Outputs the best mask's low-resolution logits (NO_OBJ_SCORE where no object appears), its IoU,
    the object pointer and the object score logit."""

    def __init__(self, model, multimask):
        super().__init__()
        self.model = model
        self.multimask = multimask

    def forward(self, pix_feat, high_res_0, high_res_1, point_coords, point_labels):
        _, _, ious, low_res_masks, _, obj_ptr, object_score_logits = self.model._forward_sam_heads(
            backbone_features=pix_feat,
            point_inputs={"point_coords": point_coords, "point_labels": point_labels},
            mask_inputs=None,
            high_res_features=[high_res_0, high_res_1],
            multimask_output=self.multimask,
        )
        best_iou = ious.max(dim=-1, keepdim=True).values
        return low_res_masks, best_iou, obj_ptr, object_score_logits


class MemoryEncoderGraph(nn.Module):
    """SAM2Base._encode_new_memory from the low-resolution mask (upsampled as in _forward_sam_heads). binarize = 1 for a
    prompted frame (binarize_mask_from_pts_for_mem_enc), 0 otherwise."""

    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, vision_feat, low_res_mask, object_score_logits, binarize):
        m = self.model
        high = F.interpolate(low_res_mask, size=(m.image_size, m.image_size), mode="bilinear", align_corners=False)
        soft = torch.sigmoid(high)
        hard = (high > 0).float()
        mask_for_mem = binarize * hard + (1 - binarize) * soft
        mask_for_mem = mask_for_mem * m.sigmoid_scale_for_mem_enc + m.sigmoid_bias_for_mem_enc
        out = m.memory_encoder(vision_feat, mask_for_mem, skip_mask_sigmoid=True)
        feats = out["vision_features"]
        pos = out["vision_pos_enc"][-1]
        appearing = (object_score_logits > 0).float()
        feats = feats + (1 - appearing[..., None, None]) * m.no_obj_embed_spatial[..., None, None]
        return feats, pos


class PointerPosGraph(nn.Module):
    """The temporal encoding of the object pointers: get_1d_sine_pe(t / t_max) -> obj_ptr_tpos_proj. Input t_norm [P]
    (already divided by t_max), output [P, 64]."""

    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, t_norm):
        from sam2.modeling.sam2_utils import get_1d_sine_pe
        pe = get_1d_sine_pe(t_norm, dim=self.model.hidden_dim)
        return self.model.obj_ptr_tpos_proj(pe)


class Half(nn.Module):
    """A graph computed in float16 with float32 inputs and outputs: the memory attention's attention matrices
    ([4096 x (N*4096 + P*4)] per layer) are memory-bound on DirectML; half precision halves their traffic (73 -> 33 ms with a
    full memory bank on an RTX 4070 SUPER) with the masks unchanged within the validation tolerance."""

    def __init__(self, graph):
        super().__init__()
        self.graph = graph.half()

    def forward(self, *inputs):
        return self.graph(*[x.half() for x in inputs]).float()


def export(module, args, path, input_names, output_names, dynamic_axes=None):
    torch.onnx.export(module, args, path, opset_version=OPSET, input_names=input_names, output_names=output_names,
                      dynamic_axes=dynamic_axes, do_constant_folding=True, dynamo=False)
    print("wrote", path, os.path.getsize(path))


def main():
    ckpt, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    model = build_sam2_video_predictor("configs/sam2.1/sam2.1_hiera_t.yaml", ckpt, device="cpu")
    model.eval()
    p = lambda name: os.path.join(out, f"sam2.1_tiny_video.{name}")
    with torch.no_grad():
        export(EncoderGraph(model), (torch.randn(1, 3, 1024, 1024),), p("encoder.onnx"), ["image"],
               ["vision_feat", "vision_pos", "high_res_0", "high_res_1"])
        n, ptr = 3, 8
        attention_graph = MemoryAttentionGraph(model)
        export(Half(attention_graph) if "--fp32-attention" not in sys.argv else attention_graph,
               (torch.randn(4096, 1, 256), torch.randn(4096, 1, 256), torch.randn(n * 4096, 1, 64), torch.randn(n * 4096, 1, 64),
                torch.randn(ptr * 4, 1, 64), torch.randn(ptr * 4, 1, 64)),
               p("memory_attention.onnx"),
               ["curr", "curr_pos", "memory_spatial", "memory_spatial_pos", "memory_ptr", "memory_ptr_pos"], ["pix_feat_with_mem"],
               {"memory_spatial": {0: "ns"}, "memory_spatial_pos": {0: "ns"}, "memory_ptr": {0: "np"}, "memory_ptr_pos": {0: "np"}})
        dec_args = (torch.randn(1, 256, 64, 64), torch.randn(1, 32, 256, 256), torch.randn(1, 64, 128, 128),
                    torch.rand(1, 2, 2) * 1024, torch.tensor([[2, 3]], dtype=torch.int32))
        export(DecoderGraph(model, True), dec_args, p("decoder_multi.onnx"),
               ["pix_feat", "high_res_0", "high_res_1", "point_coords", "point_labels"],
               ["low_res_mask", "iou", "obj_ptr", "object_score_logits"],
               {"point_coords": {1: "k"}, "point_labels": {1: "k"}})
        export(DecoderGraph(model, False), dec_args, p("decoder_single.onnx"),
               ["pix_feat", "high_res_0", "high_res_1", "point_coords", "point_labels"],
               ["low_res_mask", "iou", "obj_ptr", "object_score_logits"],
               {"point_coords": {1: "k"}, "point_labels": {1: "k"}})
        export(MemoryEncoderGraph(model),
               (torch.randn(1, 256, 64, 64), torch.randn(1, 1, 256, 256), torch.randn(1, 1), torch.ones(1, 1, 1, 1)),
               p("memory_encoder.onnx"), ["vision_feat", "low_res_mask", "object_score_logits", "binarize"],
               ["maskmem_features", "maskmem_pos"])
        export(PointerPosGraph(model), (torch.rand(5),), p("pointer_pos.onnx"), ["t_norm"], ["obj_pos"], {"t_norm": {0: "p"}})
        with open(p("constants.bin"), "wb") as f:
            f.write(b"SAM2CST1")
            for name, t in [("no_mem_embed", model.no_mem_embed), ("maskmem_tpos_enc", model.maskmem_tpos_enc)]:
                data = t.detach().float().contiguous().view(-1).numpy()
                f.write(struct.pack("<I", data.size))
                f.write(data.astype("<f4").tobytes())
        print("constants", model.no_mem_embed.shape, model.maskmem_tpos_enc.shape, "num_maskmem", model.num_maskmem,
              "max_obj_ptrs", model.max_obj_ptrs_in_encoder, "NO_OBJ", NO_OBJ_SCORE)


if __name__ == "__main__":
    main()
