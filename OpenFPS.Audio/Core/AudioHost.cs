namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// The host's recordings (open-fps: the sound bank by material and action, SoundMappingService): which file a sound
/// id plays, chosen among a category's takes, and which category a footfall or a landing on a material is.
/// </summary>
public interface IRecordings
{
    /// <summary>A file for <paramref name="id"/>: one of a category's takes, or the id itself.</summary>
    string ResolvePath(string id);

    /// <summary>The category of a foot or a thing striking <paramref name="material"/> this hard.</summary>
    string GetImpactSoundId(string material, float force);

    /// <summary>The category of a body landing on <paramref name="material"/>.</summary>
    string GetLandingSoundId(string material);
}
