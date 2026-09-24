using System;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Core;

/// <summary>What an interface sound means. Each has its own, so a menu, a chat and the world arriving
/// can be told apart by ear before a word is spoken.</summary>
public enum UiCue
{
    MenuMove, MenuSelect, MenuBack, MenuEdge,
    EnterWorld,
    ChatMap, ChatAll, ChatPrivate, ChatServer, ChatAdmin,
    VoiceOn,
}

/// <summary>
/// The sounds the interface makes: short, soft, in both ears and not in the world.
///
/// Synthesised rather than recorded, and deliberately plain — a menu tick is a sine blip with a bell's
/// decay, a chat is two or three notes — because they are heard constantly and must never compete with
/// the world or with speech. Each cue is a distinct shape (a tick, a rise, a fall, a chord) rather than
/// just a different pitch, since shape is what survives being heard a hundred times.
///
/// Shared by every client head; the head only decides WHEN. Silenced by one setting.
/// </summary>
public sealed class UiSounds
{
    private readonly AudioEngineFacade _audio;
    public const int SampleRate = 44100;

    /// <summary>Whether interface sounds play at all. Speech is unaffected.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Overall level of interface sounds, 0..1.</summary>
    public float Volume { get; set; } = 0.5f;

    public UiSounds(AudioEngineFacade audio) => _audio = audio;

    public void Play(UiCue cue)
    {
        if (!Enabled) return;
        _audio.PlayUiSound("ui:" + cue, () => Render(cue), SampleRate, Volume);
    }

    /// <summary>The waveform of a cue, peak about 0.8. Public so a test can measure it.</summary>
    public static float[] Render(UiCue cue) => cue switch
    {
        // A soft tick: short and high, gone in a few tens of milliseconds.
        UiCue.MenuMove => Notes(0.35f, (1760f, 0f, 0.03f)),
        // Up a fifth: yes.
        UiCue.MenuSelect => Notes(0.55f, (880f, 0f, 0.07f), (1318.5f, 0.06f, 0.10f)),
        // Down a fifth: back, closed, cancelled.
        UiCue.MenuBack => Notes(0.5f, (1318.5f, 0f, 0.07f), (880f, 0.06f, 0.10f)),
        // A dull low knock: the end of the list.
        UiCue.MenuEdge => Notes(0.5f, (330f, 0f, 0.06f)),
        // You are in: a C major chord and then a G major one above it, bright and warm.
        UiCue.EnterWorld => Notes(0.45f,
            (523.3f, 0.00f, 0.55f), (659.3f, 0.00f, 0.55f), (784.0f, 0.00f, 0.55f),
            (784.0f, 0.22f, 0.9f), (987.8f, 0.22f, 0.9f), (1174.7f, 0.22f, 0.9f)),
        // Someone on your map said something: one soft pop.
        UiCue.ChatMap => Notes(0.45f, (1046.5f, 0f, 0.09f)),
        // Someone said something to everybody: two notes, up.
        UiCue.ChatAll => Notes(0.45f, (784f, 0f, 0.08f), (1046.5f, 0.07f, 0.12f)),
        // Somebody said something to YOU: three notes up, a little longer, unmistakable.
        UiCue.ChatPrivate => Notes(0.5f, (1046.5f, 0f, 0.1f), (1318.5f, 0.08f, 0.1f), (1568f, 0.16f, 0.18f)),
        // The server: two low notes, down — an announcement rather than a person.
        UiCue.ChatServer => Notes(0.5f, (659.3f, 0f, 0.12f), (523.3f, 0.1f, 0.2f)),
        // An admin speaking: a bright rising triad, struck together then held.
        UiCue.ChatAdmin => Notes(0.45f, (880f, 0f, 0.25f), (1108.7f, 0.03f, 0.25f), (1318.5f, 0.06f, 0.3f)),
        // Your microphone is live: one short A.
        UiCue.VoiceOn => Notes(0.5f, (880f, 0f, 0.08f)),
        _ => Notes(0.4f, (1000f, 0f, 0.05f)),
    };

    /// <summary>
    /// Sums bell-like notes: (hertz, start seconds, ring seconds). A few milliseconds of attack so no
    /// note clicks, a fundamental with a quiet octave over it, and an exponential decay over its ring.
    /// </summary>
    private static float[] Notes(float gain, params (float Hz, float At, float Ring)[] notes)
    {
        float end = 0f;
        foreach (var n in notes) end = MathF.Max(end, n.At + n.Ring);
        var buf = new float[(int)((end + 0.02f) * SampleRate)];
        foreach (var n in notes)
        {
            int start = (int)(n.At * SampleRate), len = (int)(n.Ring * SampleRate);
            for (int i = 0; i < len && start + i < buf.Length; i++)
            {
                float t = i / (float)SampleRate;
                float attack = MathF.Min(1f, t / 0.004f);
                float decay = MathF.Exp(-t / (n.Ring * 0.3f));
                float release = MathF.Min(1f, (len - i) / (0.01f * SampleRate));
                float w = MathF.Sin(MathF.Tau * n.Hz * t) + 0.25f * MathF.Sin(MathF.Tau * 2f * n.Hz * t);
                buf[start + i] += w * attack * decay * release;
            }
        }
        float peak = 1e-6f;
        foreach (float v in buf) peak = MathF.Max(peak, MathF.Abs(v));
        float k = 0.8f * gain / peak * 1.6f;
        for (int i = 0; i < buf.Length; i++) buf[i] = Math.Clamp(buf[i] * k, -0.95f, 0.95f);
        return buf;
    }
}
