using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Stove;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// A gas hob as a voice (GasHobSynth, docs/GAS_HOB.md). Its state is its key (HobKey): what each knob was,
/// what it was turned to, and when on the shared clock. A key that changes while the voice lives is the
/// next turn of a knob, handed to the hob's hand; a voice made late runs what it missed silently, so every
/// client hears the same hob. Rendered with the sparks' own headroom over the flames, as a fire's crackle.
/// </summary>
public sealed class StoveVoiceState : PhysicalVoiceState
{
    public readonly GasHobSynth Hob;
    private volatile string _key;
    private string _applied;
    private string _settings;

    /// <summary>Under this a change is taken as heard as it happened: the network's delay, not a late
    /// arrival. Two clients a fraction of a second apart then render the same light-up.</summary>
    public const double LateSeconds = 1.0;

    public StoveVoiceState(GasHobSpec spec, string key, float sampleRate, int seed)
        : base(spec.SourceLevelDb, sampleRate, spec.PeakHeadroomDb)
    {
        Hob = new GasHobSynth(spec, sampleRate, seed);
        _key = _applied = key;
        string off = HobKey.Off(spec.Burners.Length);
        if (HobKey.TryParse(key, out var k) && k.To.Length > 0)
        {
            double age = WindField.Now() - k.At;
            Hob.Begin(k.From, k.To, age < LateSeconds ? 0f : (float)Math.Min(age, 120.0));
            _settings = k.To;
        }
        else
        {
            Hob.Begin(off, off, 0f);
            _settings = off;
        }
        LabCreated?.Invoke(Hob);
    }

    /// <summary>For the lab: called with each hob voice's synth as it is made, to give its hand a script no
    /// server sends (a slow light). Nothing in the game sets it.</summary>
    internal static Action<GasHobSynth>? LabCreated;

    /// <summary>The emitter's key now. Game thread; the producer takes it up at its next block.</summary>
    public void SetKey(string key)
    {
        if (!ReferenceEquals(key, _key)) _key = key;
    }

    protected override void Control(float seconds, float dt)
    {
        string key = _key;
        if (ReferenceEquals(key, _applied)) return;
        string was = _applied;
        _applied = key;
        if (key == was || !HobKey.TryParse(key, out var k) || k.To.Length == 0) return;
        // From what this voice last had, not what the key says it was: a change missed is still made.
        Hob.Change(_settings, k.To);
        _settings = k.To;
    }

    protected override void PushListener(Vector3 frame) => Hob.SetListener(frame);

    protected override float StepSynth() => Hob.Next();
}
