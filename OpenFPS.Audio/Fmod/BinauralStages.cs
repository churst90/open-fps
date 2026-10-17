using ActiveSound = OpenFPS.Audio.Fmod.FmodAudioProvider.ActiveSound;

namespace OpenFPS.Audio.Fmod;

/// <summary>
/// Who gets one of the binaural stages (Steam Audio's HRTF on a voice), by the one rank: how loud a voice is at the ear
/// (<see cref="FmodAudioProvider.HeardHere"/>, VoiceManager.HeardLevel), never who asked first. docs/CLIENT_NOTES.md,
/// "The binaural pool".
///
/// <para>Measured on the city's Main Street in traffic and rain (AudioLab --busy-street, Cody's log of 2026-10-17): first
/// come, first served, the pool sat at 0 to 20 free of 160 with 25 to 70 stages held by other people's footsteps
/// 140 to 180 m off and down to 55 dB under the loudest voice playing, while a horn 9 dB under it, a raindrop at your
/// feet, birds and nearer steps were refused and never played; the budgets, told the pool was short, gave up machines,
/// places and reflections, whose stages the steps then took.</para>
///
/// <para>The rule: a one-shot more than <see cref="UnheardUnderDb"/> under the loudest voice playing is not heard
/// against it and is not started. The rest take a stage while the pool has one above the reverb heads' reserve; once
/// the pool is down to its headroom, only a voice louder than the quietest holding a stage (by
/// <see cref="Hysteresis"/>) takes one, and the quietest are let go, faded out and stopped, until the headroom is back.
/// A stage stays with its voice for that voice's life: a voice is placed binaurally or not played, never handed between
/// HRTF and FMOD's panner (a voice without a stage is flat, in the middle of the head, and the mode change clicks).
/// Your own steps and speech (Essential) and a reflection without a stage still play, panned, as before.</para>
/// </summary>
internal static class BinauralStages
{
    /// <summary>Stages only a reverb bus's head takes: one for each bus heard at once (FmodAudioProvider's
    /// MaxActiveReverbBuses). A head is held only while its bus is heard.</summary>
    public const int HeadReserve = FmodAudioProvider.MaxActiveReverbBuses;

    /// <summary>Stages the pool keeps free for voices that start between two updates, by letting its quietest go. A
    /// stage let go comes back a fade and four mixer blocks later (about 170 ms); in that time the street's walkers and
    /// the rain start 20 to 40 one-shots, most of them under the floor.</summary>
    public const int ArrivalHeadroom = 20;

    /// <summary>What a reflection leaves in the pool: a copy without a stage is placed by FMOD and is not heard as wrong,
    /// so it never takes the headroom a direct sound arriving needs.</summary>
    public const int ReflectionLeaves = HeadReserve + ArrivalHeadroom;

    /// <summary>How far under the loudest voice playing a one-shot is not heard at all: the far edge of masking, where a
    /// step 150 m off sits beside a car passing at 10 m (55 dB under it on Main Street). The traced echoes stop at 30 dB
    /// under (FmodAudioProvider.EchoWithinDb); a voice's own sound is given ten more.</summary>
    public const float UnheardUnderDb = 40f;

    /// <summary>How much louder than the quietest voice holding a stage a voice must be to take one while the pool is
    /// short: about two decibels, the budget's own (VoiceManager.PlayingHysteresis), so two near-equal voices do not
    /// trade the last stage.</summary>
    public const float Hysteresis = 1.26f;

    /// <summary>The answer to a voice asking for a stage.</summary>
    public enum Answer : byte
    {
        /// <summary>A stage is its for its life.</summary>
        Given,
        /// <summary>Plays without one, panned by FMOD: your own steps and speech, or a reflection.</summary>
        Panned,
        /// <summary>A one-shot not heard against the loudest voice playing: not started.</summary>
        Unheard,
        /// <summary>The pool is down to its headroom and the voice is no louder than the quietest holding a stage: not
        /// started. The rank working, not a shortage.</summary>
        Outranked,
        /// <summary>Louder than a voice holding a stage, and none left above the heads' reserve: a shortage, which the
        /// headroom is there to prevent. Not started.</summary>
        Short,
    }

    /// <summary>
    /// Whether a voice that would sound <paramref name="heard"/> at the ear gets a stage, with <paramref name="free"/>
    /// stages free, the loudest voice playing at <paramref name="loudest"/> and the quietest holding a stage at
    /// <paramref name="floor"/> (0 when the pool is not short). Levels are linear, as HeardHere gives them.
    /// </summary>
    public static Answer Ask(int free, bool essential, bool reflection, bool oneShot, float heard, float loudest, float floor)
    {
        if (essential) return free > 0 ? Answer.Given : Answer.Panned;
        if (reflection) return free > ReflectionLeaves ? Answer.Given : Answer.Panned;
        if (oneShot && heard < loudest * UnheardGain) return Answer.Unheard;
        bool outranks = heard >= floor * Hysteresis;
        if (free <= HeadReserve) return outranks ? Answer.Short : Answer.Outranked;
        return outranks ? Answer.Given : Answer.Outranked;
    }

    /// <summary>The level ratio <see cref="UnheardUnderDb"/> is.</summary>
    public static readonly float UnheardGain = MathF.Pow(10f, -UnheardUnderDb / 20f);

    /// <summary>
    /// The level a voice must outrank to take a stage: the quietest voice holding one (that is not on its way out),
    /// once the pool's free stages are down to twice its headroom; nothing before. Twice, so the floor rises before the
    /// pool lets anything go, and a voice let go does not come straight back.
    /// </summary>
    public static float Floor(List<ActiveSound> sounds, int free)
    {
        if (free > HeadReserve + 2 * ArrivalHeadroom) return 0f;
        float floor = float.MaxValue;
        foreach (var a in sounds)
            if (MayLetGo(a)) floor = MathF.Min(floor, a.Heard);
        return floor == float.MaxValue ? 0f : floor;
    }

    /// <summary>Whether a voice holds a stage the rank may take back: a direct sound that is not your own, not already
    /// on its way out.</summary>
    public static bool MayLetGo(ActiveSound a) => a.SaState != null && !a.IsReflection && !a.Essential && !a.Leaving;

    /// <summary>
    /// The voices to let go so the pool keeps its headroom, quietest first (ties by id), into <paramref name="into"/>:
    /// as many as the free stages, with those already on their way out counted as free, fall short of the heads'
    /// reserve and the headroom.
    /// </summary>
    public static void ChooseLeavers(List<ActiveSound> sounds, int free, List<ActiveSound> into)
    {
        into.Clear();
        int leaving = 0;
        foreach (var a in sounds) if (a.SaState != null && a.Leaving) leaving++;
        int want = HeadReserve + ArrivalHeadroom - free - leaving;
        if (want <= 0) return;
        foreach (var a in sounds) if (MayLetGo(a)) into.Add(a);
        into.Sort(static (x, y) => (x.Heard, x.EntityId).CompareTo((y.Heard, y.EntityId)));
        if (into.Count > want) into.RemoveRange(want, into.Count - want);
    }

    /// <summary>The loudest voice playing at the ear, reflections left out (the one rank, <see cref="ActiveSound.Heard"/>).</summary>
    public static float Loudest(List<ActiveSound> sounds)
    {
        float loudest = 0f;
        foreach (var a in sounds) if (!a.IsReflection && !a.Leaving && a.Type != OpenFPS.Audio.Data.EmitterType.UI) loudest = MathF.Max(loudest, a.Heard);
        return loudest;
    }
}
