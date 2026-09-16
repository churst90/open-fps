using System;

namespace OpenFPS.Common;

/// <summary>What a body just did with its lungs, if anything.</summary>
public readonly record struct Breath(bool Taken, bool IsInhale, float LevelDb, float Hz, float DecaySeconds);

/// <summary>
/// How hard a body is working, and the sound that makes.
///
/// A body that has been running is audible after it stops, and that is information rather than
/// decoration: it says somebody came this way at speed, it says which of two people is the one who
/// has been chasing you, and it is the only thing a body still makes once it has stopped moving and
/// gone quiet. A body standing still in a room with you is otherwise completely silent.
///
/// Nothing here needs a recording. A breath is turbulent air through a narrow aperture, which is a
/// HISS — one of the four characters the transient synthesiser already has — and the three numbers
/// that separate a gasp from a sigh are its level, its brightness and how long it lasts. What makes
/// it sound like effort is not the timbre of any one breath but the RATE and the depth, and both of
/// those come out of the model below rather than being chosen.
///
/// Exertion is an integrator with two time constants, because getting out of breath and getting your
/// breath back are not the same process at the same speed: a hard run has you breathing heavily
/// within half a minute and you are still recovering a minute after you stop. That asymmetry is most
/// of why a winded body is a useful signal — it outlasts the running by long enough to find.
/// </summary>
public sealed class Breathing
{
    /// <summary>Seconds for exertion to close most of the gap UP toward what the body is demanding.</summary>
    public const float OnsetSeconds = 15f;

    /// <summary>Seconds for it to fall back down at rest. Longer than the onset: recovery is slower
    /// than exhaustion, which is what makes a body that has been running findable after it stops.</summary>
    public const float RecoverySeconds = 35f;

    /// <summary>Breaths per second at complete rest — about fifteen a minute.</summary>
    public const float RestingRateHz = 0.25f;

    /// <summary>Breaths per second flat out — about fifty a minute.</summary>
    public const float MaxRateHz = 0.83f;

    /// <summary>Peak level of an exhale at one metre with the body at rest, dB SPL. Inaudible across
    /// a room, which is correct: a resting body's breathing is not a cue, it is a fact about it.</summary>
    public const float RestingLevelDb = 22f;

    /// <summary>Peak level at full exertion, dB SPL. Loud enough to place someone in a corridor.</summary>
    public const float MaxLevelDb = 56f;

    /// <summary>Below this a breath is not worth a voice: nothing can hear it over anything.</summary>
    public const float AudibleFloorDb = 30f;

    private float _exertion;
    private float _phase;

    /// <summary>How hard the body is working, 0 at rest and 1 flat out. Lags the effort in both
    /// directions, which is the point of it.</summary>
    public float Exertion => _exertion;

    /// <summary>Forgets the body's state, for a spawn or a teleport — a body that arrives somewhere
    /// has not just run there.</summary>
    public void Forget()
    {
        _exertion = 0f;
        _phase = 0f;
    }

    /// <summary>
    /// Advances one step. <paramref name="speed"/> is the body's own horizontal speed in m/s;
    /// <paramref name="topSpeed"/> is what the body can do flat out, so that "working hard" means the
    /// same thing for anything that moves under its own power rather than being a number about players.
    /// </summary>
    public bool Update(float speed, float dt, out Breath breath, float topSpeed = PhysicsConstants.SprintSpeed)
    {
        breath = default;
        if (dt <= 0f) return false;

        // What the body is asking of itself, 0 to 1. Effort goes with speed; standing still asks
        // nothing. Clamped rather than extrapolated, because a body carried faster than it can run is
        // not working harder — it is not working at all.
        float demand = topSpeed > 0.01f ? Math.Clamp(speed / topSpeed, 0f, 1f) : 0f;

        float tau = demand > _exertion ? OnsetSeconds : RecoverySeconds;
        _exertion += (demand - _exertion) * (1f - MathF.Exp(-dt / tau));
        _exertion = Math.Clamp(_exertion, 0f, 1f);

        float rate = RestingRateHz + (MaxRateHz - RestingRateHz) * _exertion;
        float was = _phase;
        _phase += rate * dt;

        // One cycle is an inhale and, a little under half a cycle later, an exhale. Which of the two
        // is louder moves with effort: at rest the exhale is the audible half and the inhale is
        // nothing, and a winded body is the other way round — the gasp going IN is the loud part.
        bool inhale = _phase >= 1f;
        bool exhale = was < 0.45f && _phase >= 0.45f;
        if (_phase >= 1f) _phase -= 1f;
        if (!inhale && !exhale) return false;

        // An inhale is drawn through a narrower opening than an exhale is pushed through, so it is
        // both brighter and shorter. Everything else about a breath is how hard the body is working.
        float level = RestingLevelDb + (MaxLevelDb - RestingLevelDb) * _exertion;
        if (inhale) level -= 8f * (1f - _exertion);   // quiet at rest, equal to the exhale flat out
        else        level -= 2f * _exertion;

        if (level < AudibleFloorDb) return false;

        breath = new Breath(
            Taken: true,
            IsInhale: inhale,
            LevelDb: level,
            Hz: inhale ? 900f - 150f * _exertion : 550f - 100f * _exertion,
            DecaySeconds: (inhale ? 0.28f : 0.38f) * (1f - 0.45f * _exertion));
        return true;
    }
}
