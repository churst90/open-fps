using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Yard;

/// <summary>
/// One house's thermostat calling for its air conditioner's compressor (see <see cref="ThermostatSpec"/>).
///
/// A phase going round at the cycle rate the duty sets, the compressor on for the first <c>duty</c> of
/// each turn: so it runs for as much of the time as the weather asks, in cycles as long as a dead band
/// gives, and a change in the weather moves the share and the rate without a jump. The phase it starts
/// at and how far this house's balance point is from the street's are its own (the seed), so a street
/// of them is never in step.
/// </summary>
public sealed class Thermostat
{
    public readonly ThermostatSpec Spec;
    private readonly float _balanceOffset;
    private readonly double _seedPhase;
    private double _phase;
    private bool _started;

    /// <summary>Whether it is calling for the compressor, after the last <see cref="Step"/>.</summary>
    public bool Calling { get; private set; }

    public Thermostat(ThermostatSpec spec, int seed)
    {
        Spec = spec;
        var rng = new Random(seed);
        _seedPhase = rng.NextDouble();
        _balanceOffset = (float)(rng.NextDouble() * 2 - 1) * spec.BalanceSpreadCelsius;
    }

    /// <summary>
    /// Advances <paramref name="dt"/> seconds with the outdoor air at <paramref name="outdoorCelsius"/>.
    /// <paramref name="clockSeconds"/> places the first step in the cycle, so a voice made again when the
    /// listener comes back finds the machine where its cycle has got to, not back at its start.
    /// </summary>
    public bool Step(float dt, float outdoorCelsius, double clockSeconds = 0)
    {
        float duty = Spec.Duty(outdoorCelsius, _balanceOffset);
        double rate = Spec.CyclesPerHour(duty) / 3600.0;
        if (!_started)
        {
            _started = true;
            _phase = _seedPhase + clockSeconds * rate;
            _phase -= Math.Floor(_phase);
        }
        _phase += Math.Max(0f, dt) * rate;
        _phase -= Math.Floor(_phase);
        Calling = duty >= 1f || (duty > 0f && _phase < duty);
        return Calling;
    }
}
