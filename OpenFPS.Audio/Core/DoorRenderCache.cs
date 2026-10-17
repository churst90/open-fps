using System.Security.Cryptography;
using System.Text;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// The door models' renders kept on disk. A door, a lock or a car window takes seconds of a core to
/// render (a glass door up to forty), and a first hearing waits only
/// <see cref="OpenFPS.Client.Core.WorldAudioPlayer.MaxRenderLateness"/> before it is dropped (Cody,
/// 2026-10-05: "it doesn't play a sound for the first few times I open it, then it works").
///
/// Read first from the folder shipped with the client (ASSETS/rendercache/BUILD), then from the player's
/// own cache. Both are named by <see cref="Name"/>, the hash of the door models' sources
/// (<see cref="DoorModelFingerprint"/>) and <c>Version</c>, so a changed model is never played from an
/// old render and other changes to OpenFPS.Common keep them. Glass breaking is not kept: every break has
/// a key of its own.
///
/// Kept at <see cref="TransientSynth.SampleRate"/> and brought to the mixer's rate on registration
/// (WorldAudioPlayer.AtMixerRate). If TransientSynth's rate ever changes, bump <c>Version</c>.
/// </summary>
public static class DoorRenderCache
{
    private const int Magic = 0x4352464F;   // "OFRC"
    private const int Version = 2;

    /// <summary>The folder name for this build's door models: their sources' hash and the file version.</summary>
    public static string Name => DoorModelFingerprint.Hash + "-v" + Version;

    // Stored as 16-bit scaled by the render's own peak, so a render over full scale (a car window) is
    // not clipped: half the size of floats, and the shipped folder is in every Windows zip.

    /// <summary>Where renders are written, and read after the shipped folder. Null turns the cache off.
    /// OPENFPS_RENDER_CACHE overrides it; the lab's prerender points it at the folder it is filling.</summary>
    public static string? Folder { get; set; } = DefaultFolder();

    /// <summary>The renders shipped with the client, read only.</summary>
    public static string ShippedFolder { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "ASSETS", "rendercache", Name);

    private static int _pruned;

    private static string? DefaultFolder()
    {
        string? env = Environment.GetEnvironmentVariable("OPENFPS_RENDER_CACHE");
        if (env == "off") return null;
        if (!string.IsNullOrWhiteSpace(env)) return env;
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) return null;
        return Path.Combine(root, "OpenFPS", "rendercache", Name);
    }

    /// <summary>Whether a key's render is kept: every model key but a pane of glass breaking.</summary>
    public static bool Keeps(string key) => !key.StartsWith(GlassFracture.KeyPrefix, StringComparison.Ordinal);

    private static string FileName(string key)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".pcm";

    /// <summary>A kept render of <paramref name="key"/>, and the level its full scale stands for.</summary>
    public static bool TryLoad(string key, out float[] pcm, out float fullScaleDb)
    {
        pcm = Array.Empty<float>(); fullScaleDb = 0f;
        if (!Keeps(key)) return false;
        string name = FileName(key);
        foreach (string? dir in new[] { ShippedFolder, Folder })
        {
            if (dir == null) continue;
            string path = Path.Combine(dir, name);
            try
            {
                if (!File.Exists(path)) continue;
                using var r = new BinaryReader(File.OpenRead(path));
                if (r.ReadInt32() != Magic || r.ReadInt32() != Version) continue;
                if (r.ReadString() != key) continue;     // a hash collision, or a file for another key
                fullScaleDb = r.ReadSingle();
                float peak = r.ReadSingle();
                int n = r.ReadInt32();
                if (n < 0 || n > 48000 * 120 || !float.IsFinite(peak)) continue;
                var bytes = r.ReadBytes(n * sizeof(short));
                if (bytes.Length != n * sizeof(short)) continue;
                var samples = new short[n];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                pcm = new float[n];
                float scale = peak / 32767f;
                for (int i = 0; i < n; i++) pcm[i] = samples[i] * scale;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                // A half-written file or an unreadable folder: render.
            }
        }
        return false;
    }

    /// <summary>Keeps a render. Written to a temporary file and moved, so a reader never sees half of it.
    /// Any failure only means it is rendered again next time.</summary>
    public static void Store(string key, float[] pcm, float fullScaleDb)
    {
        string? dir = Folder;
        if (dir == null || !Keeps(key) || pcm.Length == 0) return;
        try
        {
            Directory.CreateDirectory(dir);
            PruneOtherBuilds(dir);
            string path = Path.Combine(dir, FileName(key));
            string tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                float peak = 0f;
                foreach (float v in pcm) if (float.IsFinite(v)) peak = MathF.Max(peak, MathF.Abs(v));
                if (peak <= 0f) peak = 1f;
                var samples = new short[pcm.Length];
                for (int i = 0; i < pcm.Length; i++)
                    samples[i] = float.IsFinite(pcm[i]) ? (short)MathF.Round(Math.Clamp(pcm[i] / peak, -1f, 1f) * 32767f) : (short)0;
                w.Write(Magic); w.Write(Version); w.Write(key); w.Write(fullScaleDb); w.Write(peak); w.Write(pcm.Length);
                var bytes = new byte[samples.Length * sizeof(short)];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                w.Write(bytes);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The player's renders for other door models are stale: removed, once a run.</summary>
    private static void PruneOtherBuilds(string dir)
    {
        if (Interlocked.Exchange(ref _pruned, 1) == 1) return;
        try
        {
            var parent = Directory.GetParent(dir);
            if (parent == null || !string.Equals(parent.Name, "rendercache", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var other in parent.GetDirectories())
                if (!string.Equals(other.Name, Name, StringComparison.OrdinalIgnoreCase))
                    other.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
