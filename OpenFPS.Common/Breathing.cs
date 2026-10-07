namespace OpenFPS.Common;

/// <summary>What a body just did with its lungs, if anything.</summary>
public readonly record struct Breath(bool Taken, bool IsInhale, float LevelDb, float Hz, float DecaySeconds);

/// <summary>
/// How hard a body is working, and the breathing that makes. A body that has been running is audible
/// after it stops: somebody came this way at speed, or this is the one who was chasing you. A breath is
/// turbulent air through a narrow opening, a hiss whose level, brightness and length separate a gasp
/// from a sigh; effort is heard in the rate and depth, which come out of the model. Exertion rises over
/// <see cref="OnsetSeconds"/> and falls over the longer <see cref="RecoverySeconds"/>, so a winded body
/// outlasts its running by long enough to find.
/// </summary>
public sealed class Breathing
{
    /// <summary>The time constant of exertion rising toward what the body demands, s.</summary>
    public const float OnsetSeconds = 15f;

    /// <summary>The time constant of exertion falling at rest, s: recovery is slower than exhaustion.</summary>
    public const float RecoverySeconds = 35f;

    /// <summary>Breaths per second at complete rest — about fifteen a minute.</summary>
    public const float RestingRateHz = 0.25f;

    /// <summary>Breaths per second flat out — about fifty a minute.</summary>
    public const float MaxRateHz = 0.83f;

    /// <summary>Peak level of an exhale at one metre at rest, dB SPL: inaudible across a room.</summary>
    public const float RestingLevelDb = 22f;

    /// <summary>Peak level at full exertion, dB SPL. Loud enough to place someone in a corridor.</summary>
    public const float MaxLevelDb = 56f;

    /// <summary>Below this a breath is not worth a voice: nothing can hear it over anything.</summary>
    public const float AudibleFloorDb = 30f;

    private float _exertion;
    private float _phase;

    /// <summary>How hard the body is working, 0 at rest and 1 flat out, lagging the effort both ways.</summary>
    public float Exertion => _exertion;

    /// <summary>For a spawn or a teleport: a body that arrives somewhere has not just run there.</summary>
    public void Forget()
    {
        _exertion = 0f;
        _phase = 0f;
    }

    /// <summary>
    /// Advances one step. <paramref name="topSpeed"/> is what this body can do flat out, so "working
    /// hard" means the same for anything that moves under its own power.
    /// </summary>
    public bool Update(float speed, float dt, out Breath breath, float topSpeed = PhysicsConstants.SprintSpeed)
    {
        breath = default;
        if (dt <= 0f) return false;

        // Clamped: a body carried faster than it can run is not working harder.
        float demand = topSpeed > 0.01f ? Math.Clamp(speed / topSpeed, 0f, 1f) : 0f;

        float tau = demand > _exertion ? OnsetSeconds : RecoverySeconds;
        _exertion += (demand - _exertion) * (1f - MathF.Exp(-dt / tau));
        _exertion = Math.Clamp(_exertion, 0f, 1f);

        float rate = RestingRateHz + (MaxRateHz - RestingRateHz) * _exertion;
        float was = _phase;
        _phase += rate * dt;

        // At rest the exhale is the audible half; winded, the gasp going in is the loud part.
        bool inhale = _phase >= 1f;
        bool exhale = was < 0.45f && _phase >= 0.45f;
        if (_phase >= 1f) _phase -= 1f;
        if (!inhale && !exhale) return false;

        // An inhale is drawn through a narrower opening than an exhale, so it is brighter and shorter.
        float level = RestingLevelDb + (MaxLevelDb - RestingLevelDb) * _exertion;
        if (inhale) level -= 8f * (1f - _exertion);   // 8 dB under at rest, 2 dB over the exhale flat out
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
