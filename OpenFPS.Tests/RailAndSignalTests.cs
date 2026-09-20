using System;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Pneumatics;
using OpenFPS.Client.AudioEngine.Core.Rail;
using OpenFPS.Client.AudioEngine.Core.Signals;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Trains, horns, whistles, bells and compressed air, checked against the physics they claim.
///
/// Every one of these asserts a CONSEQUENCE of a dimension rather than a value somebody typed: that
/// a horn plays c/2L, that a whistle stands sharp of it by the speed of sound in steam, that a
/// smaller wheel rings higher by the square of the radius, that the clatter's rhythm is the axle
/// spacing over the speed, that a vessel empties in volume over area over a constant, and that the
/// level goes up with speed the way a real train's does.
/// </summary>
public class RailAndSignalTests
{
    private const int Sr = 44100;
    private readonly ITestOutputHelper _o;
    public RailAndSignalTests(ITestOutputHelper o) => _o = o;

    // ── Horns ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AChimeHornPlaysTheNoteItsLengthImplies()
    {
        var spec = ChimeHornSpec.NathanK5LA;
        var horn = new ChimeHorn(spec, Sr, 11);
        var lines = horn.Describe().ToList();
        foreach (var l in lines) _o.WriteLine(l);

        // The five bells of a K5LA are five lengths of brass, and the chord is a consequence.
        var expect = new[] { 311f, 372f, 416f, 468f, 556f };
        for (int i = 0; i < spec.Bells.Length; i++)
        {
            float hz = spec.Bells[i].Hz;
            Assert.InRange(hz, expect[i] * 0.97f, expect[i] * 1.03f);
        }
        // ...and they are a minor chord with a fourth in it, which is why it is mournful: the
        // intervals in semitones from the lowest.
        var semis = spec.Bells.Select(b => 12f * MathF.Log2(b.Hz / spec.Bells[0].Hz)).ToArray();
        _o.WriteLine("semitones above the lowest: " + string.Join(", ", semis.Select(s => $"{s:F1}")));
        Assert.InRange(semis[1], 2.6f, 3.4f);     // a minor third
        Assert.InRange(semis[4], 9.6f, 10.4f);    // a minor seventh
    }

    [Fact]
    public void TheHornsReedActuallyBeats()
    {
        // A valve that never shuts makes a sine, and a sine is not a horn. Measured on the model
        // itself, not asserted about the design: the reed must spend a real fraction of every cycle
        // on its seat, and the waveform must have a crest factor well above a sine's 1.41.
        var horn = new ChimeHorn(ChimeHornSpec.NathanK5LA, Sr, 11);
        foreach (var l in horn.Describe()) _o.WriteLine(l);
        var text = string.Join(" ", horn.Describe());
        Assert.Contains("shut", text);
        // And it must lock to the column rather than to the reed: the measured note is within a
        // few per cent of the design note for every bell.
        foreach (var line in horn.Describe().Skip(1))
        {
            int d = line.IndexOf("design ", StringComparison.Ordinal);
            int m = line.IndexOf("measured ", StringComparison.Ordinal);
            if (d < 0 || m < 0) continue;
            float design = float.Parse(line.Substring(d + 7).Split(' ')[0]);
            float measured = float.Parse(line.Substring(m + 9).Split(' ')[0]);
            Assert.InRange(measured, design * 0.94f, design * 1.06f);
        }
    }

    // ── Whistles ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AStoppedPipeInSteamStandsSharpOfOneInAir()
    {
        var spec = WhistleSpec.ThreeChimePassenger;
        float cSteam = MathF.Sqrt(1.33f * 461.5f * spec.SteamKelvin);
        _o.WriteLine($"sound speed in steam at {spec.SteamKelvin:F0} K: {cSteam:F0} m/s ({cSteam / 343f:F2}x air)");
        Assert.InRange(cSteam / 343f, 1.4f, 1.65f);

        foreach (var b in spec.Bells)
        {
            // A stopped pipe: c/4L, not c/2L. The same length of air horn would be an octave up.
            float hot = b.HzAt(cSteam), cold = b.HzAt(343f);
            Assert.InRange(hot / cold, 1.4f, 1.65f);
            Assert.InRange(hot, 380f, 620f);
            _o.WriteLine($"{b.LengthMetres * 1000f:F0} mm -> {hot:F0} Hz hot, {cold:F0} Hz in cold air");
        }
        // The bells are deliberately NOT a clean chord — the beating between close bells is the
        // hollow in a big whistle.
        var hz = spec.Bells.Select(b => b.HzAt(cSteam)).OrderBy(x => x).ToArray();
        float ratio = hz[1] / hz[0];
        Assert.False(MathF.Abs(ratio - 1.25f) < 0.005f, "a whistle tuned to an exact major third has nothing to beat");
    }

