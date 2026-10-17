namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// Where one audio world reads and keeps its data, as the host says (docs/SOUND_LIBRARY_BOUNDARY.md, 7.4): the
/// sound bank, and the door models' renders kept on disk (DoorRenderCache), the folder shipped with the program
/// first, then the one it writes. Nothing in the library works a folder out for itself.
///
/// open-fps's client fills it as the folders were always worked out (ClientAudioPaths.ForPlayer); a world made
/// without one (a test's, the lab's) reads what ships beside the program and keeps no renders
/// (<see cref="Beside"/>).
/// </summary>
/// <param name="Sounds">The sound bank: open-fps's is ASSETS/SOUNDS beside the program.</param>
/// <param name="RenderCache">Where door renders are written, and read after <paramref name="ShippedRenderCache"/>;
/// null keeps none.</param>
/// <param name="ShippedRenderCache">The door renders shipped with the program, read only; null for none.</param>
public sealed record AudioDataPaths(string Sounds, string? RenderCache, string? ShippedRenderCache)
{
    /// <summary>What ships beside a program in <paramref name="baseDirectory"/>: its sound bank and its door renders
    /// (ASSETS/SOUNDS, ASSETS/rendercache/&lt;this build's door models&gt;), and no folder of renders to write.</summary>
    public static AudioDataPaths Beside(string baseDirectory) => new(
        Path.Combine(baseDirectory, "ASSETS", "SOUNDS"),
        null,
        Path.Combine(baseDirectory, "ASSETS", "rendercache", DoorRenderCache.Name));
}
