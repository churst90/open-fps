using System;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>What an interface sound means. Each has its own, so a menu, a chat and the world arriving
/// can be told apart by ear before a word is spoken.</summary>
public enum UiCue
{
    MenuMove, MenuSelect, MenuBack, MenuEdge,
    EnterWorld,
    ChatMap, ChatAll, ChatPrivate, ChatServer, ChatAdmin,
    VoiceOn, VoiceOff,
    Reconnecting,
    PresenceOnline, PresenceLoggedOut, PresenceConnectionLost, PresenceAway, PresenceBack,
    /// <summary>Your team talking: /t, or /team chat.</summary>
    ChatTeam,
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

    /// <summary>
    /// Whether somebody coming, going or going away plays its sound. The words are still filed and
    /// spoken: on a busy server the notices are wanted and the sounds can be too many.
    /// </summary>
    public bool PresenceEnabled { get; set; } = true;

    /// <summary>Overall level of interface sounds, 0..1.</summary>
    public float Volume { get; set; } = 0.5f;

    public UiSounds(AudioEngineFacade audio) => _audio = audio;

    public void Play(UiCue cue)
    {
        if (!Enabled) return;
        _audio.PlayUiSound("ui:" + cue, () => Render(cue), SampleRate, Volume);
    }

    /// <summary>
    /// The sound a line of chat is heard with, or none. The channel decides it, whoever is talking:
    /// an admin's map chat is still map chat (with the admin cue in its place, an admin was heard the
    /// same on every channel). The admin cue is for server announcements by staff. A reply to a
    /// command (no sender) is only spoken; a chat sound on "/tp" said a message came. A presence
    /// notice has its own sound, or none when those are turned off, and never the chat sound
    /// instead: nobody said anything.
    /// </summary>
    public static UiCue? CueFor(ChatMessage msg, bool presenceSounds)
    {
        if (msg.Presence != PresenceKind.None)
        {
            if (!presenceSounds) return null;
            return msg.Presence switch
            {
                PresenceKind.LoggedIn => UiCue.PresenceOnline,
                PresenceKind.LoggedOut => UiCue.PresenceLoggedOut,
                PresenceKind.WentOffline => UiCue.PresenceConnectionLost,
                PresenceKind.Away => UiCue.PresenceAway,
                PresenceKind.Back => UiCue.PresenceBack,
                _ => null,
            };
        }
        if (msg.Channel == ChatChannel.Server && msg.Sender.Length == 0) return null;
        return msg.Channel switch
        {
            ChatChannel.Private => UiCue.ChatPrivate,
            ChatChannel.Team => UiCue.ChatTeam,
            ChatChannel.All => UiCue.ChatAll,
            ChatChannel.Server => msg.FromStaff ? UiCue.ChatAdmin : UiCue.ChatServer,
            _ => UiCue.ChatMap,
        };
    }

    /// <summary>Plays the sound for a line of chat, if it has one.</summary>
    public void PlayChat(ChatMessage msg)
    {
        if (CueFor(msg, PresenceEnabled) is { } cue) Play(cue);
    }

    /// <summary>How many steps the loading tone has between empty and full.</summary>
    public const int ProgressSteps = 10;

    /// <summary>
    /// The loading tone: one soft note per step of progress, rising an octave from start to finish,
    /// so a load can be followed without a word being spoken.
    /// </summary>
    public void PlayProgress(int percent)
    {
        if (!Enabled) return;
        int step = Math.Clamp(percent * ProgressSteps / 100, 0, ProgressSteps);
        _audio.PlayUiSound("ui:progress:" + step, () => RenderProgress(step), SampleRate, Volume);
    }

    /// <summary>The loading tone at a step, 0..<see cref="ProgressSteps"/>. Public so a test can measure it.</summary>
    public static float[] RenderProgress(int step)
        => Notes(SampleRate, 0.3f, (440f * MathF.Pow(2f, Math.Clamp(step, 0, ProgressSteps) / (float)ProgressSteps), 0f, 0.09f));

    /// <summary>The waveform of a cue, peak about 0.8. Public so a test can measure it.</summary>
    public static float[] Render(UiCue cue) => Render(cue, SampleRate);

