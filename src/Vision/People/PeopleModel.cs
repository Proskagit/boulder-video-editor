namespace AiVideoEditor.Vision.People;

/// <summary>
/// The pinned people models "people-v2" (D061 §5, D066 §3; docs/AI_MODEL.md "People models"): all Apache-2.0, all local,
/// never downloaded by the application. «people-v1» differed only in the detector's file (the same outputs, see below).
/// <list type="bullet">
/// <item>RF-DETR-M (Roboflow, Apache-2.0) at 576 × 576 — the person detector, exported by
/// <c>tools/people-models/export_rfdetr.py</c> from the official checkpoint with Roboflow's <c>rfdetr</c> 1.12.1 (S1, D066 §3); on
/// 180 real frames its people are those of people-v1's third-party conversion (rtmlib 0.0.16), box for box.</item>
/// <item>RTMPose-m body7 256 × 192 (OpenMMLab, Apache-2.0) — the 17 COCO keypoints; the torso centre is the camera's
/// anchor.</item>
/// <item>SAM 2.1 Hiera tiny (Meta, Apache-2.0) in its video mode — six graphs exported by
/// <c>tools/people-models/export_sam2_video.py</c> from the official checkpoint (the memory attention in float16).</item>
/// </list>
/// A file with another size or SHA-256 is never loaded.
/// </summary>
public static class PeopleModel
{
    public const string Id = "people-v2";

    /// <summary>The folder name under %LOCALAPPDATA%\AiVideoEditor\models.</summary>
    public const string FolderName = "people-v2";

    public static readonly HoldModelFile Detector =
        new("rfdetr_m_576.onnx", 133_424_287, "ec23de6746c6737b47c34fd24f06583e80a2253924efc32c27b716157ae1632a");

    public static readonly HoldModelFile Pose =
        new("rtmpose_m_body7_256x192.onnx", 54_330_655, "5c0a4bf67953e6d2ac43ce15e77dc9d5d354ae18430a47d2c5963a7bc5683e3c");

    public static readonly HoldModelFile SamEncoder =
        new("sam2.1_tiny_video.encoder.onnx", 109_408_012, "8bd43ed78fb7c56eebec730592003a8b795604a61c22f9a68229c3da770016cf");
    public static readonly HoldModelFile SamMemoryAttention =
        new("sam2.1_tiny_video.memory_attention.onnx", 16_238_215, "5b3e99ecba4374f0b7e3adfafa956909a874673a14852ae2853c4c571f40b7df");
    public static readonly HoldModelFile SamDecoderMulti =
        new("sam2.1_tiny_video.decoder_multi.onnx", 17_884_116, "136f739f10fc44b79bb09bdb6a207577625711a4186175d2b6bf978bfbb31cc1");
    public static readonly HoldModelFile SamDecoderSingle =
        new("sam2.1_tiny_video.decoder_single.onnx", 17_893_385, "a63a960ae5a23c9509605f2425727065d20caad2ac2677ef87e4cf3689eaa4f1");
    public static readonly HoldModelFile SamMemoryEncoder =
        new("sam2.1_tiny_video.memory_encoder.onnx", 5_591_772, "0be6bdbea360072de1fa6a3b042f5415fbe2bff4618670f5846e82c852c0236c");
    public static readonly HoldModelFile SamPointerPos =
        new("sam2.1_tiny_video.pointer_pos.onnx", 67_131, "b8db9c1ddc26c967caa7cf2d49b1528d2e0ff55dfef0e689d99c9a2caf06c1da");
    public static readonly HoldModelFile SamConstants =
        new("sam2.1_tiny_video.constants.bin", 2_832, "3ffeae7ff4579e5913f8fbf4de1a6ae0e1ccc7a09157052d7d14da19736ab61b");

    /// <summary>What Follow needs.</summary>
    public static readonly IReadOnlyList<HoldModelFile> DetectionFiles = new[] { Detector, Pose };

    /// <summary>What Occlusion needs on top.</summary>
    public static readonly IReadOnlyList<HoldModelFile> SegmentationFiles = new[]
    {
        SamEncoder, SamMemoryAttention, SamDecoderMulti, SamDecoderSingle, SamMemoryEncoder, SamPointerPos, SamConstants
    };

    /// <summary>The detector's input (square, stretched).</summary>
    public const int DetectorInput = 576;

    /// <summary>The pose model's input.</summary>
    public const int PoseWidth = 192;
    public const int PoseHeight = 256;

    /// <summary>SAM's input (square, stretched) and its low-resolution mask.</summary>
    public const int SamInput = 1024;
    public const int SamMask = 256;
}
