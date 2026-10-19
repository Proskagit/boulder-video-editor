namespace AiVideoEditor.UI.Controls;

/// <summary>
/// The icons of the frequent commands (D039 §7): drawn for this project on a 16 × 16 grid as line art (stroked, rounded
/// ends) — no external file, package or licence. Shown by <see cref="IconPath"/>, which takes the button's text colour,
/// so hover, pressed and disabled states colour the icon as they colour the label. Path data, parsed by the control once it
/// is shown (a geometry needs the platform's renderer).
/// </summary>
public static class Icons
{
    public const string Play = "M4.5,2.5 L13.5,8 L4.5,13.5 Z";
    public const string Pause = "M4.5,2.5 V13.5 M11.5,2.5 V13.5";
    public const string Stop = "M3.5,3.5 H12.5 V12.5 H3.5 Z";
    public const string PreviousFrame = "M3,2.5 V13.5 M13,2.5 L6,8 L13,13.5 Z";
    public const string NextFrame = "M13,2.5 V13.5 M3,2.5 L10,8 L3,13.5 Z";
    public const string Loop = "M2.5,9 V7.5 A3.5,3.5 0 0 1 6,4 H13 M10.5,1.5 L13,4 L10.5,6.5 M13.5,7 V8.5 A3.5,3.5 0 0 1 10,12 H3 M5.5,9.5 L3,12 L5.5,14.5";
    public const string Undo = "M5.5,3 L2.5,6 L5.5,9 M2.5,6 H10 A3.5,3.5 0 0 1 10,13 H6";
    public const string Redo = "M10.5,3 L13.5,6 L10.5,9 M13.5,6 H6 A3.5,3.5 0 0 0 6,13 H10";
    public const string Split = "M3,12 A2,2 0 1 0 7,12 A2,2 0 1 0 3,12 Z M9,12 A2,2 0 1 0 13,12 A2,2 0 1 0 9,12 Z M6.3,10.4 L12,2 M9.7,10.4 L4,2";
    public const string Delete = "M2.5,4 H13.5 M6,4 V2.5 H10 V4 M4,4 L4.8,14 H11.2 L12,4 M6.8,6.5 V11.5 M9.2,6.5 V11.5";
    public const string Snap = "M3,2.5 V8 A5,5 0 0 0 13,8 V2.5 H10 V8 A2,2 0 0 1 6,8 V2.5 Z M3,5.5 H6 M10,5.5 H13";
    public const string Move = "M8,1.5 V14.5 M1.5,8 H14.5 M6,3.5 L8,1.5 L10,3.5 M6,12.5 L8,14.5 L10,12.5 M3.5,6 L1.5,8 L3.5,10 M12.5,6 L14.5,8 L12.5,10";
    public const string Crop = "M4.5,1.5 V11.5 H14.5 M1.5,4.5 H11.5 V14.5";
    public const string Eye = "M1.5,8 C3.5,4 12.5,4 14.5,8 C12.5,12 3.5,12 1.5,8 Z M6,8 A2,2 0 1 0 10,8 A2,2 0 1 0 6,8 Z";
    public const string Lock = "M3.5,7.5 H12.5 V14 H3.5 Z M5.5,7.5 V5 A2.5,2.5 0 0 1 10.5,5 V7.5";
    public const string Import = "M8,1.5 V9.5 M5,6.5 L8,9.5 L11,6.5 M2.5,10.5 V13.5 H13.5 V10.5";
}