    /// <summary>A cue at any sample rate, for writing it out to be listened to.</summary>
    public static float[] Render(UiCue cue, int rate) => cue switch
    {
        // A soft tick: short and high, gone in a few tens of milliseconds.
        UiCue.MenuMove => Notes(rate, 0.35f, (1760f, 0f, 0.03f)),
        // Up a fifth: yes.
        UiCue.MenuSelect => Notes(rate, 0.55f, (880f, 0f, 0.07f), (1318.5f, 0.06f, 0.10f)),
        // Down a fifth: back, closed, cancelled.
        UiCue.MenuBack => Notes(rate, 0.5f, (1318.5f, 0f, 0.07f), (880f, 0.06f, 0.10f)),
        // A dull low knock: the end of the list.
        UiCue.MenuEdge => Notes(rate, 0.5f, (330f, 0f, 0.06f)),
        // You are in: a C major chord and then a G major one above it, bright and warm.
        UiCue.EnterWorld => Notes(rate, 0.45f,
            (523.3f, 0.00f, 0.55f), (659.3f, 0.00f, 0.55f), (784.0f, 0.00f, 0.55f),
            (784.0f, 0.22f, 0.9f), (987.8f, 0.22f, 0.9f), (1174.7f, 0.22f, 0.9f)),
        // Someone on your map said something: one soft pop.
        UiCue.ChatMap => Notes(rate, 0.45f, (1046.5f, 0f, 0.09f)),
        // Someone said something to everybody: two notes, up.
        UiCue.ChatAll => Notes(rate, 0.45f, (784f, 0f, 0.08f), (1046.5f, 0.07f, 0.12f)),
        // Somebody said something to YOU: three notes up, a little longer, unmistakable.
        UiCue.ChatPrivate => Notes(rate, 0.5f, (1046.5f, 0f, 0.1f), (1318.5f, 0.08f, 0.1f), (1568f, 0.16f, 0.18f)),
        // The server: two low notes, down — an announcement rather than a person.
        UiCue.ChatServer => Notes(rate, 0.5f, (659.3f, 0f, 0.12f), (523.3f, 0.1f, 0.2f)),
        // An admin speaking: a bright rising triad, struck together then held.
        UiCue.ChatAdmin => Notes(rate, 0.45f, (880f, 0f, 0.25f), (1108.7f, 0.03f, 0.25f), (1318.5f, 0.06f, 0.3f)),
        // Your microphone is live: one short A.
        UiCue.VoiceOn => Notes(rate, 0.5f, (880f, 0f, 0.08f)),
        // Your microphone is off: the same A, then the E below it.
        UiCue.VoiceOff => Notes(rate, 0.5f, (880f, 0f, 0.06f), (659.3f, 0.07f, 0.08f)),
        // Trying the server again: a quiet low tick, every few seconds until it answers.
        UiCue.Reconnecting => Notes(rate, 0.25f, (587.3f, 0f, 0.04f)),
        // Presence: somebody came, went, or stepped away. The same soft sines as the chat cues and the
        // beacons, in the beacons' register (middle C to the C two octaves up), and longer than a
        // chat line, so they are heard as somebody arriving or leaving rather than somebody talking.
        // Each a shape of its own, and each pair the same figure turned round.
        // Online: C major climbing to the octave, C5 E5 G5 C6, the last note held. Arriving.
        UiCue.PresenceOnline => Notes(rate, 0.45f,
            (523.25f, 0.00f, 0.20f), (659.25f, 0.12f, 0.20f), (783.99f, 0.24f, 0.22f), (1046.5f, 0.36f, 0.55f)),
        // Logged out: the same notes falling, C6 G5 E5 C5, the low C held. Gone, on purpose.
        UiCue.PresenceLoggedOut => Notes(rate, 0.45f,
            (1046.5f, 0.00f, 0.20f), (783.99f, 0.12f, 0.20f), (659.25f, 0.24f, 0.22f), (523.25f, 0.36f, 0.55f)),
        // Connection lost: the fall starts, G5 then E flat (minor, so something is wrong), breaks off,
        // and stutters twice on a short C5 before a low G4 that is cut short. Not on purpose.
        UiCue.PresenceConnectionLost => Notes(rate, 0.45f,
            (783.99f, 0.00f, 0.16f), (622.25f, 0.12f, 0.16f),
            (523.25f, 0.40f, 0.07f), (523.25f, 0.52f, 0.07f), (392.00f, 0.64f, 0.18f)),
        // Away: two slow swells falling a fourth, A4 to E4, like the waypoint beacon's swell. Quiet,
        // because nothing has happened but somebody stepping back from the keys.
        UiCue.PresenceAway => Swells(rate, 0.22f, (440.00f, 0.00f, 0.42f), (329.63f, 0.30f, 0.50f)),
        // Back: the same two swells the other way, E4 up to A4.
        UiCue.PresenceBack => Swells(rate, 0.22f, (329.63f, 0.00f, 0.42f), (440.00f, 0.30f, 0.50f)),
        // Your team said something: the same two notes as a private message's first two, then back
        // down to the first — a call among friends rather than one aimed at you alone.
        UiCue.ChatTeam => Notes(rate, 0.5f, (1046.5f, 0f, 0.09f), (1318.5f, 0.08f, 0.09f), (1046.5f, 0.16f, 0.16f)),
        _ => Notes(rate, 0.4f, (1000f, 0f, 0.05f)),
    };

