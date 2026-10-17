using System.Globalization;
using System.Threading;

namespace OpenFPS.Sound.Core;

/// <summary>
/// One mixer's live levers, which the player's console turns to judge the mix by ear: the reflections' two
/// trims (`/tail`, `/copies`, `/reflections`), the traced echoes (`/echoes`), the cabin's response (`/cabin`),
/// far engines at reduced detail (`/enginedetail`) and the exhaust valves' rush (`/valveflow`). Every voice of
/// that mixer reads this one, so a lever turned in one world never reaches another (a test beside a session,
/// a lab comparing two settings).
///
/// Who holds it: the mixer (FmodAudioProvider.Mix), and the host reaches the same one through
/// AudioEngineFacade.Mix. A new one is the shipped mix; <see cref="FromEnvironment"/> is that, unless a run's
/// environment says otherwise.
/// </summary>
public sealed class MixSettings
{
    /// <summary>The far sources' traced echoes on (`/echoes`), at <see cref="TailDb"/>.</summary>
    public bool TracedEchoesOn
    {
        get => Volatile.Read(ref _echoes) != 0;
        set => Volatile.Write(ref _echoes, value ? 1 : 0);
    }
    private int _echoes = 1;

    /// <summary>
    /// Every traced reflection against the direct sound, dB: the rooms' and streets' traced tails and
    /// the far sources' traced echoes (`/tail`, OPENFPS_TAIL_DB; `/reflections` sets this and
    /// <see cref="CopiesDb"/>). Zero is physical to within a couple of decibels where measured
    /// (--clap-room, --traced-reverb). -6 for both, set by ear in a flat, a tunnel and a street; the
    /// trace rings as long at 4 kHz as at 250 Hz (0.79 s, Sabine 0.52), so the top hangs on. See
    /// docs/CLIENT_NOTES.md, "The reflections trim".
    /// </summary>
    public float TailDb { get => Volatile.Read(ref _tailDb); set => Volatile.Write(ref _tailDb, value); }
    private float _tailDb = -6f;

    /// <summary>Every reflection placed as a copy of the source against the direct sound, dB: early,
    /// facade and higher-order echoes, your own steps' echoes, the boundary copies (`/copies`,
    /// OPENFPS_COPIES_DB). The copies carry only the mirror share, with the scattered share as the
    /// wall's wash, and at most four second-order copies. See <see cref="TailDb"/>.</summary>
    public float CopiesDb { get => Volatile.Read(ref _copiesDb); set => Volatile.Write(ref _copiesDb, value); }
    private float _copiesDb = -6f;

    /// <summary>The cabin's traced response against its physical level, dB: 0 is the traced level.
    /// `/cabin` with a level in dB sets it, for judging a ride by ear; the reflections trim does not touch it.</summary>
    public float CabinDb { get => Volatile.Read(ref _cabinDb); set => Volatile.Write(ref _cabinDb, value); }
    private float _cabinDb;

    /// <summary>Whether far engines run at reduced detail: OPENFPS_ENGINE_DETAIL=0 and /enginedetail off
    /// keep every engine full, for an A/B. Read by the game thread each update.</summary>
    public bool ReducedFarEngines
    {
        get => Volatile.Read(ref _reduced) != 0;
        set => Volatile.Write(ref _reduced, value ? 1 : 0);
    }
    private int _reduced = 1;

    /// <summary>The exhaust valves' rush (EngineSynth.BlowdownJet): `/valveflow` in game and the lab's A/B.
    /// Read by every engine of the mixer's world as it renders, on the render pool's threads.</summary>
    public bool ValveJetNoise
    {
        get => Volatile.Read(ref _valveJet) != 0;
        set => Volatile.Write(ref _valveJet, value ? 1 : 0);
    }
    private int _valveJet = 1;

    /// <summary><see cref="TailDb"/> as a gain.</summary>
    public float TailTrim => MathF.Pow(10f, TailDb / 20f);

    /// <summary><see cref="CopiesDb"/> as a gain.</summary>
    public float CopiesTrim => MathF.Pow(10f, CopiesDb / 20f);

    /// <summary>The shipped mix, unless the run's environment chose any of it: OPENFPS_ECHOES=off,
    /// OPENFPS_TAIL_DB, OPENFPS_COPIES_DB, OPENFPS_ENGINE_DETAIL=0. What the game's mixer starts with.</summary>
    public static MixSettings FromEnvironment()
    {
        var m = new MixSettings
        {
            TracedEchoesOn = !string.Equals(System.Environment.GetEnvironmentVariable("OPENFPS_ECHOES"), "off", StringComparison.OrdinalIgnoreCase),
            ReducedFarEngines = System.Environment.GetEnvironmentVariable("OPENFPS_ENGINE_DETAIL") != "0",
        };
        if (EnvDb("OPENFPS_TAIL_DB") is float tail) m.TailDb = tail;
        if (EnvDb("OPENFPS_COPIES_DB") is float copies) m.CopiesDb = copies;
        return m;
    }

    private static float? EnvDb(string name) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float db)
            ? Math.Clamp(db, -80f, 6f) : null;
}
