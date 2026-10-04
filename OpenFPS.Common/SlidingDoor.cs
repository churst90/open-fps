using System;
using System.Collections.Generic;
using System.Text;
using static OpenFPS.Common.DoorPhysics;

namespace OpenFPS.Common;

/// <summary>
/// Sliding doors simulated as the objects, the way <see cref="KnobDoor"/> and <see cref="PushBarDoor"/> are:
/// a glass patio door slid by hand on rollers in a sill track, and an automatic door hung from carriages in
/// a header and driven by a motor through a worm gear and a toothed belt.
///
/// The parts:
///
///   The LEAF: an aluminium frame round glass, carried on two rollers. It has a vertical bounce and a
///   rock on its rollers, and its frame and glass ring as dense fields fed through the roller brackets.
///
///   The ROLLERS: a wheel on a bracket spring in the leaf, pressed on its rail by the leaf's weight
///   through a Hertz contact. What a roller rolls over is the rail's roughness, the wheel's own (out of
///   round, a flat where it sat for years), and grit. A tyre wraps round a grain smaller than its own
///   sink under load; a bigger one lifts the wheel by what stands above that, and crushes under it a few
///   steps at a time. The wheel only feels what its curvature lets it reach: over a grain its centre
///   follows a parabola of the wheel's radius, not the grain's shape.
///
///   The TRACK (a patio sill) or the HEADER (an automatic door's): an aluminium extrusion, a dense field,
///   struck through a patch at each contact.
///
///   The WEATHERSTRIP: polypropylene pile brushing the frame, a drag and a hiss from its fibres slipping.
///
///   On a PATIO door: a hand on the pull, a hook latch thrown by its lever, a bumper at each end.
///   On an AUTOMATIC door: the controller's speed profile (acceleration, run, check speed into each end),
///   a DC motor and worm gear on a rubber-mounted bracket, a toothed belt to the carriage, and a solenoid
///   lock that lifts before the door moves and drops when it is shut.
/// </summary>
public static class SlidingDoor
{
    public enum Kind { Patio, Automatic }

    /// <summary>One particular door. Width is the moving leaf's.</summary>
    public sealed class Door
    {
        public Kind Kind;
        public float Width = 0.9f, Height = 2.03f;
        public int Variant;
        public int Seed = 1;
        /// <summary>An automatic door in its night mode: the lock lifts before it opens and drops once it is
        /// shut. In the day it is left unlocked and makes no sound.</summary>
        public bool Locking;
    }

    public const double PascalsAtFullScale = 20.0;

    /// <summary>The lab's instrument: when set, each part's pressure alone is written here as PART.raw.</summary>
    public static string? StemFolder;

