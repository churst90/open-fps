using System.Numerics;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.AudioEngine.Core.Rail;

/// <summary>
/// A whole train, as a line of sources strung out along the track (docs/TRAINS.md).
///
/// A six-coach train is 170 m long and a fifty-wagon freight nearly a kilometre: a train arrives for
/// a minute. Every bogie is its own source with its own distance, Doppler and arrival time, and the
/// plateau of level (a line source falls three decibels a doubling until you are further than it is
/// long), the clatter sweeping past and the duller far end all fall out of that.
/// <see cref="Sources"/> is the deliverable: each knows how far behind the head it sits and what it
/// emits in pascals at a metre. A bogie's axles share a rail and are one source; two bogies eighteen
/// metres apart are two.
/// </summary>
public sealed class TrainSynth
{
    /// <summary>One emitter somewhere along the train.</summary>
    public sealed class Source
    {
        public required string Label { get; init; }
        /// <summary>Metres behind the front coupler of the leading vehicle.</summary>
        public required float AlongMetres { get; init; }
        /// <summary>Metres above the railhead.</summary>
        public float HeightMetres { get; init; } = 0.5f;
        /// <summary>The size of the thing, metres: what keeps the inverse square from running away
        /// at close range.</summary>
        public float ExtentMetres { get; init; } = 1.5f;
        /// <summary>Pascals at one metre, valid after Step().</summary>
        public float Out { get; internal set; }
        /// <summary>What it is, as TrainLayout names it.</summary>
        public TrainLayout.Kind Kind { get; init; }
        /// <summary>One sample at this train speed (m/s), pascals at a metre. Each source keeps its own
        /// state, so sources can be rendered apart, on different threads (TrainVoiceState).</summary>
        internal Func<float, float> Render = _ => 0f;
        /// <summary>What it does with the notch, every 64 samples: a governor's set speed, a drive's effort.</summary>
        internal Action<float>? Slow;
        /// <summary>A bogie's or a body's recipe: enough to build another like it (TrainSlotState renders
        /// the rolling stock it carries from these, not from this synth).</summary>
        internal BogieRecipe? Bogie;
        internal DrumRecipe? Drum;
    }

    /// <summary>What a bogie is made of, for building another like it.</summary>
    internal sealed record BogieRecipe(WheelsetSpec Wheels, TrackSpec Track, float ReferenceDb, int Axles, float Wheelbase, float Creep);
    /// <summary>What a body drum is made of.</summary>
    internal sealed record DrumRecipe(float Hz, float Db);

    public readonly TrainProfile Profile;
    private readonly float _rate, _dt;
    private readonly List<Source> _sources = new();
    private readonly List<(BogieVoice Voice, float Along)> _bogies = new();
    private readonly TrackResponse _track;

    private ChimeHorn? _horn;
    private SteamWhistle? _whistle;
    private StruckBell? _bell;
    private SteamFrontEnd? _steam;
    private EngineSynth? _diesel;
    private ElectricDrive? _drive;
    private readonly List<ElectricDrive> _drives = new();
    private readonly List<EngineSynth> _diesels = new();
    private float[] _notches = Array.Empty<float>();

    /// <summary>Metres a second. The one input everything else follows.</summary>
    public float Speed { get; set; } = 25f;
    /// <summary>Where the front coupler is along the track, metres.</summary>
    public double HeadMetres { get; private set; }
    /// <summary>Notch 0 to 8 on a diesel-electric; 0..1 of effort on anything else.</summary>
    public float Notch { get; set; } = 6f;
    public bool HornBlowing { get => _horn?.Blowing ?? false; set { if (_horn != null) _horn.Blowing = value; } }
    public bool WhistleBlowing { get => _whistle?.Blowing ?? false; set { if (_whistle != null) _whistle.Blowing = value; } }
    public bool BellRinging { get => _bell?.Ringing ?? false; set { if (_bell != null) _bell.Ringing = value; } }
    /// <summary>Every prime mover in the consist, lead unit first.</summary>
    public IReadOnlyList<EngineSynth> Diesels => _diesels;

    public IReadOnlyList<Source> Sources => _sources;

