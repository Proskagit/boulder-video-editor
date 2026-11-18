using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Localization;

/// <summary>The name a message gives a transition type of the library (D044 §1) — the Inspector's and the menu's name.</summary>
public static class TransitionTypeNames
{
    public static string Of(string typeId) => typeId switch
    {
        TransitionRules.DipToBlack => Messages.TransitionName_DipToBlack,
        TransitionRules.Flash => Messages.TransitionName_Flash,
        TransitionRules.Zoom => Messages.TransitionName_Zoom,
        TransitionRules.Push => Messages.TransitionName_Push,
        TransitionRules.Blur => Messages.TransitionName_Blur,
        TransitionRules.Wipe => Messages.TransitionName_Wipe,
        TransitionRules.Circle => Messages.TransitionName_Circle,
        TransitionRules.Doors => Messages.TransitionName_Doors,
        TransitionRules.ZoomOut => Messages.TransitionName_ZoomOut,
        TransitionRules.Spin => Messages.TransitionName_Spin,
        TransitionRules.Whip => Messages.TransitionName_Whip,
        TransitionRules.Glitch => Messages.TransitionName_Glitch,
        _ => Messages.TransitionName_CrossDissolve
    };
}