    public sealed class Report
    {
        public readonly List<string> Events = new();
        public double PeakPascals;
        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var e in Events) sb.AppendLine("    " + e);
            sb.Append($"    peak {20 * Math.Log10(Math.Max(1e-9, PeakPascals) / 2e-5):F1} dB SPL at 1 m");
            return sb.ToString();
        }
    }

    public const int Variants = 4;

    /// <summary>
    /// What wear does to a sliding door, by character: new, standard, worn, old.
    /// Grit: grains per metre of track and their mean size. Roughness: the rail's and the wheel's, RMS (on a
    /// patio door, as a profilometer reports it at the 2.5 mm cut-off).
    /// Flat: how deep a flat the wheel has (a polyurethane tyre left standing under a leaf takes a flat of a
    /// tenth of a millimetre or so). Tyre: nylon, polyurethane, or steel (an old automatic door's polyurethane
    /// replaced with hard nylon). Every patio door rolls on nylon, as the hardware makers fit it on aluminium
    /// track: the old one is the same door grown dirty, pitted and flatted, not steel wheels (on steel, each
    /// grain clicked and rang: Cody's "metallic"). Pile: the weatherstrip's drag, N (a patio door takes 40-50 N to keep moving, nearly all of it
    /// the seals). Roll: rolling and bearing resistance as a share of the load.
    /// Tooth: an automatic door's gear transmission error, m.
    /// </summary>
    private enum Tyre { Nylon, Urethane, Steel }

    private readonly record struct Character(double GritPerMetre, double GritMicron, double RailMicron,
        double WheelMicron, double FlatMicron, Tyre Tyre, double Pile, double Roll, double Tooth);

    private static Character Of(Kind kind, int variant) => (kind, variant % Variants) switch
    {
        (Kind.Patio, 0) => new(0.3, 60, 0.5, 2, 0, Tyre.Nylon, 35, 0.005, 0),
        (Kind.Patio, 1) => new(1, 120, 1.0, 4, 5, Tyre.Nylon, 42, 0.008, 0),
        (Kind.Patio, 2) => new(3, 150, 2.5, 8, 15, Tyre.Nylon, 50, 0.015, 0),
        (Kind.Patio, _) => new(4, 120, 3.0, 8, 30, Tyre.Nylon, 50, 0.025, 0),
        (Kind.Automatic, 0) => new(0.2, 40, 0.3, 1, 5, Tyre.Urethane, 8, 0.004, 3e-6),
        (Kind.Automatic, 1) => new(0.5, 60, 0.6, 2, 12, Tyre.Urethane, 10, 0.006, 6e-6),
        (Kind.Automatic, 2) => new(1, 80, 1.2, 5, 45, Tyre.Urethane, 12, 0.01, 12e-6),
        _ => new(2, 100, 2.0, 6, 10, Tyre.Nylon, 10, 0.015, 20e-6),
    };

    /// <summary>Opening: a patio door's latch thrown and the leaf pulled across its travel in about
    /// <paramref name="travelSeconds"/>; an automatic door's lock lifted and the leaf run open.</summary>
    public static float[] RenderOpen(Door door, int sampleRate, double travelSeconds = 1.4, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        if (door.Kind == Kind.Patio) sim.ScriptPatio(true, travelSeconds); else sim.ScriptAutomatic(true, travelSeconds);
        return sim.Output();
    }

    /// <summary>Shutting: the patio leaf pushed home and its latch thrown; the automatic leaf run shut and
    /// its lock dropped.</summary>
    public static float[] RenderClose(Door door, int sampleRate, double travelSeconds = 1.4, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        if (door.Kind == Kind.Patio) sim.ScriptPatio(false, travelSeconds); else sim.ScriptAutomatic(false, travelSeconds);
        return sim.Output();
    }

    // ── The game ─────────────────────────────────────────────────────────────────────────────────

    public const string KeyPrefix = "slidingdoor:";

    /// <summary>
    /// How long an automatic leaf of this width takes to run open or shut under its controller, seconds:
    /// the server moves the door in this time, so the leaf and its sound arrive together. (The prefab's
    /// 1.5 and 2.5 s were a guess; a real one opens at 0.7 m/s and shuts at the 0.3 m/s the standard allows.)
    /// </summary>
    public static float AutomaticSeconds(float width, bool opening)
    {
        double dt = 1e-3, travel = width;
        double x = opening ? 0 : travel - 0.002, to = opening ? travel - 0.002 : -0.001, u = 0, a = 0, t = 0;
        double dir = Math.Sign(to - x), vRun = opening ? AutoOpenSpeed : AutoCloseSpeed;
        while (t < 30 && !Sim.Controller(opening, to, dir, vRun, dt, ref x, ref u, ref a)) t += dt;
        return (float)t;
    }

    /// <summary>Declared levels, dB at a metre: the model's own LAFmax, by kind and character (new,
    /// standard, worn, old). No calibration: the automatic door runs at 35-55 dBA, where the research puts
    /// real ones (40-55), and a patio door pushed home at a walking pace is a slam of 78 dBA. A patio leaf
    /// rolls at about 73 dB at a metre, 30 dB under the blow when it is pushed home, as the recording's
    /// slides stand under its stops.</summary>
    public static float OpenLevelDb(Kind kind, int variant) => (kind, ((variant % Variants) + Variants) % Variants) switch
    {
        (Kind.Patio, 0) => 70.3f, (Kind.Patio, 1) => 75.0f, (Kind.Patio, 2) => 82.4f, (Kind.Patio, _) => 82.6f,
        (Kind.Automatic, 0) => 53.2f, (Kind.Automatic, 1) => 52.9f, (Kind.Automatic, 2) => 61.4f, _ => 70.1f,
    };
    public static float CloseLevelDb(Kind kind, int variant) => (kind, ((variant % Variants) + Variants) % Variants) switch
    {
        (Kind.Patio, 0) => 80.0f, (Kind.Patio, 1) => 78.7f, (Kind.Patio, 2) => 79.7f, (Kind.Patio, _) => 80.8f,
        (Kind.Automatic, 0) => 35.1f, (Kind.Automatic, 1) => 37.5f, (Kind.Automatic, 2) => 49.4f, _ => 55.9f,
    };

    public static string Key(Kind kind, bool closing, int variant, float travelSeconds, float width, float height)
        => FormattableString.Invariant(
            $"{KeyPrefix}{(kind == Kind.Patio ? "patio" : "auto")}:{(closing ? "close" : "open")}:{((variant % Variants) + Variants) % Variants}:{(int)MathF.Round(travelSeconds * 100f)}:{(int)MathF.Round(width * 100f)}:{(int)MathF.Round(height * 100f)}");

    public static bool TryParseKey(string? key, out bool closing, out Door door, out float travelSeconds)
    {
        closing = false; door = new Door(); travelSeconds = 1.4f;
        if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var p = key.Substring(KeyPrefix.Length).Split(':');
        if (p.Length != 6 || (p[0] != "patio" && p[0] != "auto") || (p[1] != "open" && p[1] != "close")) return false;
        if (!int.TryParse(p[2], out int v) || !int.TryParse(p[3], out int s) || !int.TryParse(p[4], out int w)
            || !int.TryParse(p[5], out int h)) return false;
        closing = p[1] == "close";
        travelSeconds = Math.Clamp(s / 100f, 0.3f, 10f);
        door = new Door
        {
            Kind = p[0] == "patio" ? Kind.Patio : Kind.Automatic, Variant = v, Seed = 1 + v,
            Width = Math.Clamp(w / 100f, 0.5f, 2f), Height = Math.Clamp(h / 100f, 1.5f, 3f),
        };
        return true;
    }

    /// <summary>The sound a key names, peak one.</summary>
    public static float[] RenderKey(string key, int sampleRate)
    {
        if (!TryParseKey(key, out bool closing, out var door, out float travel)) return new float[16];
        float[] pcm = closing ? RenderClose(door, sampleRate, travel) : RenderOpen(door, sampleRate, travel);
        float peak = 1e-9f;
        foreach (float v in pcm) peak = MathF.Max(peak, MathF.Abs(v));
        for (int i = 0; i < pcm.Length; i++) pcm[i] /= peak;
        return pcm;
    }

    /// <summary>How long a key's sound lasts, seconds: its travel and what follows it.</summary>
    public static float Seconds(Kind kind, bool closing, float travelSeconds)
        => kind == Kind.Patio ? travelSeconds + (closing ? 1.45f : 1.15f) : travelSeconds + (closing ? 1.0f : 0.8f);

    // ── Constants, each a property of a part ─────────────────────────────────────────────────────

    private const double G = 9.81;
    private const double AlE = 70e9, AlRho = 2700, GlassE = 70e9, GlassRho = 2500;
    /// <summary>A patio leaf's glass: a sealed unit of two 4 mm panes. An automatic door's: one 6 mm
    /// toughened pane. The frame round it: aluminium sections, 1.5 mm walls, about 0.15 m round.</summary>
    private const double PatioPaneT = 0.004, AutoPaneT = 0.006, FrameWallT = 0.0015, FrameGirth = 0.15;
    private const double PatioFrameKg = 8, AutoFrameKg = 12;
    /// <summary>Rollers: a patio door stands on two tandem assemblies of 1-1/4 in wheels, 50 mm apart, a few
    /// centimetres in from each end; an automatic door hangs from two carriages of 64 mm wheels.</summary>
    private const double PatioWheelR = 0.0159, AutoWheelR = 0.032, PatioWheelInset = 0.08, AutoWheelInset = 0.15;
    private const double PatioTandem = 0.05, AutoTandem = 0.07;
    private const int Wheels = 4;
    /// <summary>The moving part of a roller (wheel, axle, the bracket's free end), and of a carriage.</summary>
    private const double PatioWheelKg = 0.03, AutoWheelKg = 0.4;
    /// <summary>The bracket spring, each assembly's: a steel housing on its adjusting screw; a carriage on its
    /// hanger bolt. Each of an assembly's two wheels has half of it.
    /// The leaf bouncing on its rollers loses a tenth of critical in the roller housings and the glazing
    /// (with the wheel's mass setting it, the leaf hopped on steel wheels for a second after it shut).</summary>
    private const double PatioBracketK = 1e7, AutoBracketK = 5e6, BounceZeta = 0.1;
    /// <summary>Tyres on their rail, Hertz K for a grooved wheel on a crowned rail: nylon on aluminium,
    /// about 6 MN/m of contact stiffness at 100 N; polyurethane on the automatic door's carriages; and an old
    /// door's steel wheels on a stainless cap, about 70 MN/m.</summary>
    private const double NylonK = 5.7e8, UrethaneK = 1.2e7, SteelOnAlK = 3e10;
    private const double NylonLambda = 0.5, UrethaneLambda = 2.0, SteelLambda = 0.1;
    /// <summary>Where a patio roller bears, as what averages the rail's roughness: its 1/8 in concave groove,
    /// worn in, all but conforming to the rib's crown (3.1 mm), so Hertz's contact is an ellipse long across
    /// the rail and short along it. Rollers are moulded in glass-filled nylon, E* about 8.5 GPa on aluminium
    /// (steel on aluminium about 58). A 1-1/4 in wheel under a quarter of the leaf bears over about 0.75 mm
    /// along the rail and 2.5 mm across. (Taken as a ball's round patch in plain nylon, 1.4 mm long, it
    /// swallowed everything above 700 Hz, and the slide lost its middle.)</summary>
    private const double GrooveR = 0.0032, CrownR = 0.0031, NylonEStar = 8.5e9, SteelOnAlEStar = 5.8e10;
    /// <summary>The strip's edges are not knife edges: where contact begins is spread by the tread's own
    /// texture, asperities a few tens of microns across, so the averaging is blurred over about 30 um and
    /// passes nothing much shorter than a tenth of a millimetre. (With sharp edges the patch passed a tail of
    /// roughness to 8 kHz, where the sill radiates best, 5 dB over the recording there.)</summary>
    private const double ContactEdge = 32e-6;
    /// <summary>The track: the patio sill, aluminium 1.8 mm, about 0.12 m developed; the automatic door's
    /// header, 2.5 mm, 0.45 m round. Both bedded or bolted: they lose more than a bare sheet.</summary>
    private const double SillT = 0.0018, SillGirth = 0.12, HeaderT = 0.0025, HeaderGirth = 0.45, BeddedLoss = 0.01;
    /// <summary>A patio sill lies in a bead of sealant on the floor. Where its walls bend slowly they work the
    /// bead, as the glass works its gaskets: about 12 / f more loss, 0.1 at 125 Hz and little in
    /// the kilohertz. (With a bare sheet's loss its lowest modes rang the roll at 100-160 Hz, 5 dB over the
    /// recording.)</summary>
    private const double SillBead = 12;
    /// <summary>The header is not a sheet 0.45 m by 2 m: it is a box of walls 0.1-0.15 m wide screwed to the
    /// wall along its length, under a cover, so its walls' modes start near 300 Hz (a 0.15 m strip of 2.5 mm
    /// aluminium), and the cover, its screws and the drive's brackets take about 0.03. (As one big sheet
    /// from 60 Hz with a bare sheet's loss it boomed: Cody, "a metallic tube".)</summary>
    private const double HeaderFromHz = 300, HeaderLoss = 0.03;
    private const double PortStiffness = 2e7;
    /// <summary>Where a wheel bears: the rail's crown and web, about a gram, on the sill bedded below it
    /// (about 5e6 N/m under a wheel's footprint), passing into the sill plate through its impedance. Where a
    /// bracket bears: a couple of grams of the frame's bottom rail, on the rest of the leaf. (A 7 g patch on a
    /// stiff spring rang at 8.5 kHz and made the roll a hiss up there.) A polyurethane wheel's contact is
    /// several millimetres long and bears on about 6 g of the header's track.</summary>
    private const double RailPatchKg = 0.001, UrethaneRailPatchKg = 0.006, RailBedding = 5e6, BracketPatchKg = 0.002, BracketBacking = 1e7;
    /// <summary>The pull handle's escutcheon: 30 g of zinc on two screws, about 1.3 kHz, rung by the handle and
    /// radiating from its own 40 cm2 as well as through the stile.</summary>
    private const double EscutcheonKg = 0.03, EscutcheonK = 2e6, EscutcheonArea = 0.004;
    /// <summary>Glass sits in the frame on rubber glazing gaskets: the frame's blows reach it through about
    /// 2e5 N/m at each bracket.</summary>
    private const double GasketK = 2e5;
    /// <summary>A patio leaf's sealed unit does not hang in its frame: it stands on two setting blocks on the
    /// bottom rail, at the quarter points, neoprene of about 85 Shore A, 100 mm long under the unit's 24 mm
    /// edge and 6 mm thick: about 4 MN/m each, losing a fifth of critical twice over (loss factor 0.2). So the
    /// leaf bounces on its rollers as two bodies, the 8 kg frame on the tyres and the 30 kg of glass on the
    /// blocks, not as one 38 kg mass. (As one, it rang at 80-160 Hz under every roll, 6 dB over the
    /// recording.)</summary>
    private const double BlockK = 4e6, BlockZeta = 0.1;
    /// <summary>Grit: a tyre already sinks into its contact under the leaf's weight (nylon 40-55 um, steel
    /// 4-5 um, polyurethane more than half a millimetre), and it wraps round a grain smaller than that: only
    /// what stands above the sink lifts the wheel. A grain that does carries the wheel crushes at about
    /// 150 MPa over its own section, in a few steps of tens of microseconds each.</summary>
    private const double GritStrength = 150e6, CrushSeconds = 2e-5, PowderMicron = 3;
    /// <summary>A patio tyre meets a grain at an angle whose tangent is sqrt(2 a / R); steeper than the grip of
    /// nylon on sand (about 0.3), the wheel shoves the grain along and off the rib instead of climbing it.
    /// On a 1-1/4 in wheel nothing over about 0.7 mm is climbed. (Climbed and crushed, a 1 mm grain from the
    /// tail of the sizes struck the rail with 700 N and rang the sill at 127 dB.)</summary>
    private const double GritGrip = 0.3;
    /// <summary>Under a patio door's nylon tyre a grain does not shatter: nylon yields at about 80 MPa, long
    /// before quartz cracks, so a grain pressed harder than that sinks into the tread, the nylon flowing round
    /// it over a fraction of a millisecond, and the load stays on. (Shattered in 20 us steps, a half-millimetre
    /// grain dropped a wheel carrying 300 N in a blink and the rail rang at 117 dB.)</summary>
    private const double NylonYield = 80e6, EmbedSeconds = 3e-4;
    /// <summary>Pile weatherstrip: its fibres slip about their own width, 150 um, each on its own. A patio leaf
    /// drags about 5 m of pile at some 500 fibres a centimetre; an automatic leaf about 2 m. The drag is the
    /// sum of their sawtooth forces, so it flutters by about 0.3 / sqrt(fibres) of itself. (Half its mean,
    /// as if every fibre slipped together, made the frame hiss at 94 dBA.)</summary>
    private const double PileSlip = 1.5e-4, PatioPileFibres = 250000, AutoPileFibres = 100000, AutoBrushShare = 0.2;
    /// <summary>Bumpers: a patio leaf's stile meets a vinyl bulb flattened on aluminium at each end of its
    /// travel; an automatic leaf meets rubber on the jamb. Hertz, stiff enough that a stile's own mass
    /// stops in a few milliseconds.</summary>
    private const double PatioBumperK = 5e7, AutoBumperK = 1e7, BumperLambda = 0.6;
    /// <summary>A patio jamb's bumper is a hollow vinyl bulb in pile, and a leaf comes back off it with 0.1-0.5
    /// of its speed: Hunt-Crossley loss of about 3 s/m. (At 0.6 the leaf bounced off at three quarters of
    /// its speed and rolled back 43 mm: Cody, "the slide still plays after the door is shut".) Shutting, the
    /// hand stays on the pull through the blow and holds the leaf home with about 80 N, more than the seals'
    /// drag, while the latch is thrown; the arm behind it gives about 300 N s/m.</summary>
    private const double PatioJambLambda = 3, HoldHome = 80, ArmDamping = 300;
    /// <summary>The end of a leaf that strikes: the stile and the glass edge near it, held to the rest of the
    /// leaf through the frame's corners and the glass in its gaskets, about 200 Hz. The stile stops first
    /// and the rest of the leaf arrives through that spring: two to five knocks in the first tenth of a
    /// second, and a dark one. (A rigid 40 kg leaf on its bumper was a 40 ms push with nothing in it.)
    /// The spring is the frame's crimped corners and the glass in its gaskets, which lose a fifth of critical:
    /// at 0.05 the leaf came back off its stile as off a spring.</summary>
    private const double PatioStileKg = 1.5, AutoStileKg = 2.5, StileHz = 200, StileZeta = 0.2;
    /// <summary>A sealed unit's panes on the air between them (4-16-4): they move against each other at about
    /// 200 Hz and ring for half a second. It is the note of a patio door hitting home.</summary>
    private const double UnitGap = 0.016, UnitLoss = 0.02;
    /// <summary>The glass is not on the line the stile pushes along: about 5 mm off it over a 50 mm edge, so a
    /// tenth of the stile's load bends the pane and the frame. That is what a leaf hitting home is heard by.</summary>
    private const double EdgeEccentricity = 0.1;
    /// <summary>The patio door's hook latch: a 15 g steel hook thrown 8 mm by its lever's over-centre spring
    /// (about 0.5 N past the centre), onto the nylon of its housing at each end.</summary>
    private const double HookKg = 0.015, HookThrow = 0.008, HookSnap = 0.5, HookVolume = 2e-6;
    private const double HookStopK = 2e8, HookStopLambda = 2;
    /// <summary>The hand is still on the lever as the hook seats: the hook moves with the lever and the hand's
    /// grip, about 3 N s/m. (Free, it bounced a dozen times on its stop.)</summary>
    private const double HookDrag = 3;
    /// <summary>The pull handle: 100 g of zinc on its spindle with a third of a millimetre of play each way
    /// along the travel, centred by its spring, knocking on its escutcheon. When the leaf stops dead it is
    /// thrown across its play: the bright part of a patio door hitting home. Held while a hand is on it.</summary>
    private const double HandleKg = 0.1, HandlePlay = 0.0003, HandleCentring = 2e3, HandleZeta = 0.05, HandleVolume = 1.4e-5;
    private const double MetalK = 1e9, MetalLambda = 0.3;
    /// <summary>The automatic door's lock: a 30 g solenoid plunger lifted 8 mm by about 25 N, and dropped
    /// back into the carriage's bracket by a 6 N spring.</summary>
    private const double PlungerKg = 0.03, PlungerThrow = 0.008, PlungerPull = 25, PlungerSpring = 6;
    /// <summary>Drive (a dormakaba ES 200's): an 8 mm pitch belt over a 24-tooth pulley (61 mm), a two-start
    /// worm at 15:1, a 12-segment brushed motor near 3350 rpm at full speed. The belt between motor and
    /// carriage is about 1e5 N/m.</summary>
    private const double BeltPitch = 0.008, PulleyD = 0.061, GearRatio = 15, WormStarts = 2, MotorSlots = 12, BeltK = 1e5, BeltZeta = 0.3;
    /// <summary>The worm's mesh: about 2e6 N/m in plastic, so a few microns of transmission error is
    /// newtons between the worm and its wheel. Those forces stay inside the gearbox and ring its housing (a
    /// small cast box, its walls from about 1.2 kHz); what leaves it is the belt's pull, which the error
    /// moves by as much as it moves the wheel's rim (a 30 mm wheel under a 30 mm pulley). The motor and
    /// gearbox (1.5 kg) sit on rubber on the header, about 60 Hz.</summary>
    private const double MeshK = 2e6, WormWheelR = 0.03, DriveKg = 1.5, DriveMountHz = 60, DriveMountZeta = 0.15;
    /// <summary>The motor: a 63 mm steel can, 1.5 mm, pulled out of round by its magnets' torque ripple at the
    /// slot rate (a tenth of its torque) and knocked by its brushes crossing the commutator's bars (a 2 g
    /// brush lifted by the step between bars, 5 um, at the rotor's surface speed, landing over about 0.2 ms).
    /// The can and the gearbox are full of magnets, laminations and grease, bolted to brackets on rubber:
    /// their walls lose about a tenth. (At 0.01 they rang at their own pitches, "too much resonance", and
    /// the brushes' friction as white noise was the hiss.) The header's aluminium cover over the drive passes less the higher it goes, by
    /// the mass law: 6 dB an octave above about 800 Hz.</summary>
    private const double CanR = 0.0315, CanT = 0.0015, BrushKg = 0.002, BarStep = 5e-6, BarGap = 5e-4;
    private const double BrushLanding = 2e-4, DriveHousingLoss = 0.1, Cover = 0.5, CoverHz = 800;
    /// <summary>The belt is clamped to the carriage by its teeth in a toothed clamp, with about 0.8 mm of play.
    /// When the belt starts or stops pulling, the leaf swings on the belt's spring (about 6 Hz) and the clamp
    /// knocks across its play each half swing: the train of knocks 60-90 ms apart heard as a real one starts.
    /// It is the belt's polyurethane teeth that land, about 5 MN/m and lossy, not steel on steel.</summary>
    private const double ClampPlay = 0.0008, ClampKg = 0.1, LugK = 2.4e8, LugLambda = 1.0, ClampVolume = 1.3e-5;
    /// <summary>A shop front's leaf: aluminium stiles and rails with the glass in rubber gaskets and brush seals
    /// along its edges, losing about 0.03.</summary>
    private const double StorefrontLoss = 0.03;
    /// <summary>A belt tooth seats in the idler's groove at the far end of the header each pitch: about a gram
    /// of tooth arriving at a twentieth of the belt's speed (the teeth are rounded and crowned to roll in),
    /// bolted straight to the header. Each tooth's pitch and the belt's tension vary by a few per cent, so
    /// no two seat alike. (At a fifth of the speed, all alike, they were a 37 Hz pulse train.)</summary>
    private const double ToothSeat = 3e-4;
    private const double BeltToothKg = 0.001, ToothSeatShare = 0.05, ToothPitchJitter = 0.03, ToothLevelJitter = 0.4;
    /// <summary>Speeds: open at 0.7 m/s (a common default), shut at 0.3 (ANSI/BHMA A156.10 allows no more for
    /// a leaf under 71 kg), slowing for the last 60 mm to 0.08 m/s opening and a 0.04 m/s creep shutting
    /// (the standard asks for the slow-down at least 51 mm out), 0.8 m/s^2 either side.</summary>
    private const double AutoOpenSpeed = 0.7, AutoCloseSpeed = 0.3, OpenCheckSpeed = 0.08, CloseCheckSpeed = 0.04, CheckZone = 0.06, AutoAccel = 0.8;
    /// <summary>The controller ramps its acceleration over about a fifth of a second, m/s^3.</summary>
    private const double AutoJerk = 4;

    /// <summary>A patio door's rolling surfaces (the aluminium rail with its film of dirt, and the moulded tyre)
    /// are rough at every wavelength, the long ones most: each octave of wavelength holds RMS height in
    /// proportion to the fourth root of the wavelength (a height spectrum falling as wavenumber to the 1.5,
    /// as worn moulded and extruded surfaces with a film of dirt measure), from 10 mm down. Longer than that is the tyre's out-of-round and
    /// the sill's lie on the floor, which the leaf rides slowly on its springs. (Carried on to the tyre's whole
    /// circumference, the worn tyres' waviness met the leaf's bounce near 100 Hz and the wheels hopped off the
    /// rail two thousand times a slide. The round 2 profiles were noise smoothed on a 3 mm grain and drawn
    /// straight between points every 0.4 mm: every point a kink, and the roll came out flat to 8 kHz.)</summary>
    private const double RoughnessSlope = 0.25, RoughnessLongest = 0.01;
    /// <summary>A character's rail and tyre roughness is RMS as a profilometer reports it, at the standard
    /// 2.5 mm cut-off; the same spectrum carries on above it as waviness, to 10 mm.</summary>
    private const double RoughnessCutoff = 0.0025;
    /// <summary>A patio door's pile: polypropylene fibres about 7 mm tall and 0.1 mm thick. A fibre is a
    /// cantilever on the backing, its first mode near 370 Hz; its tip catching and slipping reaches the frame
    /// through that, so the drag's flutter is felt as a resonance there falling 12 dB an octave above, the
    /// fibres rubbing their neighbours for about a third of critical damping. (Pushed straight into the
    /// metal, its 5-10 kHz slip rate made an 8 kHz hiss over the roll.)</summary>
    private const double PileHeight = 0.007, PileFibreD = 1.0e-4, PpE = 1.5e9, PpRho = 900, PileZeta = 0.3;

    private sealed class Grain { public double X, A, Target, LastCrush = -1; public bool Done; }

    /// <summary>
    /// A rolling surface as a roller feels it, <paramref name="n"/> heights round <paramref name="length"/>
    /// (a power of two, wrapping). The surface's own roughness, RMS <paramref name="rms"/>, at every
    /// wavelength from <paramref name="longest"/> down to a couple of steps, each octave as
    /// <see cref="RoughnessSlope"/> says, and any <paramref name="feature"/> (a flat); all of it felt through
    /// the contact. The groove meets the crowned rib in an ellipse, <paramref name="halfPatch"/> long along the
    /// rail at its middle and <paramref name="halfWidth"/> across: each strip of it along the rail averages
    /// what passes under it by its own Hertz pressure, a half ellipse, which passes a wavelength by
    /// 2 J1(ka) / ka (everything longer than the strip, little shorter), and the strips are shorter towards
    /// the ellipse's sides and carry less. Roughness much longer than the width is the same under every strip;
    /// shorter, each strip rides its own, coherent with the middle's as exp(-k y), so what the wheel feels of
    /// it is their average. So a tyre rolls over a flat smaller than its patch as a dip, and the roughness it
    /// hears falls smoothly away above the speed over the patch length: at a walking slide, above a few
    /// hundred hertz. (One strip of one length has true nulls, and they swept up and down with the leaf's
    /// speed as arches of comb in the spectrum, nothing like the recording.)
    /// </summary>
    private static double[] RollingProfile(Random rng, int n, double length, double rms, double longest, double halfPatch,
                                           double halfWidth, Func<double, double>? feature)
    {
        const int strips = 9;
        var stripA = new double[strips]; var stripW = new double[strips]; var stripY = new double[strips];
        double wSum = 0;
        for (int j = 0; j < strips; j++)
        {
            double u = -1 + (j + 0.5) * 2.0 / strips;
            stripY[j] = Math.Abs(u) * halfWidth;
            stripA[j] = halfPatch * Math.Sqrt(1 - u * u);
            stripW[j] = 1 - u * u;
            wSum += stripW[j];
        }
        for (int j = 0; j < strips; j++) stripW[j] /= wSum;
        // The roughness's own spectrum, scaled to its RMS at the cut-off.
        var amp = new double[n / 2];
        double rough2 = 0;
        for (int b = 1; b < n / 2; b++)
        {
            double lambda = length / b;
            if (lambda > longest * 1.0001) continue;
            // Each octave holds bins in proportion to wavenumber: a bin's amplitude goes as k^-(slope + 1/2).
            amp[b] = Math.Pow(lambda, RoughnessSlope + 0.5);
            if (lambda <= RoughnessCutoff) rough2 += 2 * amp[b] * amp[b];
        }
        double scale = rms / Math.Max(Math.Sqrt(rough2), 1e-30);
        // The feature's spectrum.
        var fr = new double[n]; var fi = new double[n];
        if (feature != null)
        {
            for (int i = 0; i < n; i++) fr[i] = feature(i * length / n) / n;
            Fft(fr, fi, inverse: false);
        }
        var re = new double[n]; var im = new double[n];
        for (int b = 1; b < n / 2; b++)
        {
            double k = 2 * Math.PI * b / length;
            double kEdge = k * ContactEdge, edge = Math.Exp(-kEdge * kEdge / 2);
            double ph0 = rng.NextDouble() * 2 * Math.PI;
            double sr = 0, si = 0, mean = 0;
            for (int j = 0; j < strips; j++)
            {
                double ka = k * stripA[j];
                double h = stripW[j] * (ka < 1e-9 ? 1 : 2 * BesselJ1(ka) / ka) * edge;
                mean += h;
                double rho = Math.Exp(-k * stripY[j]), own = Math.Sqrt(1 - rho * rho);
                double phj = rng.NextDouble() * 2 * Math.PI;
                sr += h * (rho * Math.Cos(ph0) + own * Math.Cos(phj));
                si += h * (rho * Math.Sin(ph0) + own * Math.Sin(phj));
            }
            re[b] = scale * amp[b] * sr + mean * fr[b];
            im[b] = scale * amp[b] * si + mean * fi[b];
            re[n - b] = re[b]; im[n - b] = -im[b];
        }
        re[0] = fr[0];
        Fft(re, im, inverse: true);
        return re;
    }

    /// <summary>In place, radix 2; the inverse unscaled.</summary>
    private static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = (inverse ? 2 : -2) * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int p = i + k, q = p + len / 2;
                    double tr = re[q] * cr - im[q] * ci, ti = re[q] * ci + im[q] * cr;
                    re[q] = re[p] - tr; im[q] = im[p] - ti; re[p] += tr; im[p] += ti;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    /// <summary>The Bessel function J1 (the usual rational and asymptotic fits, to about 1e-8).</summary>
    private static double BesselJ1(double x)
    {
        double ax = Math.Abs(x);
        if (ax < 8)
        {
            double y = x * x;
            double a = x * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1 + y * (-2972611.439 + y * (15704.48260 + y * -30.16036606)))));
            double b = 144725228442.0 + y * (2300535178.0 + y * (18583304.74 + y * (99447.43394 + y * (376.9991397 + y))));
            return a / b;
        }
        double z = 8 / ax, y2 = z * z, xx = ax - 2.356194491;
        double p = 1 + y2 * (0.183105e-2 + y2 * (-0.3516396496e-4 + y2 * (0.2457520174e-5 + y2 * -0.240337019e-6)));
        double q = 0.04687499995 + y2 * (-0.2002690873e-3 + y2 * (0.8449199096e-5 + y2 * (-0.88228987e-6 + y2 * 0.105787412e-6)));
        double r = Math.Sqrt(0.636619772 / ax) * (Math.Cos(xx) * p - z * Math.Sin(xx) * q);
        return x < 0 ? -r : r;
    }

    private sealed class Sim
    {
        private readonly Door door;
        private readonly Report? report;
        private readonly Random rng;
        private readonly int rate;
        private readonly double dt;
        private readonly Character ch;
        private readonly bool auto;
        private readonly List<float> outHi = new();
        private readonly double[] peaks = new double[PeakNames.Length];
        private static readonly string[] PeakNames = { "track", "frame", "glass", "latch", "drive" };
        private List<float>[]? stems;
        private double time;
        private bool recording;

        // Leaf.
        private readonly double mass, pitchInertia, travel, width, height;
        private double x, u, heave, heaveRate, pitch, pitchRate, lastLeafAcc;
        // A patio leaf's glass on its setting blocks: its own heave and pitch.
        private readonly double frameKg, frameInertia, glassKg, glassInertia, blockArm, blockC, blockPreload;
        private double gHeave, gHeaveRate, gPitch, gPitchRate;
        // Rollers.
        private readonly double wheelR, wheelKg, bracketK, bracketC, contactK, contactLambda;
        private readonly double[] arm = new double[Wheels], zw = new double[Wheels], vw = new double[Wheels], lastH = new double[Wheels];
        private readonly double[][] wheelRough = new double[Wheels][];
        private readonly double[] railRough;
        private readonly double railStep, wheelStep;
        private readonly List<Grain> grit = new();
        private double maxReach, sink, restLoad;
        // Fields and ports.
        private readonly DenseField trackField, frameField, glassField;
        private readonly Port[] trackPort = new Port[Wheels], framePort = new Port[Wheels], glassPort = new Port[Wheels];
        private readonly double[][] trackHit = new double[Wheels][], frameHit = new double[Wheels][], glassHit = new double[Wheels][];
        private readonly Modes? unit;
        private readonly double unitGain;
        private readonly double[] brushTrackHit, brushFrameHit, endTrackHit, endFrameHit, smallHit, driveHit;
        private readonly Port endPort, smallPort;
        private double brushNoise, pileLow, fibreBend, fibreRate;
        private readonly double fibreW;
        // Bumpers at each end of the travel.
        private readonly double bumperK, stileKg, stileK, stileC;
        private readonly double[] stileD = new double[2], stileV = new double[2];
        private readonly double[][] glassEdge = new double[2][], frameEdge = new double[2][];
        private static readonly double[] ones = { 1.0 };
        // Hand (patio).
        private double handX, handV, handA;
        private bool handOn, handReleased, holdingHome;
        // Hook latch (patio) or solenoid plunger (automatic): a small steel part between two stops.
        private double small, smallRate, smallForce;
        private readonly double smallKg, smallThrow;
        private readonly AccelerationNoise smallNoise, handleNoise;
        private double handle, handleRate;
        private readonly Port handlePort;
        private readonly SmallRadiator escutcheonSound;
        private readonly double[] handleHit;
        // Drive (automatic).
        private double motorA, motorX, motorU, beltPhase, meshPhase, motorPhase;
        private readonly Mount drive;
        private readonly double[] toothError = new double[(int)(GearRatio * WormStarts)];
        private readonly double[] profilePhase = new double[10];
        private readonly Modes gearbox, motorCan;
        private readonly double[] gearShape, canShape;
        private readonly double[] idlerHit;
        private double brushLeft, brushPeak, lastBar, coverLow, clamp, clampRate;
        private double beltRun, nextTooth, seatLeft, seatPeak;
        private readonly AccelerationNoise clampNoise;
        private readonly double[] lugHit;
        private readonly HighPass lugHigh;
        private readonly Port lugPort;
        private readonly Dictionary<string, (double Start, double Peak, bool On)> contactLog = new();

        public Sim(Door door, int sampleRate, Report? report)
        {
            this.door = door; this.report = report;
            rate = sampleRate * Oversample; dt = 1.0 / rate;
            rng = new Random(door.Seed);
            auto = door.Kind == Kind.Automatic;
            ch = Of(door.Kind, door.Variant);
            width = door.Width; height = door.Height;
            travel = auto ? width : width - 0.05;

            double paneT = auto ? AutoPaneT : PatioPaneT;
            double glassArea = (width - 0.1) * (height - 0.15);
            mass = glassArea * paneT * GlassRho * (auto ? 1 : 2) + (auto ? AutoFrameKg : PatioFrameKg);
            pitchInertia = mass * (width * width + height * height) / 12;
            glassKg = auto ? 0 : glassArea * paneT * GlassRho * 2;
            frameKg = mass - glassKg;
            glassInertia = glassKg * ((width - 0.1) * (width - 0.1) + (height - 0.15) * (height - 0.15)) / 12;
            frameInertia = pitchInertia - glassInertia;
            blockArm = (width - 0.1) / 4;
            blockC = auto ? 0 : 2 * BlockZeta * Math.Sqrt(BlockK * glassKg / 2);
            blockPreload = auto ? 0 : glassKg * G / 2 / BlockK;

            wheelR = auto ? AutoWheelR : PatioWheelR;
            wheelKg = auto ? AutoWheelKg : PatioWheelKg;
            bracketK = auto ? AutoBracketK : PatioBracketK;
            bracketC = 2 * BounceZeta * Math.Sqrt(bracketK * mass / 2);
            contactK = ch.Tyre switch { Tyre.Steel => SteelOnAlK, Tyre.Urethane => UrethaneK, _ => NylonK };
            contactLambda = ch.Tyre switch { Tyre.Steel => SteelLambda, Tyre.Urethane => UrethaneLambda, _ => NylonLambda };
            double inset = auto ? AutoWheelInset : PatioWheelInset;
            double tandem = auto ? AutoTandem : PatioTandem;
            for (int i = 0; i < Wheels; i++)
                arm[i] = (i < 2 ? -1 : 1) * (width / 2 - inset) + (i % 2 == 0 ? -0.5 : 0.5) * tandem;

            // Surfaces. On an automatic door, the rail's roughness on a 3 mm grain (a wheel's contact patch
            // filters out finer), the wheel's on its circumference; both RMS as the character says.
            double trackLen = travel + width + 0.2;
            // A wheel cannot feel roughness shorter than its contact patch: the automatic door's profiles are
            // grained at no less than twice the patch's length under the static load.
            double load0 = mass * G / Wheels + wheelKg * G;
            sink = Math.Pow(load0 / contactK, 2.0 / 3);
            restLoad = load0;
            double patch = 2 * Math.Sqrt(wheelR * sink);
            double circ = 2 * Math.PI * wheelR;
            if (auto)
            {
                railRough = SurfaceProfile(rng, trackLen, Math.Max(0.003, 2 * patch));
                for (int i = 0; i < railRough.Length; i++) railRough[i] *= ch.RailMicron * 1e-6;
                for (int i = 0; i < Wheels; i++)
                {
                    var w = SurfaceProfile(rng, circ, Math.Max(0.002, 2 * patch));
                    double flat = ch.FlatMicron * 1e-6 * (0.6 + 0.8 * rng.NextDouble());
                    double flatAt = rng.NextDouble() * circ, half = Math.Sqrt(2 * wheelR * Math.Max(flat, 1e-12));
                    for (int k = 0; k < w.Length; k++)
                    {
                        double s = k * circ / w.Length;
                        w[k] *= ch.WheelMicron * 1e-6;
                        double d = Math.Abs(s - flatAt); d = Math.Min(d, circ - d);
                        if (d < half) w[k] -= flat - d * d / (2 * wheelR);
                    }
                    wheelRough[i] = w;
                }
            }
            else
            {
                // A patio door's rail and tyres as surfaces with roughness at every wavelength, felt through
                // the Hertz contact patch (see RollingProfile).
                // Hertz's ellipse: relative curvatures along (the wheel) and across (the crown in the groove), the
                // equivalent radius and the ellipse's mean semi-axis c, then its axes by the curvature ratio.
                double along = 1 / (2 * wheelR), across = (1 / CrownR - 1 / GrooveR) / 2;
                double re = 1 / (2 * Math.Sqrt(along * across));
                double c = Math.Cbrt(3 * load0 * re / (4 * (ch.Tyre == Tyre.Steel ? SteelOnAlEStar : NylonEStar)));
                double aspect = Math.Pow(along / across, 2.0 / 3);
                double halfPatch = c / Math.Sqrt(aspect), halfWidth = c * Math.Sqrt(aspect);
                railRough = RollingProfile(rng, 65536, trackLen, ch.RailMicron * 1e-6, RoughnessLongest, halfPatch, halfWidth, null);
                for (int i = 0; i < Wheels; i++)
                {
                    double flat = ch.FlatMicron * 1e-6 * (0.6 + 0.8 * rng.NextDouble());
                    double flatAt = rng.NextDouble() * circ, half = Math.Sqrt(2 * wheelR * Math.Max(flat, 1e-12));
                    wheelRough[i] = RollingProfile(rng, 4096, circ, ch.WheelMicron * 1e-6, RoughnessLongest, halfPatch, halfWidth, s =>
                    {
                        double d = Math.Abs(s - flatAt); d = Math.Min(d, circ - d);
                        return d < half ? -(flat - d * d / (2 * wheelR)) : 0;
                    });
                }
            }
            railStep = trackLen / railRough.Length;
            wheelStep = circ / wheelRough[0].Length;
            // Grit along the track: a Poisson scatter, sizes exponential about the mean.
            for (double gx = 0; ; )
            {
                gx += -Math.Log(1 - rng.NextDouble()) / Math.Max(ch.GritPerMetre, 1e-6);
                if (gx > trackLen) break;
                double a = -Math.Log(1 - rng.NextDouble()) * ch.GritMicron * 1e-6;
                bool shoved = !auto && Math.Sqrt(2 * a / wheelR) > GritGrip;
                if (a > PowderMicron * 1e-6 && !shoved) grit.Add(new Grain { X = gx - 0.1, A = a, Target = a });
                maxReach = Math.Max(maxReach, Math.Sqrt(2 * wheelR * a));
            }

            // Fields.
            trackField = auto
                ? new DenseField(HeaderGirth, trackLen, HeaderT, AlE, AlRho, Poisson, f => ThinPanelLoss(f) + HeaderLoss, HeaderFromHz, 16000, rng, dt)
                : new DenseField(SillGirth, trackLen, SillT, AlE, AlRho, Poisson, f => ThinPanelLoss(f) + BeddedLoss + SillBead / f, 60, 16000, rng, dt);
            // A shop front's stiles and rails are tubes with faces 50-100 mm wide: their walls ring from about
            // 300 Hz, as the header's do.
            frameField = new DenseField(FrameGirth, 2 * (width + height), FrameWallT, AlE, AlRho, Poisson,
                                        f => ThinPanelLoss(f) + (auto ? StorefrontLoss : 0.005), auto ? HeaderFromHz : 60, 16000, rng, dt);
            // Glass in its gaskets: glass itself barely loses (0.002); the gaskets and the unit's edge seal
            // take about 0.02, and more low down, where a pane's edges move most (0.1 at 150 Hz).
            glassField = new DenseField(width - 0.1, height - 0.15, paneT, GlassE, GlassRho, 0.22,
                                        f => 0.02 + 12 / f, 40, 16000, rng, dt);
            for (int i = 0; i < Wheels; i++)
            {
                trackPort[i] = new Port(ch.Tyre == Tyre.Urethane ? UrethaneRailPatchKg : RailPatchKg, RailBedding, trackField.Impedance);
                framePort[i] = new Port(BracketPatchKg, BracketBacking, frameField.Impedance);
                glassPort[i] = new Port(glassField.PatchMass, GasketK, glassField.Impedance);
                trackHit[i] = trackField.Point(); frameHit[i] = frameField.Point(); glassHit[i] = glassField.Point();
            }
            brushTrackHit = trackField.Point(); brushFrameHit = frameField.Point();
            endTrackHit = trackField.Point(); endFrameHit = frameField.Point();
            endPort = new Port(frameField.PatchMass, PortStiffness, frameField.Impedance);
            smallHit = auto ? trackField.Point() : frameField.Point();
            smallPort = auto ? new Port(trackField.PatchMass, PortStiffness, trackField.Impedance)
                             : new Port(frameField.PatchMass, PortStiffness, frameField.Impedance);
            driveHit = trackField.Point();
            bumperK = auto ? AutoBumperK : PatioBumperK;
            stileKg = auto ? AutoStileKg : PatioStileKg;
            stileK = stileKg * Math.Pow(2 * Math.PI * StileHz, 2);
            stileC = 2 * StileZeta * Math.Sqrt(stileK * stileKg);
            for (int k = 0; k < 2; k++) { glassEdge[k] = glassField.Point(); frameEdge[k] = frameField.Point(); }
            if (!auto)
            {
                // The sealed unit's breathing mode: two panes on the air spring between them.
                double paneKgPerM2 = PatioPaneT * GlassRho;
                double hz = Math.Sqrt(Rho0 * C0 * C0 / UnitGap * 2 / paneKgPerM2) / (2 * Math.PI);
                double paneKg = paneKgPerM2 * glassArea;
                unit = new Modes(new[] { hz }, new[] { UnitLoss }, new[] { paneKg / 4 }, new[] { 1.0 }, dt);
                unitGain = SmallPlateGain(glassArea, 4 / (Math.PI * Math.PI));
            }
            smallKg = auto ? PlungerKg : HookKg;
            smallThrow = auto ? PlungerThrow : HookThrow;
            smallNoise = new AccelerationNoise(auto ? PlungerKg / 7850 : HookVolume, dt);
            handleNoise = new AccelerationNoise(HandleVolume, dt);
            handlePort = new Port(EscutcheonKg, EscutcheonK, frameField.Impedance);
            escutcheonSound = new SmallRadiator(EscutcheonArea, dt);
            handleHit = frameField.Point();
            double kd = DriveKg * Math.Pow(2 * Math.PI * DriveMountHz, 2);
            drive = new Mount(DriveKg, kd, DriveMountZeta);
            // The worm's error is mostly its tooth profile, the same every tooth: a mesh-rate whine and its
            // harmonics falling as 1/h. Each tooth's own spacing error is a tenth of that. (Random teeth at a
            // third rang the housing's own modes instead of a whine.)
            for (int i = 0; i < toothError.Length; i++) toothError[i] = (rng.NextDouble() * 2 - 1) * 0.1;
            for (int h = 0; h < profilePhase.Length; h++) profilePhase[h] = rng.NextDouble() * 2 * Math.PI;
            idlerHit = trackField.Point();
            lugHit = frameField.Point();
            clampNoise = new AccelerationNoise(ClampVolume, dt);
            lugHigh = new HighPass(300, rate);
            lugPort = new Port(BracketPatchKg, BracketBacking, frameField.Impedance);
            // The gearbox housing's walls: sixteen modes from 1.2 kHz, 50 g each, radiating from about 100 cm2.
            var gh = new List<double>(); var gl = new List<double>(); var gm = new List<double>(); var gg = new List<double>();
            for (int i = 0; i < 16; i++)
            {
                gh.Add(1200 * Math.Pow(6, i / 15.0) * (0.95 + 0.1 * rng.NextDouble()));
                gl.Add(DriveHousingLoss); gm.Add(0.05); gg.Add(SmallPlateGain(0.01, 0.3) * (rng.NextDouble() < 0.5 ? -1 : 1));
            }
            gearbox = new Modes(gh, gl, gm, gg, dt);
            gearShape = new double[gh.Count];
            for (int i = 0; i < gearShape.Length; i++) gearShape[i] = (rng.NextDouble() * 2 - 1) * Math.Sqrt(3);
            // The motor's can: its ring modes n = 2 to 6, each with a longer and a shorter axial version.
            var ch2 = new List<double>(); var cl = new List<double>(); var cm = new List<double>(); var cg = new List<double>();
            for (int n = 2; n <= 6; n++)
                foreach (double axial in new[] { 1.0, 1.25 })
                {
                    ch2.Add(Ring(CanR, CanT, 7850, 200e9, n) * axial);
                    cl.Add(DriveHousingLoss); cm.Add(0.05); cg.Add(SmallPlateGain(0.02, 0.2) * (rng.NextDouble() < 0.5 ? -1 : 1));
                }
            motorCan = new Modes(ch2, cl, cm, cg, dt);
            canShape = new double[ch2.Count];
            for (int i = 0; i < canShape.Length; i++) canShape[i] = (rng.NextDouble() * 2 - 1) * Math.Sqrt(3);

            // A pile fibre's first cantilever mode, w = (1.875 / L)^2 sqrt(E I / rho A), with I / A = d^2 / 16.
            fibreW = 1.875 * 1.875 / (PileHeight * PileHeight) * PileFibreD / 4 * Math.Sqrt(PpE / PpRho);

            smallForce = 0;
        }

        private void Log(string s) => report?.Events.Add(s);

        private void Note(string name, double force)
        {
            contactLog.TryGetValue(name, out var c);
            if (force > 0)
            {
                if (!c.On) c = (time, force, true); else c.Peak = Math.Max(c.Peak, force);
                contactLog[name] = c;
            }
            else if (c.On)
            {
                Log($"{c.Start * 1000:F1} ms  {name}: peak {c.Peak:F1} N, {(time - c.Start) * 1e6:F0} us");
                contactLog[name] = (c.Start, c.Peak, false);
            }
        }

        /// <summary>The height the wheel's contact meets: the rail's roughness under it, or a grain's top as
        /// the wheel's curve meets it, whichever is higher; and the wheel's own roughness where it touches.</summary>
        private double Surface(int i) => Rail(x + arm[i], i, out _) + WheelAt(i);

        private double WheelAt(int i)
        {
            // The wheel has rolled as far as the leaf has gone: that much of its rim has passed the rail.
            double circ = 2 * Math.PI * wheelR;
            double pos = ((x % circ) + circ) % circ;
            double f = pos / wheelStep;
            int k = (int)f; double t = f - k;
            var w = wheelRough[i];
            if (!auto) return Smooth(w[(k + w.Length - 1) % w.Length], w[k % w.Length], w[(k + 1) % w.Length], w[(k + 2) % w.Length], t);
            return w[k % w.Length] * (1 - t) + w[(k + 1) % w.Length] * t;
        }

        /// <summary>Between a patio profile's points, a cubic through its neighbours. (Drawn straight from point
        /// to point, every point was a kink, and the kinks passed at the leaf's speed over the spacing: a faint
        /// whistle sweeping up to 16 kHz as the leaf got going.)</summary>
        private static double Smooth(double p0, double p1, double p2, double p3, double t)
            => p1 + 0.5 * t * (p2 - p0 + t * (2 * p0 - 5 * p1 + 4 * p2 - p3 + t * (3 * (p1 - p2) + p3 - p0)));

        private double Rail(double at, int wheel, out Grain? on)
        {
            on = null;
            double f = (at + 0.1) / railStep;
            int k = Math.Clamp((int)f, 0, railRough.Length - 2); double t = Math.Clamp(f - k, 0, 1);
            double h = auto ? railRough[k] * (1 - t) + railRough[k + 1] * t
                            : Smooth(railRough[Math.Max(k - 1, 0)], railRough[k], railRough[k + 1], railRough[Math.Min(k + 2, railRough.Length - 1)], t);
            for (int j = FirstGrain(at - maxReach); j < grit.Count && grit[j].X <= at + maxReach; j++)
            {
                var g = grit[j];
                double proud = g.A - sink;           // the tyre wraps round the rest
                if (proud <= 0) continue;
                double reach = Math.Sqrt(2 * wheelR * proud);
                double d = at - g.X;
                if (d < -reach || d > reach) continue;
                double top = proud - d * d / (2 * wheelR);
                if (top > h) { h = top; on = g; }
            }
            return h;
        }

        private int FirstGrain(double from)
        {
            int lo = 0, hi = grit.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (grit[mid].X < from) lo = mid + 1; else hi = mid; }
            return lo;
        }

        public void ScriptPatio(bool opening, double travelSeconds)
        {
            // A person's slide: the latch first (opening), a pull that starts the leaf, the run, and the leaf
            // let run into its end at a walking hand's pace.
            x = opening ? 0.0005 : travel - 0.0005;
            double from = x, to = opening ? travel + 0.004 : -0.004;
            // Most people brake an opening leaf to a touch at its end and push a shutting one home.
            double arrive = opening ? 0.05 : 0.35;     // m/s at the end
            double tLatch = opening ? 0.05 : -1, tStart = opening ? 0.35 : 0.05;
            double tRun = Math.Max(0.5, travelSeconds);
            small = opening ? smallThrow : 0;
            Settle();
            double end = tStart + tRun + (opening ? 0.8 : 1.4);
            double tShut = -1;
            bool thrown = false;
            while (time < end)
            {
                double t = time;
                // The latch: opening, the thumb throws the hook out before anything moves; shutting, once the
                // leaf is home the lever is thrown and the hook goes up into its keeper.
                if (opening && t >= tLatch && t < tLatch + 0.1) smallForce = -HookSnap;
                else if (!opening && tShut > 0 && t >= tShut + 0.35 && t < tShut + 0.45) { smallForce = HookSnap; if (!thrown) { thrown = true; Log($"{t * 1000:F0} ms  latch thrown"); } }
                else smallForce = 0;
                if (t >= tStart && t < tStart + tRun)
                {
                    double uu = (t - tStart) / tRun;
                    var (p, v) = Hermite(uu, from, 0, to, arrive * Math.Sign(to - from) * tRun);
                    handX = p; handV = v / tRun;
                    double a = Hermite(Math.Min(1, uu + 1e-4), from, 0, to, arrive * Math.Sign(to - from) * tRun).V / tRun;
                    handA = (a - handV) / (1e-4 * tRun);
                    handOn = !handReleased;
                }
                else if (t >= tStart + tRun) { handOn = false; }
                if (!opening && tShut < 0 && x <= 0.003) { tShut = t; holdingHome = true; Log($"{t * 1000:F0} ms  home"); }
                if (holdingHome && t > tShut + 0.6) holdingHome = false;
                Step();
            }
        }

        public void ScriptAutomatic(bool opening, double travelSeconds)
        {
            // The controller: the lock lifted, then a run at its speed with a check zone into the end. The
            // motor follows its profile; the belt carries the leaf after it. Shutting, the lock drops a
            // quarter of a second after the leaf is home.
            x = opening ? 0.0 : travel - 0.002;
            motorX = x; clamp = x; clampRate = 0;
            small = opening && door.Locking ? 0 : smallThrow;
            // A door left unlocked by day: its plunger stays lifted and never moves.
            bool energised = !opening || !door.Locking;
            smallForce = (energised ? PlungerPull : 0) - PlungerSpring;
            Settle();
            double to = opening ? travel - 0.002 : -0.001;
            double vRun = opening ? AutoOpenSpeed : AutoCloseSpeed;
            double tLock = 0.02, tGo = opening ? 0.15 : 0.05;
            double dir = Math.Sign(to - x);
            double done = -1, end = double.MaxValue;
            while (time < end)
            {
                double t = time;
                if (opening && t >= tLock && !energised && door.Locking) { energised = true; Log($"{t * 1000:F0} ms  lock lifts"); }
                if (!opening && done > 0 && t >= done + 0.25 && energised && door.Locking) { energised = false; Log($"{t * 1000:F0} ms  lock drops"); }
                smallForce = (energised ? PlungerPull : 0) - PlungerSpring;
                if (t >= tGo && done < 0)
                {
                    if (Controller(opening, to, dir, vRun, dt, ref motorX, ref motorU, ref motorA))
                    {
                        motorU = 0; motorA = 0; done = t; end = t + (opening ? 0.6 : 0.9);
                        Log($"{t * 1000:F0} ms  {(opening ? "open" : "shut")}");
                    }
                }
                Step();
            }
        }

        /// <summary>
        /// One step of the controller: a run at its speed with a check zone into the end, its acceleration
        /// ramped (an S-curve: a step in it surged the leaf against the belt and knocked the clamp
        /// mid-travel). True once it has come to rest at the end.
        /// </summary>
        internal static bool Controller(bool opening, double to, double dir, double vRun, double dt,
                                        ref double motorX, ref double motorU, ref double motorA)
        {
            double left = (to - motorX) * dir;
            double check = opening ? OpenCheckSpeed : CloseCheckSpeed;
            double want = left > CheckZone ? vRun : check;
            // Brake in time to reach the check speed at the zone, and come to rest at the end.
            if (Math.Abs(motorU) > check && left - CheckZone < (motorU * motorU - check * check) / (2 * AutoAccel))
                want = check;
            want = Math.Min(want, Math.Sqrt(2 * AutoAccel * Math.Max(0, left)));
            double aWant = Math.Clamp(6 * (want * dir - motorU), -AutoAccel, AutoAccel);
            motorA += Math.Clamp(aWant - motorA, -AutoJerk * dt, AutoJerk * dt);
            motorU += motorA * dt;
            motorX += motorU * dt;
            return left < 1e-5 && Math.Abs(motorU) < 1e-3;
        }

        /// <summary>The leaf at rest where the script put it: each wheel carries a quarter of it on its contact
        /// and its half of a bracket.</summary>
        private void Rest()
        {
            heave = 0; heaveRate = 0; pitch = 0; pitchRate = 0;
            var lift = new double[Wheels];
            for (int i = 0; i < Wheels; i++)
            {
                vw[i] = 0;
                double load = mass * G / Wheels + wheelKg * G;
                double depth = Math.Pow(load / contactK, 2.0 / 3);
                zw[i] = Surface(i) - depth;
                trackPort[i].X = load / RailBedding;
                zw[i] -= trackPort[i].X;
                framePort[i].X = mass * G / Wheels / BracketBacking;
                lastH[i] = Surface(i);
                lift[i] = zw[i] - mass * G / Wheels / (bracketK / 2) - framePort[i].X;
            }
            double sa2 = 0, sl = 0;
            foreach (double v in lift) heave += v / Wheels;
            for (int i = 0; i < Wheels; i++) { sl += (lift[i] - heave) * arm[i]; sa2 += arm[i] * arm[i]; }
            pitch = sl / sa2;
            gHeave = heave; gPitch = pitch; gHeaveRate = 0; gPitchRate = 0;
        }

        /// <summary>A twentieth of a second with nothing moving, unheard, so the fields start from rest.</summary>
        private void Settle()
        {
            Rest();
            recording = false;
            double stop = time + 0.05;
            while (time < stop) Step();
            recording = true;
            time = 0;
        }

        private void Step()
        {
            double sideForce = 0;      // along the track, on the leaf
            double[] host = new double[Wheels];
            double pTrack = 0, pFrame = 0, pGlass = 0, pSmall = 0, pDrive = 0;

            // ── Rollers ──
            for (int i = 0; i < Wheels; i++)
            {
                double at = x + arm[i];
                double rail = Rail(at, i, out var grain);
                double h = rail + WheelAt(i) - trackPort[i].X;
                double hRate = (h + trackPort[i].X - lastH[i]) / dt;
                lastH[i] = h + trackPort[i].X;
                double depth = h - zw[i];
                double f = Contact(contactK, contactLambda, depth, hRate - trackPort[i].V - vw[i]);
                Note($"roller{i}", f > 1e-3 ? f : 0);
                // A grain carrying the wheel gives way, a piece at a time.
                if (!auto && ch.Tyre == Tyre.Nylon)
                {
                    // A patio tyre's tread yields round a grain loaded past what nylon holds, and takes it in.
                    if (grain != null && !grain.Done && f > NylonYield * 4 * grain.A * grain.A)
                    {
                        grain.A -= grain.A * dt / EmbedSeconds * Math.Min(1, f / (NylonYield * 4 * grain.A * grain.A) - 1);
                        grain.Target = grain.A;
                        if (grain.A <= PowderMicron * 1e-6) grain.Done = true;
                    }
                }
                else if (grain != null && !grain.Done && f > GritStrength * 4 * grain.A * grain.A && time - grain.LastCrush > CrushSeconds)
                {
                    grain.LastCrush = time;
                    grain.Target = Math.Max(PowderMicron * 1e-6, grain.A * (0.25 + 0.5 * rng.NextDouble()));
                }
                // The rail's patch, the wheel, the bracket.
                double trackDrive = trackPort[i].Step(f, dt, out _);
                // The header's wall takes a carriage wheel's force where it bears, as a plate does. (Through the
                // stiff few-gram patch the drive rose 6 dB an octave to 4.6 kHz, and the header hissed.) The
                // patio sill keeps its patch: that door was heard and kept as it is.
                trackField.Modes.Push(trackHit[i], auto ? f - restLoad : trackDrive);
                // The spring is to the frame's patch; its damping (the housings, the glazing) is to the leaf's
                // body, not across a few grams of patch.
                double leafAt = heave + pitch * arm[i] + framePort[i].X;
                double leafRate = heaveRate + pitchRate * arm[i];
                double fb = bracketK / 2 * (zw[i] - leafAt) + bracketC / 2 * (vw[i] - leafRate);
                double aw = (f - fb) / wheelKg - G;
                vw[i] += aw * dt; zw[i] += vw[i] * dt;
                double frameDrive = framePort[i].Step(fb, dt, out double toLeaf);
                frameField.Modes.Push(frameHit[i], frameDrive);
                host[i] = toLeaf;
                // The glass in its gasket, moved by the frame where the bracket is.
                double gasket = GasketK * (framePort[i].X - glassPort[i].X);
                double glassDrive = glassPort[i].Step(gasket + GasketK * glassPort[i].X, dt, out _);
                glassField.Modes.Push(glassHit[i], glassDrive);
                // Rolling resistance, and a grain's slope pushing back.
                double slope = (Rail(at + 1e-4, i, out _) - rail) / 1e-4;
                sideForce -= ch.Roll * f * Math.Tanh(u / 0.002) + f * slope;
            }
            foreach (var g in grit)
            {
                if (g.Done) continue;
                if (g.A == g.Target) continue;
                g.A += (g.Target - g.A) * (1 - Math.Exp(-dt / CrushSeconds));
                if (Math.Abs(g.A - g.Target) < 1e-9)
                {
                    g.A = g.Target;
                    g.Done = g.A <= PowderMicron * 1e-6;
                }
            }

            // ── Weatherstrip ──
            // A fibre slips and catches again every few tens of microns of travel: the drag is a mean and a
            // flutter in the band that rate makes.
            double slipHz = Math.Min(20000, Math.Abs(u) / PileSlip);
            double white = (rng.NextDouble() * 2 - 1) * Math.Sqrt(3);
            double a1 = 1 - Math.Exp(-2 * Math.PI * Math.Max(slipHz, 1) * dt);
            brushNoise += a1 * (white - brushNoise);
            double moving = Math.Tanh(Math.Abs(u) / 0.005);
            double pile = ch.Pile * moving;
            sideForce -= pile * Math.Sign(u);
            // (The one-pole leaves white noise with a1 / (2 - a1) of its variance: put it back to one.)
            // An automatic leaf's drag in travel is mostly its nylon floor guide sliding smoothly; its brush
            // seals only wipe the jamb at the ends, about a fifth of it. (All of it as brushing fibres hissed.)
            double brushing = auto ? AutoBrushShare : 1;
            double flutter = pile * brushing * 0.3 / Math.Sqrt(auto ? AutoPileFibres : PatioPileFibres) * brushNoise / Math.Sqrt(a1 / (2 - a1));
            if (!auto)
            {
                // A fibre's catches and slips are alike, each a tooth of nearly the same length: what their
                // sum flutters with lies about the slip rate, and falls away below it as the teeth come
                // regular. (Left white down to nothing, the pile rang a smooth new door's frame at 60-80 Hz,
                // a boom under the roll.)
                pileLow += a1 * (flutter - pileLow);
                flutter = (flutter - pileLow) * Math.Sqrt(2);
                // The tips' flutter as the backing feels it, through the fibres' own bending.
                double fa = fibreW * fibreW * (flutter - fibreBend) - 2 * PileZeta * fibreW * fibreRate;
                fibreRate += fa * dt; fibreBend += fibreRate * dt;
                flutter = fibreBend;
            }
            frameField.Modes.Push(brushFrameHit, flutter);
            trackField.Modes.Push(brushTrackHit, flutter);

            // ── The ends of the travel ──
            // Each end's stile meets its bumper; the leaf follows through the stile's spring.
            double jamb = auto ? BumperLambda : PatioJambLambda;
            double fShut = Contact(bumperK, jamb, -(x + stileD[0]), -(u + stileV[0]));
            double fOpen = Contact(bumperK, jamb, x + stileD[1] - travel, u + stileV[1]);
            double leafAcc = lastLeafAcc;
            for (int k = 0; k < 2; k++)
            {
                double spring = stileK * stileD[k] + stileC * stileV[k];
                double onStile = (k == 0 ? fShut : -fOpen) - spring;
                double a = onStile / stileKg - leafAcc;           // relative to the leaf
                stileV[k] += a * dt; stileD[k] += stileV[k] * dt;
                sideForce += spring;
                glassField.Modes.Push(glassEdge[k], spring * EdgeEccentricity);
                unit?.Push(ones, spring * EdgeEccentricity * 0.5);
                frameField.Modes.Push(frameEdge[k], spring * EdgeEccentricity);
            }
            double endDrive = endPort.Step(fShut + fOpen, dt, out _);
            frameField.Modes.Push(endFrameHit, endDrive);
            trackField.Modes.Push(endTrackHit, endDrive * 0.5);
            Note("shut-bumper", fShut); Note("open-bumper", fOpen);
            // A hand lets go when the leaf reaches its end: it does not lean on the bumper.
            if (fShut > 0 || fOpen > 0) handReleased = true;
            if (holdingHome) sideForce -= HoldHome + ArmDamping * u;

            // ── Hand (patio) ──
            if (!auto)
            {
                if (handOn)
                {
                    double fh = mass * handA + 1200 * (handV - u) + 15000 * (handX - x);
                    sideForce += Math.Clamp(fh, -250, 250);
                }
            }

            // ── Drive (automatic) ──
            if (auto)
            {
                double pulleyRev = motorU / (Math.PI * PulleyD);
                double motorRev = pulleyRev * GearRatio;
                beltPhase += motorU / BeltPitch * dt;
                meshPhase += motorRev * WormStarts * dt;             // a tooth of the wheel for each start, each turn
                motorPhase += motorRev * MotorSlots * dt;
                // The worm's transmission error: its tooth shape, and each tooth's own error blended across the
                // mesh. It moves the belt's driven end, and it is a force between worm and wheel.
                int teeth = toothError.Length;
                int tooth = (int)(Math.Floor(meshPhase) % teeth + teeth) % teeth;
                double frac = meshPhase - Math.Floor(meshPhase);
                double profile = 0;
                for (int h = 0; h < profilePhase.Length; h++) profile += Math.Sin(2 * Math.PI * (h + 1) * frac + profilePhase[h]) / (h + 1);
                double te = ch.Tooth * (profile + toothError[tooth] * (1 - frac) + toothError[(tooth + 1) % teeth] * frac);
                double beltK = BeltK, beltC = 2 * BeltZeta * Math.Sqrt(BeltK * mass);
                // The belt pulls the clamp; the clamp's lug pulls the carriage across its play.
                double belt = beltK * (motorX + te * PulleyD / 2 / WormWheelR - clamp) + beltC * (motorU - clampRate);
                double rel = clamp - x, relRate = clampRate - u;
                double lug = Contact(LugK, LugLambda, rel - ClampPlay / 2, relRate) - Contact(LugK, LugLambda, -rel - ClampPlay / 2, -relRate);
                double clampAcc = (belt - lug) / ClampKg;
                clampRate += clampAcc * dt; clamp += clampRate * dt;
                sideForce += lug;
                // The knock rings the carriage and the leaf's top rail, stiff parts that pass the blow on to the
                // leaf: what bends the rail's thin faces is what its patch gives at the rail's impedance. The
                // belt's steady pull through the lug only carries the leaf. (Fed whole and straight into the
                // faces, the leaf braking onto the clamp rang the frame at 82 dBA.)
                frameField.Modes.Push(lugHit, lugPort.Step(lugHigh.Next(lug), dt, out _));
                pDrive += clampNoise.Pressure(clampAcc);
                Note("lug", Math.Abs(lug) > 0 && Math.Abs(rel) > ClampPlay / 2 ? Math.Abs(lug) : 0);
                double load = Math.Abs(belt) + 5;
                double turning = Math.Tanh(Math.Abs(motorRev) / 2);
                gearbox.Push(gearShape, MeshK * te * turning * (0.5 + load / 100));
                // The belt's pull reacts on the drive, which sits on its rubber on the header.
                double torque = load * PulleyD / 2 / GearRatio;
                drive.F += -belt + 0.1 * torque / 0.03 * Math.Sin(2 * Math.PI * motorPhase);
                drive.Step(dt);
                trackField.Modes.Push(driveHit, drive.Reaction);
                // A belt tooth seating in the idler's groove each pitch, each a little unlike the last.
                beltRun += Math.Abs(motorU) * dt;
                if (beltRun >= nextTooth)
                {
                    nextTooth = beltRun + BeltPitch * (1 + ToothPitchJitter * (rng.NextDouble() * 2 - 1));
                    seatLeft = ToothSeat;
                    seatPeak = BeltToothKg * ToothSeatShare * Math.Abs(motorU) * Math.PI / (2 * ToothSeat)
                               * (1 + ToothLevelJitter * (rng.NextDouble() * 2 - 1));
                }
                if (seatLeft > 0)
                {
                    trackField.Modes.Push(idlerHit, seatPeak * Math.Sin(Math.PI * (1 - seatLeft / ToothSeat)));
                    seatLeft -= dt;
                }
                // The brushes: a knock as each bar passes under them, and their friction's flutter.
                double surface = Math.Abs(motorRev) * 2 * Math.PI * 0.0125;
                double bar = Math.Floor(motorPhase);
                if (bar != lastBar && surface > 0.05)
                {
                    lastBar = bar;
                    brushLeft = BrushLanding;
                    brushPeak = BrushKg * surface * BarStep / BarGap * (0.7 + 0.6 * rng.NextDouble()) * Math.PI / (2 * BrushLanding);
                }
                double brush = 0;
                if (brushLeft > 0) { brush = brushPeak * Math.Sin(Math.PI * (1 - brushLeft / BrushLanding)); brushLeft -= dt; }
                double ripple = 0.1 * torque / CanR;
                brush += ripple * (Math.Sin(2 * Math.PI * motorPhase) + 0.5 * Math.Sin(4 * Math.PI * motorPhase + 0.7));
                motorCan.Push(canShape, brush);
                double inside = gearbox.Step() + motorCan.Step();
                coverLow += (1 - Math.Exp(-2 * Math.PI * CoverHz * dt)) * (inside - coverLow);
                pDrive += Cover * coverLow;
            }

            // ── Latch hook (patio) or lock plunger (automatic): a small steel part between two stops ──
            {
                // The stops ride on their patch: the low one at the patch, the high one a throw above it.
                double k = auto ? MetalK : HookStopK, lam = auto ? MetalLambda : HookStopLambda;
                double low = Contact(k, lam, smallPort.X - small, smallPort.V - smallRate);
                double high = Contact(k, lam, small - smallThrow - smallPort.X, smallRate - smallPort.V);
                double fSmall = smallForce + low - high - smallRate * (auto ? 0.5 : HookDrag);
                if (auto) fSmall -= smallKg * G;
                double acc = fSmall / smallKg;
                smallRate += acc * dt; small += smallRate * dt;
                double hit = high - low;
                double smallDrive = smallPort.Step(hit, dt, out _);
                (auto ? trackField : frameField).Modes.Push(smallHit, smallDrive);
                pSmall += smallNoise.Pressure(acc);
                Note(auto ? "plunger" : "hook", low + high);
            }

            // ── The pull handle in its play (patio) ──
            if (!auto)
            {
                double rel = handle - handlePort.X, relRate = handleRate - handlePort.V;
                double knock = Contact(MetalK, MetalLambda, rel - HandlePlay, relRate) - Contact(MetalK, MetalLambda, -rel - HandlePlay, -relRate);
                double centring = HandleCentring * handle + 2 * HandleZeta * Math.Sqrt(HandleCentring * HandleKg) * handleRate;
                double hacc;
                if (handOn) { hacc = 0; handleRate = 0; handle = 0; }
                else
                {
                    // Relative to the leaf, which is decelerating under it.
                    hacc = (-knock - centring) / HandleKg - lastLeafAcc;
                    handleRate += hacc * dt; handle += handleRate * dt;
                }
                double hd = handlePort.Step(knock, dt, out _);
                frameField.Modes.Push(handleHit, hd);
                pSmall += handleNoise.Pressure(hacc + lastLeafAcc) + escutcheonSound.Pressure(handlePort.Acc);
                Note("handle", Math.Abs(knock));
            }

            // ── Rigid motion ──
            double heaveAcc = -G, pitchAcc = 0;
            if (auto)
                for (int i = 0; i < Wheels; i++) { heaveAcc += host[i] / mass; pitchAcc += host[i] * arm[i] / pitchInertia; }
            else
            {
                // The frame on its rollers, and the glass on its blocks on the frame.
                double up = 0, turn = 0;
                for (int b = -1; b <= 1; b += 2)
                {
                    double xb = b * blockArm;
                    double squeeze = heave + pitch * xb - (gHeave + gPitch * xb) + blockPreload;
                    double rate = heaveRate + pitchRate * xb - (gHeaveRate + gPitchRate * xb);
                    double fb = BlockK * squeeze + blockC * rate;
                    up += fb; turn += fb * xb;
                }
                for (int i = 0; i < Wheels; i++) { heaveAcc += host[i] / frameKg; pitchAcc += host[i] * arm[i] / frameInertia; }
                heaveAcc -= up / frameKg; pitchAcc -= turn / frameInertia;
                gHeaveRate += (-G + up / glassKg) * dt; gHeave += gHeaveRate * dt;
                gPitchRate += turn / glassInertia * dt; gPitch += gPitchRate * dt;
            }
            heaveRate += heaveAcc * dt; heave += heaveRate * dt;
            pitchRate += pitchAcc * dt; pitch += pitchRate * dt;
            lastLeafAcc = sideForce / (mass - 2 * stileKg);
            u += lastLeafAcc * dt; x += u * dt;

            // ── Radiate ──
            pTrack += trackField.Modes.Step();
            pFrame += frameField.Modes.Step();
            pGlass += glassField.Modes.Step();
            if (unit != null) { unit.Step(); pGlass += unitGain * unit.Acc[0]; }
            double p = pTrack + pFrame + pGlass + pSmall + pDrive;
            if (recording)
            {
                double[] parts = { pTrack, pFrame, pGlass, pSmall, pDrive };
                for (int i = 0; i < parts.Length; i++) peaks[i] = Math.Max(peaks[i], Math.Abs(parts[i]));
                if (StemFolder != null)
                {
                    stems ??= new List<float>[parts.Length];
                    for (int i = 0; i < parts.Length; i++) (stems[i] ??= new List<float>()).Add((float)(parts[i] / PascalsAtFullScale));
                }
                if (!double.IsFinite(p)) p = 0;
                outHi.Add((float)p);
            }
            time += dt;
        }

        public float[] Output()
        {
            var sb = new StringBuilder("peaks by part, dB SPL at 1 m:");
            for (int i = 0; i < peaks.Length; i++) sb.Append($" {PeakNames[i]} {20 * Math.Log10(Math.Max(1e-9, peaks[i]) / 2e-5):F0}");
            Log(sb.ToString());
            if (StemFolder != null && stems != null)
                for (int i = 0; i < stems.Length; i++)
                    using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, "sd-" + PeakNames[i] + ".raw"))))
                        foreach (var v in stems[i]) f.Write(v);
            var y = Decimate(outHi, rate, PascalsAtFullScale, out double peak);
            if (report != null) report.PeakPascals = peak;
            return y;
        }
    }
}