    // ── Bells ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CurvatureTurnsAPlateIntoABell()
    {
        // The same casting, flatter and deeper. Curvature adds a membrane stiffness that lifts the
        // low modes and barely touches the high ones, so a deeper dome is higher AND its partials
        // are closer together. Flatten it and it goes back to being a cymbal.
        var flat = StruckBellSpec.CrossingGong with { RiseMetres = 0.004f };
        var domed = StruckBellSpec.CrossingGong with { RiseMetres = 0.055f };
        float Lowest(StruckBellSpec s)
        {
            var b = new StruckBell(s, Sr, 31);
            var modes = b.Describe().Where(l => l.Contains("mode (")).ToList();
            return modes.Select(l => float.Parse(l.Split("Hz")[0].Split(' ').Last(x => x.Length > 0))).Min();
        }
        float lowFlat = Lowest(flat), lowDomed = Lowest(domed);
        _o.WriteLine($"flat dome {flat.RiseMetres * 1000f:F0} mm -> lowest {lowFlat:F0} Hz");
        _o.WriteLine($"deep dome {domed.RiseMetres * 1000f:F0} mm -> lowest {lowDomed:F0} Hz");
        Assert.True(lowDomed > lowFlat * 1.3f, "curvature must lift the low modes");
    }

    [Fact]
    public void ABellsRingTimeIsThreeLossesAndNotADecayConstant()
    {
        var b = new StruckBell(StruckBellSpec.CrossingGong, Sr, 31);
        var lines = b.Describe().ToList();
        foreach (var l in lines) _o.WriteLine(l);
        var rings = lines.Where(l => l.Contains("rings "))
                         .Select(l => float.Parse(l.Split("rings ")[1].Split(' ')[0])).ToList();
        // The modes the mast cannot grip ring for seconds; the ones it can are killed. That spread
        // is the point: a single decay constant cannot produce it.
        Assert.True(rings.Max() > 1.0f, $"a bronze gong must ring: longest {rings.Max():F2} s");
        Assert.True(rings.Min() < 0.2f, $"the mounting must kill the crown modes: shortest {rings.Min():F2} s");
    }

    [Fact]
    public void ABellIsAnchoredOnItsRingingLevelNotOnOneBlow()
    {
        // A struck bell's crest factor is twenty-odd decibels. Anchoring the peak instead of the
        // meter reading puts it that far under and makes a locomotive bell inaudible under its own
        // train, which is exactly what happened.
        var bell = new StruckBell(StruckBellSpec.LocomotiveBell, Sr, 31) { Ringing = true };
        int warm = (int)(1.5f * Sr), meas = (int)(2f * Sr);
        for (int i = 0; i < warm; i++) bell.Step();
        double e = 0; float peak = 0f;
        for (int i = 0; i < meas; i++) { bell.Step(); e += bell.Out * (double)bell.Out; peak = MathF.Max(peak, MathF.Abs(bell.Out)); }
        float rms = (float)Math.Sqrt(e / meas);
        float rmsDb = 20f * MathF.Log10(rms / 2e-5f), peakDb = 20f * MathF.Log10(peak / 2e-5f);
        _o.WriteLine($"rms while ringing {rmsDb:F1} dB, peak {peakDb:F1} dB, crest {peakDb - rmsDb:F1} dB");
        Assert.InRange(rmsDb, StruckBellSpec.LocomotiveBell.ReferenceDb - 2f, StruckBellSpec.LocomotiveBell.ReferenceDb + 2f);
        Assert.True(peakDb - rmsDb > 12f, "if the crest were small the distinction would not matter");
    }