    public TrainSynth(TrainProfile p, float rate = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate, int seed = 41, double headAt = 0)
    {
        Profile = p; _rate = rate; _dt = 1f / rate;
        HeadMetres = headAt;
        _track = new TrackResponse(p.Track, rate);
        Speed = p.TypicalSpeedMps;

        float along = 0f;
        int unit = 0, s = seed;
        foreach (var (vehicle, count) in p.Consist)
            for (int c = 0; c < count; c++, unit++)
            {
                BuildVehicle(vehicle, along, unit, ref s);
                along += vehicle.LengthMetres;
            }
        Place(headAt);
    }

    private void BuildVehicle(RailVehicleSpec v, float along, int unit, ref int seed)
    {
        // Where the bogies are: symmetric about the middle of the vehicle, BogieCentres apart.
        float mid = along + v.LengthMetres * 0.5f;
        for (int b = 0; b < v.Bogies; b++)
        {
            float at = v.Bogies == 1 ? mid : mid + (b - (v.Bogies - 1) * 0.5f) * v.BogieCentresMetres;
            // On a curve the creep angle is about half the bogie's wheelbase over the radius.
            float creep = Profile.Track.CurveRadiusMetres > 1f
                ? 0.5f * v.BogieWheelbaseMetres / Profile.Track.CurveRadiusMetres
                : 0f;
            // Its own stretch of track. The rail's response to a wheel dies away within a few metres, so
            // two bogies eighteen metres apart do not share one; and a filter with state shared by every
            // bogie was stepped once per bogie per sample: on a fifty-wagon freight the rail's modes ran
            // at a hundred times the rate and folded back as a hiss.
            var voice = new BogieVoice(v.Wheels, Profile.Track, new TrackResponse(Profile.Track, _rate), Profile.RollingReferenceDb,
                                       v.AxlesPerBogie, v.BogieWheelbaseMetres, _rate, seed++);
            _bogies.Add((voice, at));
            var captured = voice;
            float cr = creep;
            var src = new Source
            {
                Label = $"{v.Name} #{unit + 1} bogie {b + 1}",
                AlongMetres = at, HeightMetres = 0.45f, ExtentMetres = 2.2f, Kind = TrainLayout.Kind.Bogie,
                Bogie = new BogieRecipe(v.Wheels, Profile.Track, Profile.RollingReferenceDb, v.AxlesPerBogie, v.BogieWheelbaseMetres, creep),
            };
            src.Render = speed => captured.Step(speed, cr);
            _sources.Add(src);
        }

        // An empty steel body drums with everything its bogies do. A loaded one does not.
        if (v.BodyDrumDb > 1f)
        {
            var drum = new BodyDrum(v.BodyDrumHz, v.BodyDrumDb, _rate, seed++);
            _sources.Add(new Source
            {
                Label = $"{v.Name} #{unit + 1} body",
                AlongMetres = mid, HeightMetres = 2.0f, ExtentMetres = v.LengthMetres * 0.5f, Kind = TrainLayout.Kind.Body,
                Drum = new DrumRecipe(v.BodyDrumHz, v.BodyDrumDb),
                Render = speed => drum.Step(speed),
            });
        }

        if (v.Traction is not { } tr) return;

        switch (tr.Kind)
        {
            case RailTraction.DieselElectric when tr.EngineKey != null:
            {
                var eng = new EngineSynth(EngineProfile.ByName(tr.EngineKey), _rate, seed++) { Ignition = true };
                _diesel ??= eng;
                _diesels.Add(eng);
                _notches = tr.NotchRpm;
                var notches = tr.NotchRpm;
                var fan = new FanNoise(tr.FanBlades, tr.FanRpm, tr.FanDb, _rate, seed++);
                float duty = Math.Clamp(Notch / 8f, 0.25f, 1f);
                _sources.Add(new Source
                {
                    Label = $"{v.Name} #{unit + 1} exhaust stack",
                    AlongMetres = mid - v.LengthMetres * 0.22f, HeightMetres = 4.4f, ExtentMetres = 1.0f, Kind = TrainLayout.Kind.ExhaustStack,
                    Render = _ => { eng.Step(); return eng.Exhaust + 0.45f * eng.Intake + 0.6f * eng.Block; },
                    Slow = notch => Govern(eng, notches, notch),
                });
                _sources.Add(new Source
                {
                    Label = $"{v.Name} #{unit + 1} radiator fans",
                    AlongMetres = mid + v.LengthMetres * 0.34f, HeightMetres = 4.2f, ExtentMetres = 1.6f, Kind = TrainLayout.Kind.RadiatorFans,
                    Render = _ => fan.Step(duty),
                    Slow = notch => duty = Math.Clamp(notch / 8f, 0.25f, 1f),
                });
                break;
            }
            case RailTraction.Electric when tr.Drive != null:
            {
                // The motors are on the bogies: an electric train's whine sweeps past twice.
                for (int b = 0; b < v.Bogies; b++)
                {
                    float at = v.Bogies == 1 ? mid : mid + (b - (v.Bogies - 1) * 0.5f) * v.BogieCentresMetres;
                    var d = new ElectricDrive(tr.Drive, v.Wheels.DiameterMetres, _rate, seed++);
                    _drives.Add(d);
                    _drive ??= d;
                    _sources.Add(new Source
                    {
                        Label = $"{v.Name} #{unit + 1} traction {b + 1}",
                        AlongMetres = at, HeightMetres = 0.7f, ExtentMetres = 2.0f, Kind = TrainLayout.Kind.Traction,
                        Render = speed => d.Step(speed),
                        Slow = notch => d.Effort = Math.Clamp(notch / 8f, 0f, 1f),
                    });
                }
                break;
            }
            case RailTraction.Steam when tr.Steam != null:
            {
                _steam = new SteamFrontEnd(tr.Steam, _rate, seed++);
                var st = _steam;
                _sources.Add(new Source
                {
                    Label = $"{v.Name} #{unit + 1} chimney",
                    AlongMetres = along + v.LengthMetres * 0.18f, HeightMetres = 4.6f, ExtentMetres = 0.8f, Kind = TrainLayout.Kind.Chimney,
                    Render = speed => st.Step(speed),
                    Slow = notch => st.Effort = Math.Clamp(notch / 8f, 0f, 1f),
                });
                break;
            }
        }

        string? hornKey = tr.HornKey;
        if (hornKey != null && _horn == null)
        {
            _horn = new ChimeHorn(ModelLibrary.Horn(hornKey), _rate, seed++);
            var h = _horn;
            _sources.Add(new Source
            {
                Label = $"{v.Name} #{unit + 1} horn",
                AlongMetres = along + 2.5f, HeightMetres = 4.8f, ExtentMetres = 0.6f, Kind = TrainLayout.Kind.Horn,
                Render = _ => { h.Step(); return h.Out; },
            });
        }
        if (tr.Steam?.WhistleKey is { } wk && _whistle == null)
        {
            _whistle = new SteamWhistle(ModelLibrary.Whistle(wk), _rate, seed++);
            var w = _whistle;
            _sources.Add(new Source
            {
                Label = $"{v.Name} #{unit + 1} whistle",
                AlongMetres = along + v.LengthMetres * 0.55f, HeightMetres = 4.4f, ExtentMetres = 0.5f, Kind = TrainLayout.Kind.Whistle,
                Render = _ => { w.Step(); return w.Out; },
            });
        }
        string? bellKey = tr.BellKey ?? tr.Steam?.BellKey;
        if (bellKey != null && _bell == null)
        {
            _bell = new StruckBell(ModelLibrary.Bell(bellKey), _rate, seed++);
            var bl = _bell;
            _sources.Add(new Source
            {
                Label = $"{v.Name} #{unit + 1} bell",
                AlongMetres = along + 1.8f, HeightMetres = 3.2f, ExtentMetres = 0.4f, Kind = TrainLayout.Kind.Bell,
                Render = _ => { bl.Step(); return bl.Out; },
            });
        }
    }

