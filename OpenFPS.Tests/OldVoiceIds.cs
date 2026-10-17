using OpenFPS.Client.Core;
using OpenFPS.Audio.Core;

namespace OpenFPS.Tests;

/// <summary>
/// The id each voice had before the registry gave ids out (docs/SPECIAL_CASES_AUDIT.md, P3c): bands, each a
/// formula of an entity id or a count. They were the voices' seeds until the reseed (P3d, step 6), and are kept
/// here only as names: the stream replay writes a voice by its old id to compare a stream with one from before
/// a change to ids or seeds (Replay.RunUnderOldIds).
/// </summary>
internal static class OldVoiceIds
{
    /// <summary>How many one-offs of a kind took turns at a voice before each was a voice of its own: the next
    /// after that many had the first's id again.</summary>
    public static int TurnsOf(VoiceKind kind, int owner) => kind switch
    {
        VoiceKind.Transient => 400_000,
        VoiceKind.Beacon => 24,
        VoiceKind.Bird or VoiceKind.StepEcho or VoiceKind.RainDrop => 48,
        VoiceKind.OwnStep => 12,
        VoiceKind.Footstep => 64,
        VoiceKind.Aid => (AidVoice)owner is AidVoice.Guide or AidVoice.Centre or AidVoice.Kerb or AidVoice.Turn or AidVoice.Brake ? 6 : 0,
        _ => 0,
    };

    /// <summary>The id <paramref name="kind"/> of <paramref name="owner"/>, the <paramref name="part"/>th of
    /// them, had. A landing's part, below zero, took no turn.</summary>
    public static int Of(VoiceKind kind, int owner, int part = 0)
    {
        if (TurnsOf(kind, owner) is int turns and > 0 && part >= 0) part %= turns;
        return kind switch
        {
            VoiceKind.Engine or VoiceKind.Aircraft or VoiceKind.Machine or VoiceKind.Wood or VoiceKind.Recording => owner,
            VoiceKind.Reflection => -30_000 - owner * (OpenFPS.Acoustics.EarlyReflections.MaxArrivals + 1) - part,
            VoiceKind.Transient => -100_000 - part,
            VoiceKind.EngineEcho => -600_000 - part,
            VoiceKind.Borrowed => -700_000 - Math.Abs(owner),
            VoiceKind.Intake => -800_000 - Math.Abs(owner),
            VoiceKind.Siren => -900_000 - Math.Abs(owner),
            VoiceKind.Aid => OfAid((AidVoice)owner, part),
            VoiceKind.Beacon => -967_000 - part,
            VoiceKind.Bird => -970_000 - part,
            VoiceKind.Horn => -1_200_000 - Math.Abs(owner),
            VoiceKind.OwnVoice => -1_300_000,
            VoiceKind.OwnVoiceCopy => -1_300_001 - part,
            VoiceKind.Talker => -1_400_000 - Math.Abs(owner),
            VoiceKind.Cabin => -1_600_000 - part,
            VoiceKind.TalkerCopy => -1_700_000 - owner * 8 - part,
            VoiceKind.StepEcho => -1_800_000 - part,
            VoiceKind.HeldHorn => -2_100_000 - Math.Abs(owner),
            VoiceKind.TrainField or VoiceKind.TrainSignal => -2_300_000 - (owner * OpenFPS.Sound.Core.Rail.TrainVoicing.Slots + part),
            VoiceKind.Rain => part == 0 ? -2_400_000 - owner : -2_400_100 - owner * 8 - part,
            VoiceKind.RainDrop => -2_401_000 - part,
            VoiceKind.Place => -5_000_000 - Math.Abs(owner) * OpenFPS.Sound.Core.Nature.ExtendedSources.MaxPlaces - part,
            VoiceKind.Reference => -7_900_000 - part,
            VoiceKind.Probe => -3_000_000 - owner,
            VoiceKind.OwnStep => part == VoiceRegistry.Landing ? -50 : -100 - part,
            VoiceKind.Footstep => part == VoiceRegistry.Landing ? -50 : -300 - part,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no old id"),
        };
    }

    private static int OfAid(AidVoice aid, int part) => aid switch
    {
        AidVoice.Guide => -961_000 - part,
        AidVoice.Centre => -962_000 - part,
        AidVoice.Kerb => -963_000 - part,
        AidVoice.CentreTone => -964_001,
        AidVoice.KerbTone => -964_002,
        AidVoice.CentreRumble => -964_003,
        AidVoice.KerbRumble => -964_004,
        AidVoice.Turn => -965_000 - part,
        AidVoice.Align => -966_001,
        AidVoice.Relay => -966_010,
        AidVoice.OverSpeed => -966_011,
        AidVoice.Brake => -967_100 - part,
        _ => throw new ArgumentOutOfRangeException(nameof(aid), aid, "no old id"),
    };
}
