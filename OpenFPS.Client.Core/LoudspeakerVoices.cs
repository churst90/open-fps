using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Signals;
using OpenFPS.Common;
using Serilog;

namespace OpenFPS.Client.Core;

/// <summary>
/// Programs played through loudspeakers, each rendered once through its speaker's chain
/// (LoudspeakerChain) on a worker and registered as a sound of its own, so the voice that plays it is an
/// ordinary recording's: placed, blocked, reflected and reverberated. Until its render is in, an emitter
/// is silent (an announcement starts at its next turn, half a second late at most).
///
/// The key carries the speaker's model as well as its name, so a speaker changed in the editor renders
/// again rather than playing the old one's buffer.
/// </summary>
internal sealed class LoudspeakerVoices
{
    private readonly AudioEngineFacade _audio;
    private readonly Dictionary<string, LoudspeakerRender> _ready = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rendering = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Key, LoudspeakerRender? Render)> _done = new();

    /// <summary>Render on the calling thread (a test, a replay), not a worker.</summary>
    public bool RenderInPlace { get; init; }

    public LoudspeakerVoices(AudioEngineFacade audio) => _audio = audio;

    /// <summary>The program <paramref name="program"/> (a resolved recording) through the speaker
    /// <paramref name="speaker"/>, if rendered; otherwise starts it. Game thread.</summary>
    public bool TryGet(string speaker, string program, bool loop, out LoudspeakerRender render)
    {
        Drain();
        render = null!;
        if (!Loudspeakers.TryGet(speaker, out var spec))
        {
            if (_failed.Add("speaker:" + speaker))
                Log.Warning("Loudspeaker '{Speaker}' is not a loudspeaker model; {Program} is silent", speaker, program);
            return false;
        }
        string key = KeyFor(speaker, spec, program, loop);
        if (_ready.TryGetValue(key, out var r)) { render = r; return true; }
        if (_failed.Contains(key) || !_rendering.Add(key)) return false;

        if (RenderInPlace) { _done.Enqueue((key, RenderOne(key, spec, program, loop))); Drain(); }
        else System.Threading.Tasks.Task.Run(() => _done.Enqueue((key, RenderOne(key, spec, program, loop))));
        if (_ready.TryGetValue(key, out r)) { render = r; return true; }
        return false;
    }

    /// <summary>The sound id a speaker's render of a program plays under.</summary>
    internal static string KeyFor(string speaker, LoudspeakerSpec spec, string program, bool loop)
        => Loudspeakers.Key($"{speaker}@{Fingerprint(spec):x8}{(loop ? "~loop" : "")}", program);

    /// <summary>A hash of the model's numbers, the same in every run (a string's own hash is not).</summary>
    private static uint Fingerprint(LoudspeakerSpec spec)
    {
        uint h = 2166136261;
        foreach (char c in System.Text.Json.JsonSerializer.Serialize(spec)) { h ^= c; h *= 16777619; }
        return h;
    }

    private LoudspeakerRender? RenderOne(string key, LoudspeakerSpec spec, string program, bool loop)
    {
        try
        {
            if (!_audio.TryDecodeMono(program, out var mono, out int rate) || mono.Length == 0)
            {
                Log.Warning("Loudspeaker: no recording {Program} to play through {Speaker}", program, spec.Name);
                return null;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = LoudspeakerChain.Render(key, spec, mono, rate, OpenFPS.Client.AudioEngine.Fmod.MixerQuality.MixerRate, loop);
            Log.Information("Loudspeaker {Key}: {Spl:F1} dB SPL at 1 m on the axis (gated speech), peaks {Peak:F1}; "
                          + "declared {Level:F1} dB full scale; clipped {Clip:P1} of samples, diaphragm to {X:F2} of its travel, "
                          + "compressor {Limit:F1} dB at most; room fed {Radiated:F1} dB under the axis; rendered in {Ms} ms",
                            key, r.SplDb, r.PeakSplDb, r.LevelDb, r.ClippedShare, r.MaxExcursion, r.MaxLimitDb,
                            20f * MathF.Log10(MathF.Max(1e-6f, r.RadiatedGain)), clock.ElapsedMilliseconds);
            return r;
        }
        catch (Exception ex)
        {
            Log.Warning("Loudspeaker: could not render {Key}: {Message}", key, ex.Message);
            return null;
        }
    }

    /// <summary>Registers what the workers have finished, on the thread that owns the engine.</summary>
    private void Drain()
    {
        while (_done.TryDequeue(out var done))
        {
            _rendering.Remove(done.Key);
            if (done.Render is not { } r) { _failed.Add(done.Key); continue; }
            if (!_audio.RegisterSynthesisedSoundFloat(r.Key, r.Pcm, r.Rate)) continue;   // the engine is not up: asked again
            EarTimbres.MeasureAsync(r.Key, r.Pcm, r.Rate);
            _ready[r.Key] = r;
        }
    }
}
