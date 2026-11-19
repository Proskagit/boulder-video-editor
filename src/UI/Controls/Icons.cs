namespace AiVideoEditor.UI.Controls;

/// <summary>
/// The icons of the frequent commands (D039 §7): drawn for this project on a 16 × 16 grid as line art (stroked, rounded
/// ends) — no external file, package or licence. Shown by <see cref="IconPath"/>, which takes the button's text colour,
/// so hover, pressed and disabled states colour the icon as they colour the label. Path data, parsed by the control once it
/// is shown (a geometry needs the platform's renderer).
/// </summary>
public static class Icons
{
    // D069 §8: the transition menu's diagrams, one per type of the library.
    public const string TransitionCrossDissolve = "M1.5,4 H9.5 V12 H1.5 Z M6.5,4 H14.5 V12 H6.5 Z";
    public const string TransitionDipToBlack = "M1.5,4 H14.5 V12 H1.5 Z M8,4 V12 M4,6 L6,10 M10,6 L12,10";
    public const string TransitionFlash = "M8,1.5 V4.5 M8,11.5 V14.5 M1.5,8 H4.5 M11.5,8 H14.5 M3.5,3.5 L5.5,5.5 M10.5,10.5 L12.5,12.5 M12.5,3.5 L10.5,5.5 M5.5,10.5 L3.5,12.5";
    public const string TransitionZoom = "M1.5,3 H14.5 V13 H1.5 Z M5,6 H11 V10 H5 Z";
    public const string TransitionPush = "M1.5,4 H14.5 V12 H1.5 Z M8,4 V12 M10,8 H14 M12,6 L14,8 L12,10";
    public const string TransitionBlur = "M2,5 C4,3.5 6,6.5 8,5 C10,3.5 12,6.5 14,5 M2,8 C4,6.5 6,9.5 8,8 C10,6.5 12,9.5 14,8 M2,11 C4,9.5 6,12.5 8,11 C10,9.5 12,12.5 14,11";
    public const string TransitionWipe = "M1.5,4 H14.5 V12 H1.5 Z M10,4 V12 M5,8 H9 M7,6 L9,8 L7,10";
    public const string TransitionCircle = "M1.5,3 H14.5 V13 H1.5 Z M5,8 A3,3 0 1 0 11,8 A3,3 0 1 0 5,8 Z";
    public const string TransitionDoors = "M1.5,4 H14.5 V12 H1.5 Z M6,4 V12 M10,4 V12 M5,8 H2.5 M11,8 H13.5";
    public const string TransitionZoomOut = "M1.5,3 H14.5 V13 H1.5 Z M6,6.5 H10 V9.5 H6 Z M2.5,4 L5,6 M13.5,4 L11,6 M2.5,12 L5,10 M13.5,12 L11,10";
    public const string TransitionSpin = "M13,8 A5,5 0 1 1 11,4 M11,1.5 V4.5 H14";
    public const string TransitionWhip = "M5.5,4 H14.5 V12 H5.5 Z M1.5,6 H4 M1,8 H4 M1.5,10 H4";
    public const string TransitionGlitch = "M1.5,4 H8 V7 H14.5 M1.5,9 H6 V12 H14.5 M3,6 H11 M5,10.5 H13";

    public const string Play = "M4.5,2.5 L13.5,8 L4.5,13.5 Z";
    public const string Pause = "M4.5,2.5 V13.5 M11.5,2.5 V13.5";
    public const string Stop = "M3.5,3.5 H12.5 V12.5 H3.5 Z";
    public const string PreviousFrame = "M3,2.5 V13.5 M13,2.5 L6,8 L13,13.5 Z";
    public const string NextFrame = "M13,2.5 V13.5 M3,2.5 L10,8 L3,13.5 Z";
    public const string Loop = "M2.5,9 V7.5 A3.5,3.5 0 0 1 6,4 H13 M10.5,1.5 L13,4 L10.5,6.5 M13.5,7 V8.5 A3.5,3.5 0 0 1 10,12 H3 M5.5,9.5 L3,12 L5.5,14.5";
    public const string Undo = "M5.5,3 L2.5,6 L5.5,9 M2.5,6 H10 A3.5,3.5 0 0 1 10,13 H6";
    public const string Redo = "M10.5,3 L13.5,6 L10.5,9 M13.5,6 H6 A3.5,3.5 0 0 0 6,13 H10";
    /// <summary>D063: a keyframe — a diamond.</summary>
    public const string Keyframe = "M8,1.5 L14.5,8 L8,14.5 L1.5,8 Z";

    /// <summary>D064: the mixer — three faders with their knobs.</summary>
    public const string Mixer = "M3.5,2 V14 M8,2 V14 M12.5,2 V14 M1.5,10 H5.5 M6,5 H10 M10.5,8 H14.5";

