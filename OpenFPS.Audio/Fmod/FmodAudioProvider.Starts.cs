using FMOD;
using Serilog;

namespace OpenFPS.Audio.Fmod;

/// <summary>
/// The check that every recording leaves its first sample. Resonance's copy of the library found 58 of about
/// 58,000 recordings that never did: past their delayed start, not paused, not virtual, the channel's units run
/// block after block while its play position stays where it began, and a footstep is cut off. This changes no
/// sound: each recording is looked at once, a margin after its start, on the update pass that already walks
/// every voice, and one that has not moved is counted and named.
/// </summary>
public partial class FmodAudioProvider
{
    /// <summary>How far past its start, seconds of the parent's DSP clock, a recording must be before it is
    /// judged: five mixer blocks of 1024 at 48 kHz, so a start a block late is not called stuck.</summary>
    internal const double StartMarginSeconds = 0.1;

    /// <summary>How long a recording held paused or virtual past its start is watched before it is let be,
    /// seconds of DSP clock: either is a reason of its own not to move.</summary>
    internal const double StartGiveUpSeconds = 2.0;

    /// <summary>One recording's start, watched until it is judged (<see cref="WatchStart"/>).</summary>
    internal struct StartWatch
    {
        public bool Watching;
        /// <summary>When it begins on the parent's DSP clock, its delay included.</summary>
        public ulong Clock;
        /// <summary>Its play position when it was started: a reflection or a joined program begins mid-file.</summary>
        public uint Pcm;
        /// <summary>Stopwatch ticks before which no FMOD call is made for it: its delay and the margin in real time.</summary>
        public long CheckAfter;
        public float DelayMs, Pitch;
        public bool Fades;
    }

    /// <summary>What one look at a watched start finds.</summary>
    internal enum StartVerdict { Wait, Started, Stuck, LetBe }

    private int _startsWatched, _startsChecked, _neverStarted;

    /// <summary>Recordings started and watched, judged by their play position (the rest ended first), and never
    /// started, since the mixer was made.</summary>
    public (int Watched, int Checked, int NeverStarted) StartCheck
        => (Volatile.Read(ref _startsWatched), Volatile.Read(ref _startsChecked), Volatile.Read(ref _neverStarted));

    /// <summary>The judgement on a start, from the parent's clock and the play position: wait until the margin is
    /// past; moved, started; not moved, stuck unless paused or virtual, which are waited out and then let be.</summary>
    internal static StartVerdict Judge(ulong start, ulong parentNow, uint startPcm, uint pcmNow, bool paused, bool isVirtual,
                                       ulong marginFrames, ulong giveUpFrames)
    {
        if (parentNow < start + marginFrames) return StartVerdict.Wait;
        if (pcmNow != startPcm) return StartVerdict.Started;
        if (!paused && !isVirtual) return StartVerdict.Stuck;
        return parentNow >= start + giveUpFrames ? StartVerdict.LetBe : StartVerdict.Wait;
    }

    /// <summary>Begins watching a recording just started, before it is unpaused: one position read.</summary>
    private void WatchFrom(ActiveSound active, ulong startClock, float delayMs, float pitch, bool fades)
    {
        if (active.Channel.getPosition(out uint pcm, TIMEUNIT.PCM) != RESULT.OK) return;
        double wait = Math.Max(0f, delayMs) / 1000.0 + StartMarginSeconds;
        active.Start = new StartWatch
        {
            Watching = true, Clock = startClock, Pcm = pcm, DelayMs = delayMs, Pitch = pitch, Fades = fades,
            CheckAfter = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(wait * System.Diagnostics.Stopwatch.Frequency),
        };
        _startsWatched++;
    }

    /// <summary>
    /// Looks at a watched start once its margin is past in real time: a clock read and a position read, once for
    /// most recordings; nothing at all for the rest of the voices. A recording still where it began is counted and
    /// logged once, with what it was started with and what FMOD says of it now.
    /// </summary>
    private void WatchStart(ActiveSound active, long nowTicks)
    {
        ref var w = ref active.Start;
        if (!w.Watching || nowTicks < w.CheckAfter) return;
        if (active.Channel.getDSPClock(out ulong own, out ulong parent) != RESULT.OK
            || active.Channel.getPosition(out uint pcm, TIMEUNIT.PCM) != RESULT.OK)
        {
            w.Watching = false;
            return;
        }
        bool paused = false, isVirtual = false;
        if (pcm == w.Pcm)
        {
            active.Channel.getPaused(out paused);
            active.Channel.isVirtual(out isVirtual);
        }
        ulong margin = (ulong)(MixerRate * StartMarginSeconds), giveUp = (ulong)(MixerRate * StartGiveUpSeconds);
        switch (Judge(w.Clock, parent, w.Pcm, pcm, paused, isVirtual, margin, giveUp))
        {
            case StartVerdict.Wait:
                return;
            case StartVerdict.Started:
                _startsChecked++;
                break;
            case StartVerdict.Stuck:
                _startsChecked++;
                _neverStarted++;
                LogNeverStarted(active, own, parent, pcm);
                break;
        }
        w.Watching = false;
    }

    private void LogNeverStarted(ActiveSound active, ulong own, ulong parent, uint pcm)
    {
        ref var w = ref active.Start;
        active.Channel.getPitch(out float pitchNow);
        active.Channel.getDelay(out ulong delayStart, out ulong delayEnd, out _);
        uint points = 0;
        active.Channel.getFadePoints(ref points, null!, null!);
        active.Channel.getNumDSPs(out int dsps);
        active.Channel.getAudibility(out float audibility);
        double lateMs = (parent - w.Clock) * 1000.0 / MixerRate;
        Log.Warning("[NEVER-STARTED] {Sound} (voice {Id}): still on sample {Pcm} {Late:F0} ms after its start; delay {Delay:F2} ms, "
                  + "pitch {Pitch:F3} (now {PitchNow:F3}), fade points {Fades} ({Points} now); start {Start}, parent clock {Parent}, "
                  + "own clock {Own}, delay set {DelayStart}-{DelayEnd}, {Dsps} units, audibility {Audibility:F3}; {Count} so far.",
                    active.SoundId, active.EntityId, pcm, lateMs, w.DelayMs, w.Pitch, pitchNow, w.Fades ? "yes" : "no", points,
                    w.Clock, parent, own, delayStart, delayEnd, dsps, audibility, _neverStarted);
    }

    /// <summary>The whole run's count, said once as the mixer closes.</summary>
    private void ReportStartCheck()
    {
        var (watched, judged, stuck) = StartCheck;
        if (watched == 0) return;
        Log.Information("Recordings started {Watched}, judged by their play position {Checked}, never started {Stuck} ({PerThousand:F2} per thousand).",
                        watched, judged, stuck, stuck * 1000.0 / watched);
    }
}