    /// <summary>Put the head of the train at a place on the track and line every axle up behind it.</summary>
    public void Place(double headMetres)
    {
        HeadMetres = headMetres;
        foreach (var (voice, along) in _bogies) voice.Place(headMetres - along);
    }

    /// <summary>The listener's position relative to the head of the train, in the train's frame:
    /// +x forward along the track, +y up, +z to the right of the direction of travel. Sets the horn's
    /// directivity, which is most of why a horn brightens as it comes at you.</summary>
    public void SetListener(Vector3 trainFrame)
    {
        // The horn points forward: +z of a ChimeHorn is out of the mouths, so forward along +x here.
        _horn?.SetListener(new Vector3(trainFrame.Z, trainFrame.Y, trainFrame.X));
    }

    private int _slowTick;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        if (_slowTick == 0) UpdateSlow();
        _slowTick = _slowTick + 1 == 64 ? 0 : _slowTick + 1;

        HeadMetres += Speed * _dt;
        float speed = Speed;
        for (int i = 0; i < _sources.Count; i++) _sources[i].Out = _sources[i].Render(speed);
    }

    private void UpdateSlow()
    {
        float notch = Notch;
        for (int i = 0; i < _sources.Count; i++) _sources[i].Slow?.Invoke(notch);
    }

    /// <summary>
    /// A diesel-electric has notches, not a throttle: the engine's own governor holds the notch's speed
    /// with the pedal up, so the sound steps, and takes a couple of seconds.
    /// </summary>
    internal static void Govern(EngineSynth eng, float[] notches, float notch)
    {
        if (notches.Length == 0) return;
        int n = Math.Clamp((int)MathF.Round(notch), 0, notches.Length - 1);
        float want = notches[n], idle = notches[0];
        float frac = n / MathF.Max(1f, notches.Length - 1f);
        float rpm = eng.Rpm;
        eng.Starter = rpm < 100f;
        eng.Throttle = 0f;
        eng.GovernedRpm = want;
        // The alternator is the load: not excited while cranking, all of it from idle up, and its torque
        // goes with speed, which lets a bogged engine pull back up instead of stalling.
        float excited = Math.Clamp((rpm - eng.Profile.CrankingRpm) / MathF.Max(1f, idle - eng.Profile.CrankingRpm), 0f, 1f);
        float atNotch = eng.Profile.PeakTorqueNm * (0.06f + 0.84f * MathF.Pow(frac, 1.3f));
        eng.LoadTorque = excited * atNotch * rpm / want;
    }

    public IEnumerable<string> Describe(float atSpeed = 0f)
    {
        var p = Profile;
        yield return $"{p.Name}: {p.Consist.Sum(c => c.Count)} vehicles, {p.LengthMetres:F0} m, {p.TotalAxles} axles, {_sources.Count} sources";
        yield return $"track: {p.Track.Name}, pinned-pinned {p.Track.PinnedPinnedHz:F0} Hz, "
                   + (p.Track.JointSpacingMetres > 0.1f
                        ? $"joints every {p.Track.JointSpacingMetres:F1} m{(p.Track.StaggeredJoints ? ", staggered" : ", square")}"
                        : "continuous welded rail, no joints at all")
                   + (p.Track.CurveRadiusMetres > 1f ? $", curve {p.Track.CurveRadiusMetres:F0} m radius" : ", straight");
        float v = atSpeed > 0.1f ? atSpeed : p.TypicalSpeedMps;
        if (p.Track.JointSpacingMetres > 0.1f)
        {
            var lead = p.Consist[^1].Vehicle;
            float period = p.Track.StaggeredJoints ? p.Track.JointSpacingMetres * 0.5f : p.Track.JointSpacingMetres;
            yield return $"at {v * 3.6f:F0} km/h: a joint every {period / v * 1000f:F0} ms, "
                       + $"the two axles of a bogie {lead.BogieWheelbaseMetres / v * 1000f:F0} ms apart, "
                       + $"the two bogies of a {lead.Name} {lead.BogieCentresMetres / v * 1000f:F0} ms apart, "
                       + $"and the next vehicle {lead.LengthMetres / v * 1000f:F0} ms after that";
        }
        yield return $"sleepers pass at {p.Track.SleeperPassHz(v):F0} Hz; the whole train takes {p.LengthMetres / v:F1} s to go by";
        foreach (var (vehicle, count) in p.Consist)
        {
            var w = vehicle.Wheels;
            var probe = new BogieVoice(w, p.Track, _track, p.RollingReferenceDb, vehicle.AxlesPerBogie, vehicle.BogieWheelbaseMetres, _rate, 1);
            yield return $"  {count} x {vehicle.Name}: {w.DiameterMetres:F3} m wheels, {(w.TreadBraked ? "tread braked (+9 dB)" : "disc braked")}, "
                       + $"ring modes {string.Join(", ", probe.WheelModeHz.Take(4).Select(f => $"{f:F0}"))} Hz, "
                       + $"contact resonance {probe.ContactResonanceHz:F0} Hz -> {0.5f / probe.ContactResonanceHz * 1000f:F1} ms blows"
                       + (p.Track.CurveRadiusMetres > 1f ? $", squeal at {probe.SquealHz:F0} Hz" : "");
        }
        if (_diesel != null) foreach (var l in _diesel.Describe()) yield return "  " + l;
        if (_steam != null) foreach (var l in _steam.Describe(v)) yield return "  " + l;
        if (_drive != null) foreach (var l in _drive.Describe(v)) yield return "  " + l;
        if (_horn != null) foreach (var l in _horn.Describe()) yield return "  " + l;
        if (_whistle != null) foreach (var l in _whistle.Describe()) yield return "  " + l;
        if (_bell != null) foreach (var l in _bell.Describe().Take(2)) yield return "  " + l;
    }
}