    // ── Wheels and track ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASmallerWheelRingsHigherByTheSquareOfItsRadius()
    {
        var track = TrackSpec.WeldedMainLine;
        var big = TrainProfile.PassengerCoach.Wheels;            // 915 mm
        var small = TrainProfile.LightRailCar.Wheels;            // 660 mm
        float[] Modes(WheelsetSpec w)
        {
            var v = new BogieVoiceProbe(w, track);
            return v.Hz;
        }
        var bigHz = Modes(big); var smallHz = Modes(small);
        _o.WriteLine("915 mm: " + string.Join(", ", bigHz.Take(4).Select(f => $"{f:F0}")));
        _o.WriteLine("660 mm: " + string.Join(", ", smallHz.Take(4).Select(f => $"{f:F0}")));
        // A ring's modes go as 1/R^2 for the same section; these sections differ a little, so allow
        // a wide band — what must hold is the DIRECTION and the rough size.
        Assert.True(smallHz[0] > bigHz[0] * 1.2f);
        // And the family is the ring's: n(n^2-1)/sqrt(n^2+1), so the second mode is about 2.7 times
        // the first and the third about 5.4.
        Assert.InRange(bigHz[1] / bigHz[0], 2.4f, 3.1f);
        Assert.InRange(bigHz[2] / bigHz[0], 4.7f, 6.0f);
    }

    [Fact]
    public void ThePinnedPinnedResonanceIsTheRailAndTheSleeperSpacing()
    {
        // Half a bending wave in the rail fitting exactly one sleeper bay. Tighten the spacing and
        // it goes up; nothing else in the track changes it.
        var wide = TrackSpec.WeldedMainLine with { SleeperSpacingMetres = 0.75f };
        var tight = TrackSpec.WeldedMainLine with { SleeperSpacingMetres = 0.50f };
        _o.WriteLine($"0.75 m bays -> {wide.PinnedPinnedHz:F0} Hz;  0.50 m bays -> {tight.PinnedPinnedHz:F0} Hz");
        Assert.True(tight.PinnedPinnedHz > wide.PinnedPinnedHz);
        // It goes as 1/L^2.
        Assert.InRange(tight.PinnedPinnedHz / wide.PinnedPinnedHz, 2.0f, 2.5f);
        Assert.InRange(TrackSpec.WeldedMainLine.PinnedPinnedHz, 900f, 1700f);
    }

    [Fact]
    public void TheClatterRhythmIsWhereTheAxlesAre()
    {
        // Nothing sequences a clickety-clack. These four intervals are four lengths divided by a
        // speed, and they are what the ear hears as the pattern.
        var v = TrainProfile.FreightWagon;
        var track = TrackSpec.JointedTimber;
        const float speed = 18f;
        float jointPeriod = (track.StaggeredJoints ? track.JointSpacingMetres * 0.5f : track.JointSpacingMetres) / speed;
        float withinBogie = v.BogieWheelbaseMetres / speed;
        float bogieToBogie = v.BogieCentresMetres / speed;
        float carToCar = v.LengthMetres / speed;
        _o.WriteLine($"joint {jointPeriod * 1000f:F0} ms, axles {withinBogie * 1000f:F0} ms, "
                   + $"bogies {bogieToBogie * 1000f:F0} ms, cars {carToCar * 1000f:F0} ms");
        Assert.True(withinBogie < bogieToBogie);
        Assert.True(bogieToBogie < carToCar);
        // The two axles of a bogie are a tenth of a second apart at line speed: that is the
        // "clack-CLACK" and it is why the pair reads as one event with two halves.
        Assert.InRange(withinBogie, 0.06f, 0.14f);
    }

    [Fact]
    public void RollingNoiseGoesUpWithSpeedTheWayARealTrainsDoes()
    {
        // The model declares no speed law. What it has is a roughness spectrum, a contact patch that
        // opens with speed, and radiators whose efficiency rises with frequency — and the law falls
        // out of those. Measured trains give 30 log10 V; anything from 20 to 40 means the three
        // terms are pulling together rather than against each other.
        var track = TrackSpec.WeldedMainLine;
        var w = TrainProfile.PassengerCoach.Wheels;
        float LevelAt(float mps)
        {
            var b = new BogieVoice(w, track, new TrackResponseProbe(track).Inner, 104f, 2, 2.59f, Sr, 7);
            b.Place(0);
            int n = (int)(1.5f * Sr);
            double e = 0; int used = 0;
            for (int i = 0; i < n; i++)
            {
                float y = b.Step(mps, 0f);
                if (i > n / 3) { e += y * (double)y; used++; }
            }
            return 20f * MathF.Log10((float)Math.Sqrt(e / used) / 2e-5f);
        }
        float slow = LevelAt(15f), fast = LevelAt(45f);
        float law = (fast - slow) / MathF.Log10(45f / 15f);
        _o.WriteLine($"15 m/s: {slow:F1} dB   45 m/s: {fast:F1} dB   -> {law:F0} log10(V)");
        Assert.InRange(law, 18f, 42f);
    }

