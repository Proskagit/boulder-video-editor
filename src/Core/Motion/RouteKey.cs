namespace AiVideoEditor.Core.Motion;

/// <summary>D053: one route of one video clip — the key of its camera-motion track (each route has its own marking frame
/// and its own window) and of its tracking state.</summary>
public readonly record struct RouteKey(Guid ClipId, Guid RouteId);
