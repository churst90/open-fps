using System.Runtime.InteropServices;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// The native libraries the audio stack resolves at runtime, and what is lost when one is absent, said
/// the same way by both heads. A game that quietly starts without HRTF is worse than one that refuses:
/// from inside, the world just sounds flat.
/// </summary>
public static class NativeAudioLibraries
{
    /// <summary>One expected native library: its file name on this platform, whether the client can
    /// meaningfully run without it, and the sentence spoken/logged when it is missing.</summary>
    public readonly record struct NativeLib(string FileName, bool Required, string Degradation);

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Platform file name of the Steam Audio library (phonon).</summary>
    public static string PhononFileName => IsWindows ? "phonon.dll" : "libphonon.so";

    /// <summary>Platform file name of the FMOD core library.</summary>
    public static string FmodFileName => IsWindows ? "fmod.dll" : "libfmod.so";

    /// <summary>Every native library the audio stack expects next to the executable, in report order.</summary>
    public static IReadOnlyList<NativeLib> Expected { get; } = IsWindows
        ?
        [
            new("fmod.dll", true, "no sound at all"),
            new("fmodstudio.dll", true, "no sound at all"),
            new("phonon.dll", true, "no HRTF binaural — spatial cues collapse to stereo panning, and geometry-driven occlusion, pathing and reverb are all off"),
        ]
        :
        [
            new("libfmod.so", true, "no sound at all"),
            new("libfmodstudio.so", true, "no sound at all"),
            new("libphonon.so", true, "no HRTF binaural — spatial cues collapse to stereo panning, and geometry-driven occlusion, pathing and reverb are all off"),
        ];

    /// <summary>The directory the runtime resolves native libraries from (next to the executable).</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>Every expected library that is not present on disk.</summary>
    public static List<NativeLib> FindMissing()
    {
        string baseDir = BaseDirectory;
        var missing = new List<NativeLib>();
        foreach (var lib in Expected)
            if (!File.Exists(Path.Combine(baseDir, lib.FileName)))
                missing.Add(lib);
        return missing;
    }

    /// <summary>True when the given library file is present next to the executable.</summary>
    public static bool IsPresent(string fileName) => File.Exists(Path.Combine(BaseDirectory, fileName));

    /// <summary>One sentence, logged and spoken, naming each missing library and what it costs; empty when
    /// none is missing.</summary>
    public static string DescribeMissing(IReadOnlyList<NativeLib> missing)
    {
        if (missing.Count == 0) return string.Empty;
        var parts = missing.Select(m => $"{m.FileName} ({m.Degradation})");
        return $"Missing audio libraries in {BaseDirectory}: {string.Join("; ", parts)}.";
    }
}