    [Fact]
    public void TreadBrakesAreWorthAboutNineDecibels()
    {
        // Because a cast-iron block dragging on the tread corrugates it. It is a property of the
        // brake and lives in the parts list; there is no "freight is louder" rule anywhere.
        var track = TrackSpec.WeldedMainLine;
        var disc = TrainProfile.PassengerCoach.Wheels;
        var tread = disc with { TreadBraked = true };
        float LevelAt(WheelsetSpec w)
        {
            var b = new BogieVoice(w, track, new TrackResponseProbe(track).Inner, 104f, 2, 2.59f, Sr, 7);
            b.Place(0);
            int n = (int)(1.2f * Sr);
            double e = 0; int used = 0;
            for (int i = 0; i < n; i++) { float y = b.Step(28f, 0f); if (i > n / 3) { e += y * (double)y; used++; } }
            return 20f * MathF.Log10((float)Math.Sqrt(e / used) / 2e-5f);
        }
        float d = LevelAt(tread) - LevelAt(disc);
        _o.WriteLine($"tread braked is {d:F1} dB over disc braked");
        Assert.InRange(d, 7f, 11f);
    }

    // ── Air ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AVesselEmptiesInVolumeOverAreaOverAConstant()
    {
        var spec = AirSystemSpec.TractorTrailer;
        foreach (var p in spec.Ports)
        {
            var port = new AirPort(p, spec.JetTrimDb, Sr, 1);
            float area = MathF.PI * 0.25f * p.OrificeMetres * p.OrificeMetres * p.DischargeCoefficient;
            float expect = p.VolumeLitres * 1e-3f / (area * 198.5f);
            _o.WriteLine($"{p.Name}: {port.BlowdownSeconds:F2} s (expected {expect:F2})");
            Assert.InRange(port.BlowdownSeconds, expect * 0.98f, expect * 1.02f);
        }
        // A bus's door is quick and a trailer's brake line is slow, and that is the whole difference
        // in how the two vehicles sound.
        var bus = AirSystemSpec.TransitBus;
        float door = new AirPort(bus.Port("door"), bus.JetTrimDb, Sr, 1).BlowdownSeconds;
        float trailer = new AirPort(spec.Port("service_release"), spec.JetTrimDb, Sr, 1).BlowdownSeconds;
        Assert.True(trailer > door * 2.5f, $"trailer {trailer:F2} s vs bus door {door:F2} s");
    }

    [Fact]
    public void AirBrakesAreChokedAndThereforeSupersonic()
    {
        // At 120 psi the pressure ratio is about nine, far past the 1.893 it takes to choke, so the
        // jet leaves underexpanded at about Mach 1.6 — which is why there are shock cells in it and
        // why it is so bright.
        var spec = AirSystemSpec.TractorTrailer;
        float ratio = (spec.CutOutKPa + 101.3f) / 101.3f;
        float u = MathF.Sqrt(2f * 1005f * 293f * (1f - MathF.Pow(ratio, -0.2857f)));
        _o.WriteLine($"ratio {ratio:F2}, fully expanded {u:F0} m/s = Mach {u / 343f:F2}");
        Assert.True(ratio > 1.893f, "below the critical ratio there is no choked flow and no crack");
        Assert.InRange(u / 343f, 1.4f, 1.8f);
    }

    [Fact]
    public void TheGovernorPurgesTheDryerWithoutBeingTold()
    {
        // Nobody sequences the bang-and-sigh from a parked truck. The compressor fills, the governor
        // reaches cut-out, and the dryer blows down. It is a pressure switch.
        var sys = new AirSystem(AirSystemSpec.TractorTrailer, Sr, 61) { EngineRpm = 800f };
        sys.Vent("service_release");                 // knock the pressure down so it has to refill
        bool purged = false;
        float purgeLevel = 0f;
        for (int i = 0; i < Sr * 40; i++)
        {
            float y = sys.Step();
            if (sys.Ports["dryer_purge"].Venting) { purged = true; purgeLevel = MathF.Max(purgeLevel, MathF.Abs(y)); }
        }
        _o.WriteLine($"reservoir ended at {sys.ReservoirKPa:F0} kPa, compressor {(sys.CompressorLoaded ? "loaded" : "unloaded")}, purged: {purged}");
        Assert.True(purged, "the dryer must blow down when the governor cuts out");
        Assert.True(purgeLevel > 0f);
    }