/// <summary>
/// A vehicle body as a drum: an empty steel wagon's thin panels boom at their low modes with
/// everything the bogies do, and a load damps them. Why an empty freight train is louder than a full
/// one.
/// </summary>
internal sealed class BodyDrum
{
    private readonly float _rate;
    private readonly Random _rng;
    private Mode _m1, _m2, _m3;
    private readonly float _amp;
    private float _hp;

    public BodyDrum(float hz, float db, float rate, int seed)
    {
        _rate = rate; _rng = new Random(seed);
        _m1 = new Mode(hz, 9f, rate);
        _m2 = new Mode(hz * 1.87f, 7f, rate);
        _m3 = new Mode(hz * 3.1f, 5f, rate);
        _amp = 20e-6f * MathF.Pow(10f, db / 20f);
    }

    public float Step(float speed) => StepShared(speed, 1f);

    /// <summary>One sample of a body standing for several like it (TrainSlotState): the modes are
    /// linear and each body's drive is its own noise, so together they are one drive <paramref
    /// name="weight"/> times as strong (the root of the sum of their squared weights).</summary>
    public float StepShared(float speed, float weight)
    {
        float drive = (float)(_rng.NextDouble() * 2 - 1) * MathF.Min(1f, speed / 12f) * weight;
        float y = (_m1.Process(drive) + 0.6f * _m2.Process(drive) + 0.35f * _m3.Process(drive)) * 22f * _amp;
        _hp += OnePole.AlphaFor(25f, _rate) * (y - _hp);
        return y - _hp;
    }
}