    public const string Split = "M3,12 A2,2 0 1 0 7,12 A2,2 0 1 0 3,12 Z M9,12 A2,2 0 1 0 13,12 A2,2 0 1 0 9,12 Z M6.3,10.4 L12,2 M9.7,10.4 L4,2";
    public const string Delete = "M2.5,4 H13.5 M6,4 V2.5 H10 V4 M4,4 L4.8,14 H11.2 L12,4 M6.8,6.5 V11.5 M9.2,6.5 V11.5";
    public const string Snap = "M3,2.5 V8 A5,5 0 0 0 13,8 V2.5 H10 V8 A2,2 0 0 1 6,8 V2.5 Z M3,5.5 H6 M10,5.5 H13";
    public const string Move = "M8,1.5 V14.5 M1.5,8 H14.5 M6,3.5 L8,1.5 L10,3.5 M6,12.5 L8,14.5 L10,12.5 M3.5,6 L1.5,8 L3.5,10 M12.5,6 L14.5,8 L12.5,10";
    public const string Crop = "M4.5,1.5 V11.5 H14.5 M1.5,4.5 H11.5 V14.5";
    public const string Eye = "M1.5,8 C3.5,4 12.5,4 14.5,8 C12.5,12 3.5,12 1.5,8 Z M6,8 A2,2 0 1 0 10,8 A2,2 0 1 0 6,8 Z";
    public const string Lock = "M3.5,7.5 H12.5 V14 H3.5 Z M5.5,7.5 V5 A2.5,2.5 0 0 1 10.5,5 V7.5";
    public const string Import = "M8,1.5 V9.5 M5,6.5 L8,9.5 L11,6.5 M2.5,10.5 V13.5 H13.5 V10.5";

    // The timeline's tool bar (D040 §5), with their labels.
    public const string VideoTrack = "M1.5,4.5 H14.5 V11.5 H1.5 Z M4.5,4.5 V11.5 M11.5,4.5 V11.5";
    public const string AudioTrack = "M2,7.5 V8.5 M4.5,6 V10 M7,3.5 V12.5 M9.5,5.5 V10.5 M12,7 V9 M14,7.5 V8.5";
    public const string Text = "M3,3 H13 M8,3 V13 M6,13 H10";

    /// <summary>D045: a template — a frame's corner marks around a bar.</summary>
    public const string Template = "M2,5.5 V2 H5.5 M10.5,2 H14 V5.5 M14,10.5 V14 H10.5 M5.5,14 H2 V10.5 M5,8 H11";
    public const string RippleDelete = "M2,5.5 L5.5,10.5 M5.5,5.5 L2,10.5 M14.5,4.5 H10.5 V11.5 H14.5 M9,8 H6.5 M8,6.5 L6.5,8 L8,9.5";
    public const string CloseGap = "M1.5,4.5 H5 V11.5 H1.5 M14.5,4.5 H11 V11.5 H14.5 M9.5,8 H6.5 M8,6.5 L6.5,8 L8,9.5";
    public const string Paste = "M4,3 H12 V14.5 H4 Z M6,1.5 H10 V4.5 H6 Z M6,8 H10 M6,10.5 H10";
    public const string Duplicate = "M5.5,5.5 H14 V14 H5.5 Z M3,10.5 H2 V2 H10.5 V3";
    public const string Dissolve = "M1.5,4 L14.5,12 M1.5,12 L14.5,4 M1.5,4 V12 M14.5,4 V12";
    public const string Link = "M6.5,9.5 L9.5,6.5 M7,4.5 L8.5,3 A3.2,3.2 0 0 1 13,7.5 L11.5,9 M9,11.5 L7.5,13 A3.2,3.2 0 0 1 3,8.5 L4.5,7";
    public const string Unlink = "M7,4.5 L8.5,3 A3.2,3.2 0 0 1 13,7.5 L11.5,9 M9,11.5 L7.5,13 A3.2,3.2 0 0 1 3,8.5 L4.5,7 M1.5,1.5 L3.5,3.5 M12.5,12.5 L14.5,14.5";
    public const string Fit = "M1.5,3 V13 M14.5,3 V13 M4,8 H12 M5.5,6.5 L4,8 L5.5,9.5 M10.5,6.5 L12,8 L10.5,9.5";

    // D068 item 4: the status bar's message kinds.
    public const string Warning = "M8,1.8 L14.6,13.6 H1.4 Z M8,6 V9.4 M8,11.6 V11.7";
    public const string Error = "M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z M5.6,5.6 L10.4,10.4 M10.4,5.6 L5.6,10.4";

    // D068 item 8: the Inspector rail's groups.
    public const string Hold = "M2.8,9.2 C2.6,5.6 5.4,3 8.8,3.3 C12.2,3.6 14,6.4 13.2,9.6 C12.4,12.6 9.2,13.8 6.2,13.2 C4.2,12.8 2.9,11.2 2.8,9.2 Z M6.2,8.3 C7.6,7.4 9.6,7.3 11,8";
    public const string Person = "M8,2 A2.2,2.2 0 1 0 8.01,2 Z M3.5,14.5 V11 A4.5,4.5 0 0 1 12.5,11 V14.5";
    public const string Pulse = "M1.5,8.5 H4.5 L6,4.5 L8.5,12.5 L10.5,6.5 L11.6,8.5 H14.5";

    // D068 item 18: reset a section.
    public const string Reset = "M3,8 A5,5 0 1 0 4.6,4.3 M2.5,2 V5 H5.5";
}