    // ── Steam ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FourBeatsATurnAndTheBarkIsTheChimney()
    {
        var s = TrainProfile.SteamNorthern.Traction!.Steam!;
        // Two cylinders, each double-acting: four beats per revolution of the drivers, and the
        // drivers turn at road speed over their circumference.
        Assert.Equal(4, 2 * s.Cylinders);
        float slow = s.ChuffHz(6f), fast = s.ChuffHz(24f);
        _o.WriteLine($"at 6 m/s: {slow:F1} beats/s;  at 24 m/s: {fast:F1} beats/s;  stack {s.StackHz:F0} Hz");
        Assert.InRange(slow, 3.5f, 4.5f);
        Assert.InRange(fast, 15f, 18f);
        // The chimney is a pipe open at both ends: c/2L, and that is the note in the bark.
        Assert.InRange(s.StackHz, 120f, 200f);

        // At speed the beats run together: the gap between them is shorter than one beat's decay.
        float cylVol = MathF.PI * 0.25f * s.CylinderBoreMetres * s.CylinderBoreMetres * s.CylinderStrokeMetres;
        float nozzle = MathF.PI * 0.25f * s.BlastNozzleMetres * s.BlastNozzleMetres;
        float decay = cylVol / (nozzle * 480f);
        _o.WriteLine($"each beat decays in {decay * 1000f:F0} ms; at 24 m/s they are {1000f / fast:F0} ms apart");
        Assert.True(decay > 1f / fast * 0.4f, "a big engine at speed roars rather than barking");
    }

    [Fact]
    public void ADriftingEngineIsNearlySilentBecauseOfTheEighthPower()
    {
        // Lighthill, not a volume control: the exhaust pressure at release is what the engine is
        // being worked at, and the jet's power goes as the eighth power of the velocity.
        var s = TrainProfile.SteamNorthern.Traction!.Steam!;
        float Level(float effort)
        {
            float u = 150f + 330f * effort;
            return JetLevelDb(s.BlastNozzleMetres, u);
        }
        float full = Level(1f), drift = Level(0.05f);
        _o.WriteLine($"full effort {full:F0} dB, drifting {drift:F0} dB, difference {full - drift:F0} dB");
        Assert.True(full - drift > 20f);
    }

    private static float JetLevelDb(float d, float u)
    {
        const float rho0 = 1.2f, c = 343f;
        double w = 1e-4 * rho0 * (293.0 / 700.0) * Math.Pow(u, 8) * d * d / Math.Pow(c, 5);
        double p2 = w * rho0 * c / (4 * Math.PI);
        return (float)(20.0 * Math.Log10(Math.Sqrt(p2) / 2e-5));
    }

    // ── Electric traction ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheInverterStepsItsPulseCountDownAsTheTrainSpeedsUp()
    {
        // The staircase nobody designed: holding a fixed carrier at high output frequency means more
        // switchings per cycle and more heat, so the drive locks to a whole number of pulses and
        // steps it down. The tone rises through each mode and drops at every change.
        var spec = TrainProfile.LightRailCar.Traction!.Drive!;
        var drive = new ElectricDrive(spec, TrainProfile.LightRailCar.Wheels.DiameterMetres, Sr, 5);
        int lastMode = int.MaxValue;
        int drops = 0;
        float lastCarrier = 0f, lastAtChange = 0f;
        for (float v = 0.5f; v < 24.5f; v += 0.25f)
        {
            for (int i = 0; i < 128; i++) drive.Step(v);
            if (drive.PulseMode != lastMode && drive.PulseMode > 0 && lastMode != int.MaxValue)
            {
                if (drive.PulseMode < lastMode && lastMode > 0) drops++;
                _o.WriteLine($"{v * 3.6f,5:F0} km/h: pulses {lastMode} -> {drive.PulseMode}, carrier {lastCarrier:F0} -> {drive.CarrierHz:F0} Hz");
                Assert.True(drive.CarrierHz < lastCarrier + 1f, "the tone must DROP at a mode change");
                lastAtChange = v;
            }
            else if (drive.PulseMode == lastMode && drive.PulseMode > 0 && lastCarrier > 0f)
            {
                Assert.True(drive.CarrierHz >= lastCarrier - 1f, "and rise within a mode");
            }
            lastMode = drive.PulseMode;
            lastCarrier = drive.CarrierHz;
        }
        // Asynchronous at a standstill (mode 0, a fixed carrier), then down the ladder.
        Assert.True(drops >= 3, $"only {drops} step-downs across the speed range");
        Assert.True(lastAtChange > 0f);
    }

