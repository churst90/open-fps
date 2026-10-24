using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OpenFPS.Audio.Core;

namespace OpenFPS.Audio.Fmod;

/// <summary>
/// Where the loader finds a sound by its id: the files under <see cref="Folder"/> (the facade's
/// <c>Paths.Sounds</c>, so the loader and the bank agree), and the sounds a host handed in as bytes, which win over a
/// file of the same id. A packaged game reads its sounds from an encrypted package in memory and has no folder.
///
/// The ids come from the server and from a map's owner, so an id that leaves the folder, or names a network share
/// (which would hand Windows' login hash to whoever runs it), is refused and reads as missing.
///
/// Memory: <see cref="Add(string, byte[])"/> copies the bytes into a block the collector never moves, because FMOD
/// reads them from the loader thread after the call (a non-blocking load) and for as long as a sound made from them
/// lives. The library owns the copy; the host may free or change its own. The copies are freed when the mixer is
/// disposed (FmodAudioProvider.Dispose), after every sound made from them is released, and not before. Replacing or
/// removing an id keeps the old copy until then too, as a sound already playing from it goes on playing.
/// </summary>
public sealed class SoundSource
{
    /// <summary>One handed-in sound. <see cref="Generation"/> tells the loader its cached copy is out of date.</summary>
    internal sealed class Held
    {
        public required byte[] Bytes;
        public required IntPtr Address;
        public required long Generation;
    }

    private readonly ConcurrentDictionary<string, Held> _held = new();
    private readonly List<Held> _kept = new();
    private long _generation;
    private volatile string _folder;
    private bool _closed;

    /// <param name="folder">The sound bank's folder; none: ASSETS/SOUNDS beside the program.</param>
    public SoundSource(string? folder = null) => _folder = folder ?? DefaultFolder;

    /// <summary>Where the program's own sounds are: ASSETS/SOUNDS beside it.</summary>
    public static string DefaultFolder => Path.Combine(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), "ASSETS", "SOUNDS");

    /// <summary>The folder a sound id is read under. The facade sets it from <see cref="AudioDataPaths.Sounds"/> before
    /// anything loads.</summary>
    public string Folder { get => _folder; set => _folder = value; }

    /// <summary>The number of sounds handed in.</summary>
    internal int Count => _held.Count;

    /// <summary>Takes a sound's bytes (a whole file's, in any format FMOD reads) under <paramref name="id"/>, from any
    /// thread but the ones that place sounds. A second call with the same id replaces it for every play that starts
    /// afterwards; one playing carries on from the old. False for an empty id or no bytes, or after the mixer is
    /// disposed.</summary>
    public bool Add(string id, byte[] bytes) => Add(id, bytes.AsSpan());

    /// <summary><see cref="Add(string, byte[])"/> from a stream, read to its end.</summary>
    public bool Add(string id, Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Add(id, copy.GetBuffer().AsSpan(0, (int)copy.Length));
    }

    private bool Add(string id, ReadOnlySpan<byte> bytes)
    {
        if (string.IsNullOrEmpty(id) || bytes.Length == 0) return false;
        // A copy of megabytes: the host's own thread is the place for it, never the one that places sounds.
        PlacementThread.Heavy("a host's sound bytes taken in");
        var pinned = GC.AllocateUninitializedArray<byte>(bytes.Length, pinned: true);
        bytes.CopyTo(pinned);
        var held = new Held
        {
            Bytes = pinned,
            Address = Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0),
            Generation = Interlocked.Increment(ref _generation),
        };
        lock (_kept)
        {
            if (_closed) return false;
            _kept.Add(held);
            _held[id] = held;
        }
        return true;
    }

    /// <summary>Forgets the bytes under an id: a file of that name, if any, is heard from the next play. True if there
    /// were any.</summary>
    public bool Remove(string id) => _held.TryRemove(id, out _);

    /// <summary>Whether a sound was handed in under the id.</summary>
    public bool Has(string id) => _held.ContainsKey(id);

    internal bool TryGet(string id, out Held held) => _held.TryGetValue(id, out held!);

    /// <summary>Lets every copy go. The mixer's dispose calls it once no sound of them is left.</summary>
    internal void Close()
    {
        lock (_kept) { _closed = true; _held.Clear(); _kept.Clear(); }
    }

    /// <summary>The file this id names under <see cref="Folder"/>, without an extension tried; empty if it would leave the
    /// folder or names a network share.</summary>
    public string PathOf(string soundId) => PathIn(soundId, _folder);

    /// <summary>Where a sound id is read under a folder (FmodAudioProvider.SoundFilePath is this under the program's own).</summary>
    internal static string PathIn(string soundId, string soundsFolder)
    {
        if (string.IsNullOrEmpty(soundId)) return "";
        string baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        // An id that names ASSETS is a path from the program's folder, as it always was.
        if (soundId.Contains("ASSETS", StringComparison.OrdinalIgnoreCase))
            return Inside(soundId, baseDir, Path.IsPathFullyQualified(soundId) ? soundId : Path.Combine(baseDir, soundId));
        string normId = soundId.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return Inside(soundId, soundsFolder, Path.Combine(soundsFolder, normId));
    }

    /// <summary>The path, if it is inside the folder and names no network share; empty if not.</summary>
    private static string Inside(string id, string folder, string path)
    {
        if (id.StartsWith(@"\\", StringComparison.Ordinal) || id.StartsWith("//", StringComparison.Ordinal)) return "";
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception) { return ""; }
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : "";
    }
}