    /// <summary>
    /// Sums bell-like notes: (hertz, start seconds, ring seconds). A few milliseconds of attack so no
    /// note clicks, a fundamental with a quiet octave over it, and an exponential decay over its ring.
    /// </summary>
    private static float[] Notes(int rate, float gain, params (float Hz, float At, float Ring)[] notes)
    {
        float end = 0f;
        foreach (var n in notes) end = MathF.Max(end, n.At + n.Ring);
        var buf = new float[(int)((end + 0.02f) * rate)];
        foreach (var n in notes)
        {
            int start = (int)(n.At * rate), len = (int)(n.Ring * rate);
            for (int i = 0; i < len && start + i < buf.Length; i++)
            {
                float t = i / (float)rate;
                float attack = MathF.Min(1f, t / 0.004f);
                float decay = MathF.Exp(-t / (n.Ring * 0.3f));
                float release = MathF.Min(1f, (len - i) / (0.01f * rate));
                float w = MathF.Sin(MathF.Tau * n.Hz * t) + 0.25f * MathF.Sin(MathF.Tau * 2f * n.Hz * t);
                buf[start + i] += w * attack * decay * release;
            }
        }
        return Scale(buf, gain);
    }

    /// <summary>
    /// Notes that swell in and out rather than strike: (hertz, start seconds, length seconds), each a
    /// raised cosine with the beacon swell's slow, slight vibrato. For cues that should arrive gently.
    /// </summary>
    private static float[] Swells(int rate, float gain, params (float Hz, float At, float Seconds)[] notes)
    {
        float end = 0f;
        foreach (var n in notes) end = MathF.Max(end, n.At + n.Seconds);
        var buf = new float[(int)((end + 0.02f) * rate)];
        foreach (var n in notes)
        {
            int start = (int)(n.At * rate), len = (int)(n.Seconds * rate);
            double ph = 0;
            for (int i = 0; i < len && start + i < buf.Length; i++)
            {
                float t = i / (float)rate;
                float env = 0.5f - 0.5f * MathF.Cos(MathF.Tau * t / n.Seconds);
                ph += MathF.Tau * n.Hz * (1f + 0.004f * MathF.Sin(MathF.Tau * 5f * t)) / rate;
                buf[start + i] += env * (float)(Math.Sin(ph) + 0.25 * Math.Sin(2 * ph));
            }
        }
        return Scale(buf, gain);
    }

    private static float[] Scale(float[] buf, float gain)
    {
        float peak = 1e-6f;
        foreach (float v in buf) peak = MathF.Max(peak, MathF.Abs(v));
        float k = 0.8f * gain / peak * 1.6f;
        for (int i = 0; i < buf.Length; i++) buf[i] = Math.Clamp(buf[i] * k, -0.95f, 0.95f);
        return buf;
    }
}