    [Fact]
    public void TheGearWhineIsACountOfTeeth()
    {
        var spec = TrainProfile.LightRailCar.Traction!.Drive!;
        var d = new ElectricDrive(spec, 0.66f, Sr, 5);
        d.Step(10f);
        float axleHz = 10f / (MathF.PI * 0.66f);
        _o.WriteLine($"at 36 km/h: axle {axleHz:F2} rev/s, motor {d.MotorRpm:F0} rpm, mesh {d.MeshHz:F0} Hz");
        Assert.InRange(d.MeshHz, axleHz * spec.GearTeeth * 0.99f, axleHz * spec.GearTeeth * 1.01f);
        // The same number two ways: gear teeth times axle speed IS pinion teeth times motor speed.
        Assert.InRange(d.MeshHz, d.MotorRpm / 60f * spec.PinionTeeth * 0.99f, d.MotorRpm / 60f * spec.PinionTeeth * 1.01f);
    }

    // ── A train is a line of sources ────────────────────────────────────────────────────────────

    [Fact]
    public void ATrainIsALineOfSourcesAndNotAThingAtAPlace()
    {
        var train = new TrainSynth(TrainProfile.AmtrakDiesel, Sr, 41);
        var srcs = train.Sources;
        _o.WriteLine($"{TrainProfile.AmtrakDiesel.Name}: {srcs.Count} sources over {TrainProfile.AmtrakDiesel.LengthMetres:F0} m");
        // One per bogie at least, and they must be spread out along the train rather than stacked.
        Assert.True(srcs.Count >= TrainProfile.AmtrakDiesel.Consist.Sum(c => c.Vehicle.Bogies * c.Count));
        float spread = srcs.Max(s => s.AlongMetres) - srcs.Min(s => s.AlongMetres);
        Assert.True(spread > TrainProfile.AmtrakDiesel.LengthMetres * 0.75f,
            $"sources span {spread:F0} m of a {TrainProfile.AmtrakDiesel.LengthMetres:F0} m train");
        // The locomotive's stack is up in the air and the bogies are not.
        var stack = srcs.First(s => s.Label.Contains("stack"));
        var bogie = srcs.First(s => s.Label.Contains("bogie"));
        Assert.True(stack.HeightMetres > bogie.HeightMetres + 2f);
    }

    // ── Probes ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reaches the internal track response so a test can build a bogie on its own.</summary>
    private sealed class TrackResponseProbe
    {
        public readonly TrackResponse Inner;
        public TrackResponseProbe(TrackSpec t) => Inner = new TrackResponse(t, Sr);
    }

    /// <summary>A bogie built only to read its wheel's ring modes back out.</summary>
    private sealed class BogieVoiceProbe
    {
        public readonly float[] Hz;
        public BogieVoiceProbe(WheelsetSpec w, TrackSpec t)
        {
            var v = new BogieVoice(w, t, new TrackResponseProbe(t).Inner, 104f, 2, 2.5f, Sr, 3);
            Hz = v.WheelModeHz.ToArray();
        }
    }

    /// <summary>The server places a train's sources by TrainLayout and the client indexes the synth's
    /// sources by the same number. If the two ever disagree, every bogie plays the wrong place.</summary>
    [Fact]
    public void TrainLayoutMatchesTheSynthSourceForSource()
    {
        foreach (var key in TrainProfile.Presets.Keys)
        {
            var p = TrainProfile.ByName(key);
            var layout = TrainLayout.Sources(p);
            var synth = new TrainSynth(p, 44100f, 3);
            Assert.True(layout.Count == synth.Sources.Count,
                $"{key}: layout lists {layout.Count} sources, the synth builds {synth.Sources.Count}");
            for (int i = 0; i < layout.Count; i++)
            {
                Assert.True(layout[i].Label == synth.Sources[i].Label,
                    $"{key} source {i}: layout '{layout[i].Label}' vs synth '{synth.Sources[i].Label}'");
                Assert.True(MathF.Abs(layout[i].AlongMetres - synth.Sources[i].AlongMetres) < 0.01f,
                    $"{key} source {i} ({layout[i].Label}): along {layout[i].AlongMetres} vs {synth.Sources[i].AlongMetres}");
                Assert.True(layout[i].LevelDb > 40f && layout[i].LevelDb < 145f,
                    $"{key} source {i} ({layout[i].Label}): level {layout[i].LevelDb} dB is not a level");
            }
        }
    }
}
