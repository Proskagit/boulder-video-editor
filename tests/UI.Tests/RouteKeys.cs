using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Motion;

namespace AiVideoEditor.UI.Tests;

/// <summary>D053: the camera-motion key of a test clip's single route.</summary>
internal static class RouteKeys
{
    public static RouteKey Of(VideoClip clip) => new(clip.Id, clip.Routes[0].Id);
}
