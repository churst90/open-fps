using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.AudioEngine.Core.Rail;

/// <summary>
/// A whole train, as a line of sources strung out along the track.
///
/// THIS IS THE POINT OF THE WHOLE FILE: a train is not a thing at a place. A six-coach Amtrak is a
/// hundred and seventy metres long and a fifty-wagon freight is nearly a kilometre, and the
/// difference between a train going past and a lorry going past is not timbre, it is that the train
/// ARRIVES FOR A MINUTE. Every bogie is its own source with its own distance, its own Doppler and
/// its own arrival time, and everything people recognise falls out of that and out of nothing else:
/// the level that rises and then sits on a plateau instead of peaking (a line source falls off by
/// three decibels a doubling, not six, until you are further away than it is long); the clatter
/// sweeping down the train past you; the way the far end of a long freight is a different, duller
/// sound than the near end because the air has taken the top off it.
///
/// So <see cref="Sources"/> is the deliverable. Each one knows how far behind the head of the train
/// it sits and what it is emitting in pascals at a metre; where they are heard is the renderer's or
/// the mixer's business. A bogie is one source because the axles of a bogie share a rail and a
/// sleeper bay; two bogies of the same coach are two sources because they are eighteen metres apart
/// and the ear can hear that.
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
        /// <summary>The size of the thing, metres — a bogie is small, a locomotive body is not.
        /// The renderer uses it to stop the inverse square from running away at close range.</summary>
        public float ExtentMetres { get; init; } = 1.5f;
        /// <summary>Pascals at one metre, valid after Step().</summary>
        public float Out { get; internal set; }
        internal Func<float> Render = () => 0f;
    }

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
    public SteamFrontEnd? Steam => _steam;
    public EngineSynth? Diesel => _diesel;

    public IReadOnlyList<Source> Sources => _sources;

    public TrainSynth(TrainProfile p, float rate = 44100f, int seed = 41, double headAt = 0)
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
            // On a curve a rigid wheelset cannot point where it is going: the creep angle it is
            // forced into is about half the bogie's wheelbase over the radius. Below a few
            // milliradians nothing happens; past it, the wheel sings.
            float creep = Profile.Track.CurveRadiusMetres > 1f
                ? 0.5f * v.BogieWheelbaseMetres / Profile.Track.CurveRadiusMetres
                : 0f;
            var voice = new BogieVoice(v.Wheels, Profile.Track, _track, Profile.RollingReferenceDb,
                                       v.AxlesPerBogie, v.BogieWheelbaseMetres, _rate, seed++);
            _bogies.Add((voice, at));
            var captured = voice;
            float cr = creep;
            var src = new Source
            {
                Label = $"{v.Name} #{unit + 1} bogie {b + 1}",
                AlongMetres = at, HeightMetres = 0.45f, ExtentMetres = 2.2f,
            };
            src.Render = () => captured.Step(Speed, cr);
            _sources.Add(src);
        }

        // An empty steel body drums with everything its bogies do. A loaded one does not.
        if (v.BodyDrumDb > 1f)
        {
            var drum = new BodyDrum(v.BodyDrumHz, v.BodyDrumDb, _rate, seed++);
            _sources.Add(new Source
            {
                Label = $"{v.Name} #{unit + 1} body",
                AlongMetres = mid, HeightMetres = 2.0f, ExtentMetres = v.LengthMetres * 0.5f,
                Render = () => drum.Step(Speed),
            });
        }

        if (v.Traction is not { } tr) return;

        switch (tr.Kind)
        {
            case RailTraction.DieselElectric when tr.EngineKey != null:
            {
                _diesel = new EngineSynth(EngineProfile.ByName(tr.EngineKey), _rate, seed++) { Ignition = true };
                _notches = tr.NotchRpm;
                var fan = new FanNoise(tr.FanBlades, tr.FanRpm, tr.FanDb, _rate, seed++);
                var eng = _diesel;
                _sources.Add(new Source
                {
                    Label = $"{v.Name} #{unit + 1} exhaust stack",
                    AlongMetres = mid - v.LengthMetres * 0.22f, HeightMetres = 4.4f, ExtentMetres = 1.0f,
                    Render = () => { eng.Step(); return eng.Exhaust + 0.45f * eng.Intake + 0.6f * eng.Block; },
                });
                _sources.Add(new Source
                {
                    Label = $"{v.Name} #{unit + 1} radiator fans",
                    AlongMetres = mid + v.LengthMetres * 0.34f, HeightMetres = 4.2f, ExtentMetres = 1.6f,
                    Render = () => fan.Step(Math.Clamp(Notch / 8f, 0.25f, 1f)),
                });
                break;
            }
            case RailTraction.Electric when tr.Drive != null:
            {
                // The motors are ON the bogies, not in the middle of the car — which is why an
                // electric train's whine sweeps past you twice.
                for (int b = 0; b < v.Bogies; b++)
                {
                    float at = v.Bogies == 1 ? mid : mid + (b - (v.Bogies - 1) * 0.5f) * v.BogieCentresMetres;
                    var d = new ElectricDrive(tr.Drive, v.Wheels.DiameterMetres, _rate, seed++);
                    _drives.Add(d);
                    _drive ??= d;
                    _sources.Add(new Source
                    {
                        Label = $"{v.Name} #{unit + 1} traction {b + 1}",
                        AlongMetres = at, HeightMetres = 0.7f, ExtentMetres = 2.0f,
                        Render = () => d.Step(Speed),
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
                    AlongMetres = along + v.LengthMetres * 0.18f, HeightMetres = 4.6f, ExtentMetres = 0.8f,
                    Render = () => st.Step(Speed),
                });
                break;
            }
        }

        // Whatever it warns people with.
        string? hornKey = tr.HornKey;
        if (hornKey != null && _horn == null)
        {
            _horn = new ChimeHorn(ModelLibrary.Horn(hornKey), _rate, seed++);
            var h = _horn;
            _sources.Add(new Source
            {
                Label = $"{v.Name} #{unit + 1} horn",
                AlongMetres = along + 2.5f, HeightMetres = 4.8f, ExtentMetres = 0.6f,
                Render = () => { h.Step(); return h.Out; },
            });
        }
        if (tr.Steam?.WhistleKey is { } wk && _whistle == null)
        {
            _whistle = new SteamWhistle(ModelLibrary.Whistle(wk), _rate, seed++);
            var w = _whistle;
            _sources.Add(new Source
            {
                Label = $"{v.Name} #{unit + 1} whistle",
                AlongMetres = along + v.LengthMetres * 0.55f, HeightMetres = 4.4f, ExtentMetres = 0.5f,
                Render = () => { w.Step(); return w.Out; },
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
                AlongMetres = along + 1.8f, HeightMetres = 3.2f, ExtentMetres = 0.4f,
                Render = () => { bl.Step(); return bl.Out; },
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
        for (int i = 0; i < _sources.Count; i++) _sources[i].Out = _sources[i].Render();
    }

    private void UpdateSlow()
    {
        if (_diesel != null && _notches.Length > 0)
        {
            // A diesel-electric has no throttle, it has notches, and a governor that holds the
            // notch by moving the fuel rack. So the sound steps rather than sweeps, and the engine
            // takes a couple of seconds to get there because it weighs what it weighs.
            int n = Math.Clamp((int)MathF.Round(Notch), 0, _notches.Length - 1);
            float want = _notches[n];
            float err = (want - _diesel.Rpm) / MathF.Max(100f, want);
            _diesel.Throttle = Math.Clamp(_diesel.Throttle + err * 0.09f, 0.05f, 1f);
            // The alternator is the load, and it takes what the traction motors are asking for.
            float frac = n / MathF.Max(1f, _notches.Length - 1f);
            _diesel.LoadTorque = _diesel.Profile.PeakTorqueNm * (0.06f + 0.84f * MathF.Pow(frac, 1.3f));
            _diesel.Starter = _diesel.Rpm < 100f;
        }
        float effort = Math.Clamp(Notch / 8f, 0f, 1f);
        foreach (var d in _drives) d.Effort = effort;
        if (_steam != null) _steam.Effort = effort;
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
/// A vehicle body as a drum. An empty steel wagon is a box of thin panels hung off the bogies, and
/// everything the bogies do goes into it: it booms at the panels' own low modes, and it stops when
/// the wagon is loaded because the load damps it. It is why an empty freight train is louder than a
/// full one and sounds completely different.
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

    public float Step(float speed)
    {
        float drive = (float)(_rng.NextDouble() * 2 - 1) * MathF.Min(1f, speed / 12f);
        float y = (_m1.Process(drive) + 0.6f * _m2.Process(drive) + 0.35f * _m3.Process(drive)) * 22f * _amp;
        _hp += OnePole.AlphaFor(25f, _rate) * (y - _hp);
        return y - _hp;
    }
}

/// <summary>
/// A locomotive's radiator fans: a metre and a half across, ten or twelve blades, and on all the
/// time. At idle on a summer night they are most of what you hear of a standing locomotive — more
/// than the engine — and they are the reason a diesel in a yard is a roar and not a rumble.
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
        // Broadband, because a fan is mostly turbulence; plus the blade-passing tone and its octave,
        // because it is also a row of blades going past.
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
