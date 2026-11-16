namespace AiVideoEditor.Core.People;

/// <summary>
/// The people analyses of the project's videos by asset (D061 §5): the tracks (Follow, the occlusion's prompts) and the
/// masks (Occlusion). Derived from the videos, never part of the project; the Preview's snapshot and the export's preflight
/// get the same instances, so they draw the same.
/// </summary>
public sealed record PeopleAnalyses(IReadOnlyDictionary<Guid, PersonTracks> Tracks, IReadOnlyDictionary<Guid, PersonMaskTrack> Masks)
{
    public static readonly PeopleAnalyses None = new(new Dictionary<Guid, PersonTracks>(), new Dictionary<Guid, PersonMaskTrack>());
}
