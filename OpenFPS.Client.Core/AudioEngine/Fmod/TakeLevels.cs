using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Every take in a bank at the same level: the one its bank's median take has.
///
/// A recorded bank is many takes of one thing — forty-eight footsteps on carpet — played one at
/// random each time. The takes were not recorded at one level: measured, the carpet steps' peaks
/// spread over 22 dB, a third of them 14-22 dB under the loudest, and the concrete, tile and wood
/// banks the same. On a soft surface in a flat with an air conditioner in the window and traffic
/// outside, the quiet third were simply not there: "my foot steps seem to be dropping out ... I only
/// hear foot steps on few key presses". How loud one carpet step is against another is the
/// microphone and the session, not the floor; how loud carpet is against wood is the floor, and that
/// is kept, because each bank is brought to ITS OWN median.
///
/// The level of a take is its loudest 20 ms — a step is an impact, and its tail and the room tone
/// either side of it are not what the ear judges it by. Only folders of three or more takes are
/// banks; a lone file is played as it is.
/// </summary>
internal static class TakeLevels
{
    private const double WindowSeconds = 0.020;
    /// <summary>No take is raised or cut by more than this, dB: a take that far off its bank is
    /// something else, not a quiet recording of the same thing.</summary>
    private const float MaxCorrectionDb = 18f;

    private static readonly ConcurrentDictionary<string, Dictionary<string, float>> Banks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, float> Gains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The gain that brings this file to its bank's median, 1 if it is not in a bank or
    /// cannot be read.</summary>
    public static float GainFor(string path)
    {
        if (string.IsNullOrEmpty(path)) return 1f;
        if (Gains.TryGetValue(path, out float g)) return g;
        g = 1f;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir))
            {
                var bank = Banks.GetOrAdd(dir, Measure);
                if (bank.Count >= 3 && bank.TryGetValue(Path.GetFileName(path), out float own))
                {
                    var levels = bank.Values.OrderBy(v => v).ToList();
                    float median = levels[levels.Count / 2];
                    float db = Math.Clamp(median - own, -MaxCorrectionDb, MaxCorrectionDb);
                    g = MathF.Pow(10f, db / 20f);
                }
            }
        }
        catch { g = 1f; }
        Gains[path] = g;
        return g;
    }

    private static Dictionary<string, float> Measure(string dir)
    {
        var levels = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(dir, "*.wav"))
            if (ImpactDb(f) is float db) levels[Path.GetFileName(f)] = db;
        return levels;
    }

    /// <summary>The loudest 20 ms of a PCM WAV, dB full scale; null if it is not one this reads.</summary>
    internal static float? ImpactDb(string file)
    {
        using var br = new BinaryReader(File.OpenRead(file));
        if (new string(br.ReadChars(4)) != "RIFF") return null;
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") return null;
        int channels = 1, rate = 44100, bits = 16, format = 1;
        byte[]? data = null;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                format = br.ReadInt16(); channels = br.ReadInt16(); rate = br.ReadInt32();
                br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16);
            }
            else if (id == "data") { data = br.ReadBytes(size); break; }
            else br.ReadBytes(size + (size & 1));
        }
        if (data == null || channels < 1) return null;
        int bytes = bits / 8, frames = data.Length / (bytes * channels);
        if (frames == 0) return null;
        float Sample(int i)
        {
            int o = i * bytes * channels;
            return (format, bits) switch
            {
                (1, 16) => BitConverter.ToInt16(data, o) / 32768f,
                (1, 24) => ((data[o] | data[o + 1] << 8 | (sbyte)data[o + 2] << 16)) / 8388608f,
                (1, 32) => BitConverter.ToInt32(data, o) / 2147483648f,
                (3, 32) => BitConverter.ToSingle(data, o),
                (1, 8) => (data[o] - 128) / 128f,
                _ => 0f,
            };
        }
        int win = Math.Max(1, (int)(rate * WindowSeconds));
        double sum = 0, best = 0;
        var sq = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double s = Sample(i);
            sq[i] = s * s;
            sum += sq[i];
            if (i >= win) sum -= sq[i - win];
            if (i >= win - 1) best = Math.Max(best, sum / win);
        }
        if (frames < win) best = sum / frames;
        return (float)(10 * Math.Log10(best + 1e-12));
    }
}