/// <summary>
/// A locomotive's radiator fans: a metre and a half across, ten or twelve blades, on all the time.
/// At idle they are most of what a standing locomotive is heard as: a roar, not a rumble.
/// </summary>
internal sealed class FanNoise
{
    private readonly float _rate;
    private readonly Random _rng;
    private readonly float _bladeHz, _amp;
    private double _phase;
    private float _lp1, _lp2, _hp;

    public FanNoise(int blades, float rpm, float db, float rate, int seed)
    {
        _rate = rate; _rng = new Random(seed);
        _bladeHz = blades * rpm / 60f;
        _amp = 20e-6f * MathF.Pow(10f, db / 20f);
    }

    public float Step(float duty)
    {
        // Turbulence, plus the blade-passing tone and its octave.
        float n = (float)(_rng.NextDouble() * 2 - 1);
        float a = OnePole.AlphaFor(700f * (0.6f + 0.4f * duty), _rate);
        _lp1 += a * (n - _lp1); _lp2 += a * (_lp1 - _lp2);
        float broad = (_lp1 - _lp2) * 11f;
        _phase += _bladeHz * duty / _rate; if (_phase > 1) _phase -= 1;
        float tone = (MathF.Sin(MathF.Tau * (float)_phase) * 0.35f + MathF.Sin(2f * MathF.Tau * (float)_phase) * 0.12f) * duty;
        float y = (broad + tone) * _amp * (0.45f + 0.55f * duty);
        _hp += OnePole.AlphaFor(40f, _rate) * (y - _hp);
        return y - _hp;
    }
}
