using System.Collections.Generic;
using System.Linq;
using System.Text;
using static OpenFPS.Common.DoorPhysics;

namespace OpenFPS.Common;

/// <summary>
/// Sliding doors simulated as the objects, as <see cref="KnobDoor"/> and <see cref="PushBarDoor"/> are: a
/// glass patio door slid by hand on rollers in a sill track, and an automatic door hung from carriages in a
/// header, driven by a motor through a worm gear and a toothed belt.
/// <list type="bullet">
/// <item>The leaf: an aluminium frame round glass on two rollers, bouncing and rocking on them, its frame and
/// glass ringing as dense fields fed through the brackets.</item>
/// <item>The rollers: a wheel on a bracket spring, on its rail through a Hertz contact, over the rail's
/// roughness, its own (out of round, flatted) and grit. A tyre wraps round a grain smaller than its sink
/// under load; a bigger one lifts the wheel and crushes a few steps at a time. Over a grain the centre
/// follows a parabola of the wheel's radius, not the grain's shape.</item>
/// <item>The track (patio sill) or header (automatic): an aluminium extrusion, a dense field struck at each
/// contact.</item>
/// <item>The weatherstrip: polypropylene pile, a drag and a hiss from its fibres slipping.</item>
/// <item>Patio: a hand on a pull handle with play, a hook latch, pile that grips the leaf at rest, the jamb's
/// pile and bulb and the jamb behind them, a rubber bumper in the head track. Opening: the latch, the pull
/// taken up, the leaf breaking away and brought to rest by the hand. Shutting: a push into the jamb, the
/// handle thrown across its play, the stile meeting the jamb.</item>
/// <item>Automatic: the controller's speed profile, a DC motor and worm on a rubber-mounted bracket, a
/// toothed belt to the carriage, and a solenoid lock.</item>
/// </list>
/// What each round fixed: docs/DOOR_TYPES.md, Model history.
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

    private enum Tyre { Nylon, Urethane, Steel }

    /// <summary>
    /// What wear does to a sliding door, by character (new, standard, worn, old): grit per metre and its mean
    /// size; rail and wheel roughness, RMS (a patio door's at the profilometer's 2.5 mm cut-off); the wheel's
    /// flat (a polyurethane tyre left standing takes about a tenth of a millimetre); the tyre (an old automatic
    /// door's urethane replaced with hard nylon); the pile's drag, N (a patio door takes 40-50 N to keep
    /// moving, nearly all seals); rolling resistance as a share of the load; the gear's transmission error, m.
    /// Every patio door rolls on nylon, as fitted on aluminium track: on steel each grain clicked and rang,
    /// Cody's "metallic".
    /// </summary>

    private readonly record struct Character(double GritPerMetre, double GritMicron, double RailMicron,
        double WheelMicron, double FlatMicron, Tyre Tyre, double Pile, double Roll, double Tooth, double Breakaway = 1,
        double HandlePlay = 0.0003);

    /// <summary>Breakaway (patio): starting over running drag, 1.1 new to 1.4 dirty and old. HandlePlay: the
    /// pull handle's play each way, 0.05 mm new to 0.3 mm loose. At 0.3 mm on every door the pull's knock stood
    /// level with the slide; the recording's are 6-15 dB under it.</summary>
    private static Character Of(Kind kind, int variant) => (kind, variant % Variants) switch
    {
        (Kind.Patio, 0) => new(0.3, 60, 0.5, 2, 0, Tyre.Nylon, 35, 0.005, 0, 1.1, 0.00005),
        (Kind.Patio, 1) => new(1, 120, 1.0, 4, 5, Tyre.Nylon, 42, 0.008, 0, 1.2, 0.0001),
        (Kind.Patio, 2) => new(3, 150, 2.5, 8, 15, Tyre.Nylon, 50, 0.015, 0, 1.3, 0.0002),
        (Kind.Patio, _) => new(4, 120, 3.0, 8, 30, Tyre.Nylon, 50, 0.025, 0, 1.4, 0.0003),
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
        if (door.Kind == Kind.Patio) sim.ScriptPatio(true, travelSeconds); else sim.ScriptAutomatic(true);
        return sim.Output();
    }

    /// <summary>Shutting: the patio leaf pushed home and its latch thrown; the automatic leaf run shut and
    /// its lock dropped.</summary>
    public static float[] RenderClose(Door door, int sampleRate, double travelSeconds = 1.4, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        if (door.Kind == Kind.Patio) sim.ScriptPatio(false, travelSeconds); else sim.ScriptAutomatic(false);
        return sim.Output();
    }

    // ── The game ─────────────────────────────────────────────────────────────────────────────────

    public const string KeyPrefix = "slidingdoor:";

    /// <summary>How long an automatic leaf of this width runs open or shut under its controller, seconds: the
    /// server moves the door in this time, so leaf and sound arrive together.</summary>
    public static float AutomaticSeconds(float width, bool opening)
    {
        double dt = 1e-3, travel = width;
        double x = opening ? 0 : travel - 0.002, to = opening ? travel - 0.002 : -0.001, u = 0, a = 0, t = 0;
        double dir = Math.Sign(to - x), vRun = opening ? AutoOpenSpeed : AutoCloseSpeed;
        while (t < 30 && !Sim.Controller(opening, to, dir, vRun, dt, ref x, ref u, ref a)) t += dt;
        return (float)t;
    }

    /// <summary>Declared levels, dB at a metre, by kind and character: the render's peak
    /// (<see cref="KnobDoor.OpenLevelDb"/>). Measured at a 1.0 by 2.1 m leaf (AudioLab --heard-levels survey,
    /// 2026-10-04; the patio door's again after its round 3, only=patio); the client puts each render's own
    /// peak in its place. As the LAFmax, every run played 15-29 dB under the model.</summary>
    public static float OpenLevelDb(Kind kind, int variant) => (kind, ((variant % Variants) + Variants) % Variants) switch
    {
        (Kind.Patio, 0) => 75.9f, (Kind.Patio, 1) => 82.4f, (Kind.Patio, 2) => 88.2f, (Kind.Patio, _) => 93.2f,
        (Kind.Automatic, 0) => 77.8f, (Kind.Automatic, 1) => 76.1f, (Kind.Automatic, 2) => 79.4f, _ => 84.6f,
    };
    public static float CloseLevelDb(Kind kind, int variant) => (kind, ((variant % Variants) + Variants) % Variants) switch
    {
        (Kind.Patio, 0) => 113.4f, (Kind.Patio, 1) => 113.4f, (Kind.Patio, 2) => 115.4f, (Kind.Patio, _) => 113.7f,
        (Kind.Automatic, 0) => 61.0f, (Kind.Automatic, 1) => 62.6f, (Kind.Automatic, 2) => 71.1f, _ => 73.0f,
    };

    /// <summary>What the model puts at a metre, LAFmax, no calibration: the automatic door runs at 35-55 dBA,
    /// where the research puts real ones (40-55); a patio door pushed home casually meets its jamb frame on
    /// frame, 91-93 dBA; opened, its slide is 63-76.</summary>
    public static float OpenLafDb(Kind kind, int variant) => (kind, ((variant % Variants) + Variants) % Variants) switch
    {
        (Kind.Patio, 0) => 63.4f, (Kind.Patio, 1) => 68.7f, (Kind.Patio, 2) => 75.6f, (Kind.Patio, _) => 75.4f,
        (Kind.Automatic, 0) => 53.5f, (Kind.Automatic, 1) => 53.2f, (Kind.Automatic, 2) => 61.5f, _ => 70.1f,
    };
    public static float CloseLafDb(Kind kind, int variant) => (kind, ((variant % Variants) + Variants) % Variants) switch
    {
        (Kind.Patio, 0) => 91.0f, (Kind.Patio, 1) => 92.0f, (Kind.Patio, 2) => 93.3f, (Kind.Patio, _) => 91.3f,
        (Kind.Automatic, 0) => 36.4f, (Kind.Automatic, 1) => 38.3f, (Kind.Automatic, 2) => 49.6f, _ => 56.0f,
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
    public static float[] RenderKey(string key, int sampleRate) => RenderKey(key, sampleRate, out _);

    /// <summary>The same, and its own peak, dB SPL at a metre: the level its full scale stands for.</summary>
    public static float[] RenderKey(string key, int sampleRate, out float fullScaleDb)
    {
        fullScaleDb = 0f;
        if (!TryParseKey(key, out bool closing, out var door, out float travel)) return new float[16];
        float[] pcm = closing ? RenderClose(door, sampleRate, travel) : RenderOpen(door, sampleRate, travel);
        return KnobDoor.PeakToFullScale(pcm, PascalsAtFullScale, out fullScaleDb);
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
    /// <summary>Each assembly's bracket spring (half to each wheel). The bounce loses a tenth of critical in
    /// the housings and glazing; set by the wheel's mass, the leaf hopped on steel wheels for a second after
    /// it shut.</summary>
    private const double PatioBracketK = 1e7, AutoBracketK = 5e6, BounceZeta = 0.1;
    /// <summary>Tyres on their rail, Hertz K for a grooved wheel on a crowned rail: nylon on aluminium,
    /// about 6 MN/m of contact stiffness at 100 N; polyurethane on the automatic door's carriages; and an old
    /// door's steel wheels on a stainless cap, about 70 MN/m.</summary>
    private const double NylonK = 5.7e8, UrethaneK = 1.2e7, SteelOnAlK = 3e10;
    private const double NylonLambda = 0.5, UrethaneLambda = 2.0, SteelLambda = 0.1;
    /// <summary>A patio roller's 1/8 in groove, worn in, all but conforms to the rib's 3.1 mm crown, so the
    /// contact is an ellipse about 0.75 mm along the rail and 2.5 mm across (glass-filled nylon, E* about 8.5
    /// GPa on aluminium; steel about 58). As a ball's round patch, 1.4 mm long, it swallowed everything above
    /// 700 Hz.</summary>
    private const double GrooveR = 0.0032, CrownR = 0.0031, NylonEStar = 8.5e9, SteelOnAlEStar = 5.8e10;
    /// <summary>The tread's texture blurs the patch's edges over about 30 um, passing little shorter than a
    /// tenth of a millimetre. With sharp edges roughness reached 8 kHz, 5 dB over the recording.</summary>
    private const double ContactEdge = 32e-6;
    /// <summary>The track: the patio sill, aluminium 1.8 mm, about 0.12 m developed; the automatic door's
    /// header, 2.5 mm, 0.45 m round. Both bedded or bolted: they lose more than a bare sheet.</summary>
    private const double SillT = 0.0018, SillGirth = 0.12, HeaderT = 0.0025, HeaderGirth = 0.45, BeddedLoss = 0.01;
    /// <summary>A patio sill's slow bending works its bead of sealant: about 12 / f more loss, 0.1 at 125 Hz.
    /// With a bare sheet's loss the roll rang at 100-160 Hz, 5 dB over the recording.</summary>
    private const double SillBead = 12;
    /// <summary>The header is a box of walls 0.1-0.15 m wide under a cover, so its modes start near 300 Hz and
    /// the cover and brackets take about 0.03. As one sheet from 60 Hz it boomed: Cody, "a metallic tube".</summary>
    private const double HeaderFromHz = 300, HeaderLoss = 0.03;
    private const double PortStiffness = 2e7;
    /// <summary>Where a wheel bears: about a gram of rail on the bedded sill (about 5e6 N/m), or 6 g of header
    /// track under a polyurethane wheel's longer contact; where a bracket bears, a couple of grams of bottom
    /// rail. A 7 g patch on a stiff spring rang at 8.5 kHz and made the roll a hiss.</summary>
    private const double RailPatchKg = 0.001, UrethaneRailPatchKg = 0.006, RailBedding = 5e6, BracketPatchKg = 0.002, BracketBacking = 1e7;
    /// <summary>The pull handle's escutcheon: 30 g of zinc on two screws, about 1.3 kHz, rung by the handle and
    /// radiating from its own 40 cm2 as well as through the stile.</summary>
    private const double EscutcheonKg = 0.03, EscutcheonK = 2e6, EscutcheonArea = 0.004;
    /// <summary>Glass sits in the frame on rubber glazing gaskets: the frame's blows reach it through about
    /// 2e5 N/m at each bracket.</summary>
    private const double GasketK = 2e5;
    /// <summary>A patio leaf's sealed unit stands on two setting blocks at the quarter points (neoprene, about
    /// 85 Shore A, 100 by 24 by 6 mm: about 4 MN/m each, loss factor 0.2), so the leaf bounces as two bodies,
    /// 8 kg of frame and 30 kg of glass. As one it rang at 80-160 Hz under every roll, 6 dB over the recording.</summary>
    private const double BlockK = 4e6, BlockZeta = 0.1;
    /// <summary>A tyre sinks under the leaf (nylon 40-55 um, steel 4-5 um, polyurethane over half a
    /// millimetre) and wraps round a smaller grain; a grain that carries the wheel crushes at about 150 MPa
    /// in a few steps of tens of microseconds.</summary>
    private const double GritStrength = 150e6, CrushSeconds = 2e-5, PowderMicron = 3;
    /// <summary>Steeper than nylon's grip on sand (about 0.3; the angle's tangent is sqrt(2 a / R)), a wheel
    /// shoves a grain off the rib instead of climbing it: nothing over about 0.7 mm on a 1-1/4 in wheel.
    /// Climbed and crushed, a 1 mm grain struck the rail with 700 N and rang the sill at 127 dB.</summary>
    private const double GritGrip = 0.3;
    /// <summary>Under nylon a grain does not shatter: nylon yields at about 80 MPa, long before quartz cracks,
    /// so the grain sinks into the tread over a few milliseconds. Shattered in 20 us steps, the rail rang at
    /// 117 dB; sunk in a third of a millisecond, every grain clicked to 20 kHz.</summary>
    private const double NylonYield = 80e6, EmbedSeconds = 5e-3;
    /// <summary>Pile fibres slip about their width, 150 um, each on its own (a patio leaf drags about 5 m at
    /// 500 a centimetre, an automatic about 2 m), so the drag flutters by 0.3 / sqrt(fibres) of itself. All
    /// slipping together, the frame hissed at 94 dBA.</summary>
    private const double PileSlip = 1.5e-4, PatioPileFibres = 250000, AutoPileFibres = 100000, AutoBrushShare = 0.2;
    /// <summary>Rubber bumpers (a patio leaf's open end, an automatic leaf's jamb), stiff enough that a stile
    /// stops in a few milliseconds.</summary>
    private const double PatioBumperK = 5e7, AutoBumperK = 1e7, BumperLambda = 0.6;
    /// <summary>A patio jamb's pile and bulb return 0.1-0.5 of the leaf's speed (about 3 s/m; at 0.6 it rolled
    /// back 43 mm, "the slide still plays after the door is shut"). The hand holds the leaf home with about
    /// 80 N while the latch is thrown, its arm giving about 300 N s/m.</summary>
    private const double PatioJambLambda = 3, HoldHome = 80, ArmDamping = 300;
    /// <summary>
    /// The lock stile shuts through pile and a bulb: a linear cushion of about 50 kN/m over 10 mm, holding
    /// about 2.5 J, so a push faster than about 0.35 m/s meets the jamb frame on frame through a vinyl skin
    /// (research notes, "Sliding and automatic doors", item 9). A casual push does, with 700-900 N, the blow in
    /// the walls at 300 Hz-1 kHz as the recording's. One soft bulb was Cody's "hollow"; a plain dashpot on the
    /// cushion clicked at first touch.
    /// </summary>
    private const double CushionK = 5e4, CushionDepth = 0.010, FrameOnFrameK = 1.5e9, FrameOnFrameLambda = 0.4;
    /// <summary>Where stile and jamb meet, a face is a web between flanges: about 5e5 N/m at mid-web, 2e7 and
    /// more borne flat. Set between, at 4e6, by the recording's blows: their first 30 ms hold its octaves
    /// (300 Hz-1 kHz loudest; under 300 Hz 2-6 dB down, 1-4 kHz 8-12, 4-16 kHz 19-25). At 2e7 the glass's 212
    /// Hz was the loudest thing; at 2e5 the blow stood 45-65 dB over the slide, the recording's 24.</summary>
    private const double WebBacking = 4e6;
    /// <summary>Opening, the hand brings the leaf to rest short of the bumper, no blow, as both recorded
    /// openings end. Run into the bumper, "the open sound sounds like a close sound" (Cody).</summary>
    private const double OpenShort = 0.03;
    /// <summary>A grip comes up over about a tenth of a second. The arm holds the handle, not the leaf, so the
    /// play is taken up at the hand's pace; with the arm on the leaf the handle crossed its play at 0.5 m/s
    /// and knocked 5-10 dB over the slide.</summary>
    private const double HandGrip = 0.1, GripDamping = 150;
    /// <summary>A leaf at rest takes 1.03-1.4 times its running drag to start (research notes, item 7); the
    /// pile bends about its slip distance before it lets go.</summary>
    private const double PileStribeck = 0.002;
    /// <summary>The striking end (stile and nearby glass) on the rest of the leaf through the crimped corners
    /// and gaskets, about 200 Hz, a fifth of critical: the stile stops first and the leaf arrives after, two to
    /// five knocks in a tenth of a second. Rigid, a 40 kg leaf was a 40 ms push; at 0.05 it bounced off.</summary>
    private const double PatioStileKg = 1.5, AutoStileKg = 2.5, StileHz = 200, StileZeta = 0.2;
    /// <summary>A 4-16-4 unit's panes against the air between them, about 200 Hz for a third of a second: the
    /// note of a patio door hitting home (recorded at 220 Hz, 5.7 Hz wide, loss 0.026, 11-19 dB under the
    /// blow).</summary>
    private const double UnitGap = 0.016, UnitLoss = 0.026;
    /// <summary>The glass sits about 5 mm off the stile's line over a 50 mm edge, so a tenth of the load bends
    /// pane and frame: what a leaf hitting home is heard by.</summary>
    private const double EdgeEccentricity = 0.1;
    /// <summary>The patio door's hook latch: a 15 g steel hook thrown 8 mm by its lever's over-centre spring
    /// (about 0.5 N past the centre), onto the nylon of its housing at each end.</summary>
    private const double HookKg = 0.015, HookThrow = 0.008, HookSnap = 0.5, HookVolume = 2e-6;
    private const double HookStopK = 2e8, HookStopLambda = 2;
    /// <summary>The hand is still on the lever as the hook seats, about 3 N s/m; free, it bounced a dozen
    /// times on its stop.</summary>
    private const double HookDrag = 3;
    /// <summary>The pull handle: 100 g of zinc centred in its play, knocking on its escutcheon. Thrown across
    /// the play when the leaf stops dead, it is the bright part of a patio door hitting home.</summary>
    private const double HandleKg = 0.1, HandleCentring = 2e3, HandleZeta = 0.05, HandleVolume = 1.4e-5;
    private const double MetalK = 1e9, MetalLambda = 0.3;
    /// <summary>Zinc on zinc (E* about 53 GPa) over a 5 mm radius: K = 4/3 E* sqrt(R), about 5e9. It knocks
    /// twice in a millisecond 10-25 ms before the blow, as the recording's bright brush 25-38 ms before each,
    /// 4-5 dB over the blow in 4-16 kHz. At 1e9 and 0.3 it was one dull millisecond, 15 dB under.</summary>
    private const double HandleStopK = 5e9, HandleStopLambda = 0.05;
    /// <summary>The automatic door's lock: a 30 g solenoid plunger lifted 8 mm by about 25 N, and dropped
    /// back into the carriage's bracket by a 6 N spring.</summary>
    private const double PlungerKg = 0.03, PlungerThrow = 0.008, PlungerPull = 25, PlungerSpring = 6;
    /// <summary>Drive (a dormakaba ES 200's): an 8 mm pitch belt over a 24-tooth pulley (61 mm), a two-start
    /// worm at 15:1, a 12-segment brushed motor near 3350 rpm at full speed. The belt between motor and
    /// carriage is about 1e5 N/m.</summary>
    private const double BeltPitch = 0.008, PulleyD = 0.061, GearRatio = 15, WormStarts = 2, MotorSlots = 12, BeltK = 1e5, BeltZeta = 0.3;
    /// <summary>The worm's mesh, about 2e6 N/m in plastic: a few microns of error is newtons, ringing the cast
    /// housing (walls from about 1.2 kHz) and moving the belt's pull as much as the 30 mm wheel's rim. The
    /// 1.5 kg drive sits on rubber on the header, about 60 Hz.</summary>
    private const double MeshK = 2e6, WormWheelR = 0.03, DriveKg = 1.5, DriveMountHz = 60, DriveMountZeta = 0.15;
    /// <summary>The motor: a 63 mm, 1.5 mm steel can pulled out of round by torque ripple at the slot rate (a
    /// tenth of its torque) and knocked by its 2 g brushes over the 5 um bar steps, landing over about 0.2 ms.
    /// Full of magnets and grease on rubber, can and gearbox lose about a tenth (at 0.01, "too much
    /// resonance"). The header's cover passes 6 dB an octave less above about 800 Hz, by the mass law.</summary>
    private const double CanR = 0.0315, CanT = 0.0015, BrushKg = 0.002, BarStep = 5e-6, BarGap = 5e-4;
    private const double BrushLanding = 2e-4, DriveHousingLoss = 0.1, Cover = 0.5, CoverHz = 800;
    /// <summary>The belt's clamp has about 0.8 mm of play: as the belt starts or stops pulling the leaf swings
    /// on it (about 6 Hz) and the clamp knocks each half swing, the train 60-90 ms apart heard as a real one
    /// starts. Polyurethane teeth land, about 5 MN/m and lossy.</summary>
    private const double ClampPlay = 0.0008, ClampKg = 0.1, LugK = 2.4e8, LugLambda = 1.0, ClampVolume = 1.3e-5;
    /// <summary>A leaf's aluminium frame, glass in gaskets and seals along it, loses about 0.03; its tube walls
    /// ring from about 300 Hz, and below that it moves with its glass. As a sheet from 60 Hz losing 0.005, one
    /// 76 Hz mode rang 25 dB over its neighbours, Cody's hollow box; the recording's stops ring 36-160 Hz as a
    /// dozen lines within 6 dB, each 5-7 Hz wide.</summary>
    private const double StorefrontLoss = 0.03;
    /// <summary>A belt tooth seats in the idler each pitch: about a gram at a twentieth of the belt's speed,
    /// pitch and tension varying a few per cent so no two alike. At a fifth, all alike, a 37 Hz pulse train.</summary>
    private const double ToothSeat = 3e-4;
    private const double BeltToothKg = 0.001, ToothSeatShare = 0.05, ToothPitchJitter = 0.03, ToothLevelJitter = 0.4;
    /// <summary>Speeds: open at 0.7 m/s (a common default), shut at 0.3 (ANSI/BHMA A156.10 allows no more for
    /// a leaf under 71 kg), slowing for the last 60 mm to 0.08 m/s opening and a 0.04 m/s creep shutting
    /// (the standard asks for the slow-down at least 51 mm out), 0.8 m/s^2 either side.</summary>
    private const double AutoOpenSpeed = 0.7, AutoCloseSpeed = 0.3, OpenCheckSpeed = 0.08, CloseCheckSpeed = 0.04, CheckZone = 0.06, AutoAccel = 0.8;
    /// <summary>The controller ramps its acceleration over about a fifth of a second, m/s^3.</summary>
    private const double AutoJerk = 4;

    /// <summary>A patio door's rolling surfaces are rough at every wavelength from 10 mm down, each octave's
    /// RMS as the fourth root of the wavelength (a height spectrum falling as wavenumber to the 1.5, as worn,
    /// dirty moulded and extruded surfaces measure). Carried on to the whole circumference, the waviness met the
    /// bounce near 100 Hz and the wheels hopped off the rail two thousand times a slide.</summary>
    private const double RoughnessSlope = 0.25, RoughnessLongest = 0.01;
    /// <summary>A character's rail and tyre roughness is RMS as a profilometer reports it, at the standard
    /// 2.5 mm cut-off; the same spectrum carries on above it as waviness, to 10 mm.</summary>
    private const double RoughnessCutoff = 0.0025;
    /// <summary>A patio door's pile fibres, about 7 mm by 0.1 mm, are cantilevers near 370 Hz, so the drag's
    /// flutter reaches the frame through that resonance, falling 12 dB an octave above, about a third of
    /// critical. Pushed straight into the metal, the 5-10 kHz slip rate was an 8 kHz hiss.</summary>
    private const double PileHeight = 0.007, PileFibreD = 1.0e-4, PpE = 1.5e9, PpRho = 900, PileZeta = 0.3;

    private sealed class Grain { public double X, A, Target, LastCrush = -1; public bool Done, Embedding; }

    /// <summary>
    /// A rolling surface as a roller feels it: <paramref name="n"/> heights round <paramref name="length"/> (a
    /// power of two, wrapping), roughness RMS <paramref name="rms"/> from <paramref name="longest"/> down, plus
    /// any <paramref name="feature"/> (a flat), felt through the elliptical contact
    /// (<paramref name="halfPatch"/> along, <paramref name="halfWidth"/> across). Each strip along the rail
    /// averages by its Hertz pressure, passing a wavelength by 2 J1(ka) / ka, and rides its own roughness,
    /// coherent with the middle's as exp(-k y). So a flat smaller than the patch is a dip, and the roughness
    /// falls smoothly away above the speed over the patch: at a walking slide, a few hundred hertz. One strip of
    /// one length has true nulls that swept with the speed as arches of comb.
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
        private readonly Grain?[] lastGrain = new Grain?[Wheels];
        private double maxReach, sink, restLoad, grainPatch;
        /// <summary>Points across a contact patch, as fractions of its half-length, and their share of its
        /// Hertz pressure (a half ellipse).</summary>
        private static readonly double[] PatchPoints = Enumerable.Range(0, 9).Select(m => -1 + (m + 0.5) * 2 / 9.0).ToArray();
        private static readonly double[] PatchWeights = PatchShares();
        private static double[] PatchShares()
        {
            var w = PatchPoints.Select(u => Math.Sqrt(1 - u * u)).ToArray();
            double sum = w.Sum();
            return w.Select(v => v / sum).ToArray();
        }
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
        private double motorA, motorX, motorU, meshPhase, motorPhase;
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
        // A patio door's jamb and stile faces; its pile's grip at rest; the hand's grip.
        private readonly Port? stileFace, jambWeb;
        private LuGre pileGrip;
        private bool shutting;
        private double gripFrom, gripTo = double.MaxValue, handForce, armShare = 1;

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

            double trackLen = travel + width + 0.2;
            // An automatic door's profiles are grained at no less than twice the contact patch's length under
            // the static load (3 mm on the rail): a wheel cannot feel finer.
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
                // Hertz's ellipse: curvatures along (the wheel) and across (the crown in the groove), the
                // equivalent radius and mean semi-axis c, then the axes by the curvature ratio (RollingProfile).
                double along = 1 / (2 * wheelR), across = (1 / CrownR - 1 / GrooveR) / 2;
                double re = 1 / (2 * Math.Sqrt(along * across));
                double c = Math.Cbrt(3 * load0 * re / (4 * (ch.Tyre == Tyre.Steel ? SteelOnAlEStar : NylonEStar)));
                double aspect = Math.Pow(along / across, 2.0 / 3);
                double halfPatch = c / Math.Sqrt(aspect), halfWidth = c * Math.Sqrt(aspect);
                grainPatch = halfPatch;
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

            trackField = auto
                ? new DenseField(HeaderGirth, trackLen, HeaderT, AlE, AlRho, Poisson, f => ThinPanelLoss(f) + HeaderLoss, HeaderFromHz, 16000, rng, dt)
                : new DenseField(SillGirth, trackLen, SillT, AlE, AlRho, Poisson, f => ThinPanelLoss(f) + BeddedLoss + SillBead / f, 60, 16000, rng, dt);
            frameField = new DenseField(FrameGirth, 2 * (width + height), FrameWallT, AlE, AlRho, Poisson,
                                        f => ThinPanelLoss(f) + StorefrontLoss, HeaderFromHz, 16000, rng, dt);
            // Glass loses 0.002; the gaskets and edge seal take about 0.02, more low down (0.1 at 150 Hz).
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
            // The worm's error is mostly its tooth profile (a mesh-rate whine, harmonics as 1/h), each tooth's
            // spacing a tenth of that; random teeth at a third rang the housing instead of a whine.
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

            if (!auto)
            {
                // Built last: they draw nothing from the door's random numbers.
                stileFace = new Port(frameField.PatchMass, WebBacking, frameField.Impedance);
                jambWeb = new Port(trackField.PatchMass, WebBacking, trackField.Impedance);
                pileGrip = new LuGre { MuStatic = ch.Breakaway, MuSliding = 1, StribeckSpeed = PileStribeck, Bristle = 1 / PileSlip };
            }
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

        /// <summary>The height the wheel's contact meets: the rail or a grain's top, whichever is higher, plus
        /// the wheel's own roughness where it touches.</summary>
        private double Surface(int i) => Rail(x + arm[i], out _) + WheelAt(i);

        private double WheelAt(int i)
        {
            double circ = 2 * Math.PI * wheelR;
            double pos = ((x % circ) + circ) % circ;
            double f = pos / wheelStep;
            int k = (int)f; double t = f - k;
            var w = wheelRough[i];
            if (!auto) return Smooth(w[(k + w.Length - 1) % w.Length], w[k % w.Length], w[(k + 1) % w.Length], w[(k + 2) % w.Length], t);
            return w[k % w.Length] * (1 - t) + w[(k + 1) % w.Length] * t;
        }

        /// <summary>Between a patio profile's points, a cubic through its neighbours; drawn straight, every
        /// point was a kink, a faint whistle sweeping up to 16 kHz as the leaf got going.</summary>
        private static double Smooth(double p0, double p1, double p2, double p3, double t)
            => p1 + 0.5 * t * (p2 - p0 + t * (2 * p0 - 5 * p1 + 4 * p2 - p3 + t * (3 * (p1 - p2) + p3 - p0)));

        private double Rail(double at, out Grain? on)
        {
            on = null;
            double f = (at + 0.1) / railStep;
            int k = Math.Clamp((int)f, 0, railRough.Length - 2); double t = Math.Clamp(f - k, 0, 1);
            double h = auto ? railRough[k] * (1 - t) + railRough[k + 1] * t
                            : Smooth(railRough[Math.Max(k - 1, 0)], railRough[k], railRough[k + 1], railRough[Math.Min(k + 2, railRough.Length - 1)], t);
            if (!auto)
            {
                // A patio tyre meets a grain across its patch: the rise averaged under the Hertz pressure. Met
                // at a point, each grain clicked to 20 kHz, round 3's "gritty".
                double best = h;
                for (int j = FirstGrain(at - maxReach - grainPatch); j < grit.Count && grit[j].X <= at + maxReach + grainPatch; j++)
                {
                    var g = grit[j];
                    double proud = g.A - sink;
                    if (proud <= 0 || g.Done) continue;
                    double reach = Math.Sqrt(2 * wheelR * proud);
                    double d = at - g.X;
                    if (d < -reach - grainPatch || d > reach + grainPatch) continue;
                    double lift = 0;
                    for (int m = 0; m < PatchPoints.Length; m++)
                    {
                        double dd = d + PatchPoints[m] * grainPatch;
                        double top = proud - dd * dd / (2 * wheelR);
                        if (top > h) lift += PatchWeights[m] * (top - h);
                    }
                    if (h + lift > best) { best = h + lift; on = g; }
                }
                return best;
            }
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
            // A casual push home arrives at 0.3-0.7 m/s (research notes, item 8): here the run's mean speed, so
            // the slide stays loud into the blow as the recording's shuttings do. At 0.35 m/s a shutting faded
            // into its stop as an opening does.
            shutting = !opening;
            x = opening ? CushionDepth : travel - OpenShort;
            double from = x, to = opening ? travel - OpenShort : -0.004;
            double arrive = opening ? 0 : Math.Abs(to - from) / Math.Max(0.5, travelSeconds);     // m/s at the end
            double tLatch = opening ? 0.05 : -1, tStart = opening ? 0.2 : 0.05;
            double tRun = Math.Max(0.5, travelSeconds);
            double tLetGo = tStart + tRun + (opening ? 0.3 : 0);
            gripFrom = tStart; gripTo = opening ? tLetGo : double.MaxValue;
            small = opening ? smallThrow : 0;
            Settle();
            double end = tStart + tRun + (opening ? 0.8 : 1.4);
            double tShut = -1;
            bool thrown = false;
            while (time < end)
            {
                double t = time;
                if (opening && t >= tLatch && t < tLatch + 0.1) smallForce = -HookSnap;
                else if (!opening && tShut > 0 && t >= tShut + 0.35 && t < tShut + 0.45) { smallForce = HookSnap; if (!thrown) { thrown = true; Log($"{t * 1000:F0} ms  latch thrown"); } }
                else smallForce = 0;
                if (t >= tStart && t < tStart + tRun)
                {
                    double uu = (t - tStart) / tRun;
                    if (opening)
                    {
                        // Rest to rest on the minimum-jerk path (Flash and Hogan): a cubic ended braking at 2.7
                        // m/s^2 and knocked the handle across its play.
                        double d = to - from;
                        handX = from + d * uu * uu * uu * (10 - 15 * uu + 6 * uu * uu);
                        handV = d / tRun * 30 * uu * uu * (1 - uu) * (1 - uu);
                        handA = d / (tRun * tRun) * 60 * uu * (1 - uu) * (1 - 2 * uu);
                    }
                    else
                    {
                        var (p, v) = Hermite(uu, from, 0, to, arrive * Math.Sign(to - from) * tRun);
                        handX = p; handV = v / tRun;
                        double a = Hermite(Math.Min(1, uu + 1e-4), from, 0, to, arrive * Math.Sign(to - from) * tRun).V / tRun;
                        handA = (a - handV) / (1e-4 * tRun);
                    }
                    // Over the last fifth of an opening the arm eases off and the pile stops the leaf: held to its
                    // path, the hand knocked the handle over as the leaf came to rest.
                    armShare = opening ? Math.Clamp((1 - uu) / 0.2, 0, 1) : 1;
                    handOn = !handReleased;
                }
                else if (t >= tStart + tRun && t < tLetGo + HandGrip) { handX = to; handV = 0; handA = 0; if (opening) armShare = 0; }
                else if (t >= tLetGo + HandGrip) { handOn = false; }
                if (!opening && tShut < 0 && x <= CushionDepth) { tShut = t; holdingHome = true; Log($"{t * 1000:F0} ms  home"); }
                // The hook holds the leaf into the bulb in the hand's place; without it the leaf slid back off the
                // bulb half a second after the latch, a thump of its own.
                if (holdingHome && t > tShut + 0.6 && !thrown) holdingHome = false;
                Step();
            }
        }

        public void ScriptAutomatic(bool opening)
        {
            // The lock lifted, the run; shutting, the lock drops a quarter of a second after the leaf is home.
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

        /// <summary>One step of the controller: a run with a check zone into the end, its acceleration ramped
        /// (a step in it surged the leaf against the belt and knocked the clamp). True once at rest at the end.</summary>
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

        /// <summary>A moment with nothing moving, unheard, so the fields start from rest.</summary>
        private void Settle()
        {
            Rest();
            recording = false;
            // A quarter second on a patio door: its glass on the blocks (about 80 Hz, a tenth of critical) still
            // rang a twentieth in, a thump at the head of every render.
            double stop = time + (auto ? 0.05 : 0.25);
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
                double rail = Rail(at, out var grain);
                double h = rail + WheelAt(i) - trackPort[i].X;
                double hRate = (h + trackPort[i].X - lastH[i]) / dt;
                lastH[i] = h + trackPort[i].X;
                double depth = h - zw[i];
                double f = Contact(contactK, contactLambda, depth, hRate - trackPort[i].V - vw[i]);
                Note($"roller{i}", f > 1e-3 ? f : 0);
                // A grain carrying the wheel gives way, a piece at a time.
                if (!auto && ch.Tyre == Tyre.Nylon)
                {
                    // Nylon yields round an overloaded grain and takes it in; once it no longer carries the wheel
                    // it is in the tread. Left on the rail, a half-sunk grain took and released the wheel every few
                    // steps: a 4 kHz buzz at full scale.
                    if (grain != null && !grain.Done && f > NylonYield * 4 * grain.A * grain.A)
                    {
                        grain.A -= grain.A * dt / EmbedSeconds * Math.Min(1, f / (NylonYield * 4 * grain.A * grain.A) - 1);
                        grain.Target = grain.A;
                        grain.Embedding = true;
                        if (grain.A <= PowderMicron * 1e-6) grain.Done = true;
                    }
                    if (lastGrain[i] != null && lastGrain[i] != grain && lastGrain[i]!.Embedding) lastGrain[i]!.Done = true;
                    lastGrain[i] = grain;
                }
                else if (grain != null && !grain.Done && f > GritStrength * 4 * grain.A * grain.A && time - grain.LastCrush > CrushSeconds)
                {
                    grain.LastCrush = time;
                    grain.Target = Math.Max(PowderMicron * 1e-6, grain.A * (0.25 + 0.5 * rng.NextDouble()));
                }
                double trackDrive = trackPort[i].Step(f, dt, out _);
                // The header takes a carriage wheel's force directly, as a plate: through the stiff patch the drive
                // rose 6 dB an octave to 4.6 kHz and hissed. The patio sill keeps its patch, as heard and approved.
                trackField.Modes.Push(trackHit[i], auto ? f - restLoad : trackDrive);
                // The bracket's spring is to the frame's patch, its damping to the leaf's body.
                double leafAt = heave + pitch * arm[i] + framePort[i].X;
                double leafRate = heaveRate + pitchRate * arm[i];
                double fb = bracketK / 2 * (zw[i] - leafAt) + bracketC / 2 * (vw[i] - leafRate);
                double aw = (f - fb) / wheelKg - G;
                vw[i] += aw * dt; zw[i] += vw[i] * dt;
                double frameDrive = framePort[i].Step(fb, dt, out double toLeaf);
                frameField.Modes.Push(frameHit[i], frameDrive);
                host[i] = toLeaf;
                double gasket = GasketK * (framePort[i].X - glassPort[i].X);
                double glassDrive = glassPort[i].Step(gasket + GasketK * glassPort[i].X, dt, out _);
                glassField.Modes.Push(glassHit[i], glassDrive);
                // Rolling resistance, and a grain's slope pushing back.
                double slope = (Rail(at + 1e-4, out _) - rail) / 1e-4;
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

            // ── Weatherstrip: a mean drag and a flutter in the band the fibres' slip rate makes ──
            double slipHz = Math.Min(20000, Math.Abs(u) / PileSlip);
            double white = (rng.NextDouble() * 2 - 1) * Math.Sqrt(3);
            double a1 = 1 - Math.Exp(-2 * Math.PI * Math.Max(slipHz, 1) * dt);
            brushNoise += a1 * (white - brushNoise);
            double moving = Math.Tanh(Math.Abs(u) / 0.005);
            double pile = ch.Pile * moving;
            // A patio leaf's pile grips where it stands and lets go once the pull passes its breakaway.
            sideForce -= auto ? pile * Math.Sign(u) : pileGrip.Force(u, ch.Pile, mass, dt);
            // The one-pole leaves white noise with a1 / (2 - a1) of its variance: put back to one. An automatic
            // leaf's drag is mostly its floor guide; its brushes are about a fifth (all of it as fibres hissed).
            double brushing = auto ? AutoBrushShare : 1;
            double flutter = pile * brushing * 0.3 / Math.Sqrt(auto ? AutoPileFibres : PatioPileFibres) * brushNoise / Math.Sqrt(a1 / (2 - a1));
            if (!auto)
            {
                // Catches of nearly one length: the flutter lies about the slip rate and falls away below it.
                // Left white to nothing, it rang a new door's frame at 60-80 Hz, a boom under the roll.
                pileLow += a1 * (flutter - pileLow);
                flutter = (flutter - pileLow) * Math.Sqrt(2);
                // Through the fibres' own bending to the backing.
                double fa = fibreW * fibreW * (flutter - fibreBend) - 2 * PileZeta * fibreW * fibreRate;
                fibreRate += fa * dt; fibreBend += fibreRate * dt;
                flutter = fibreBend;
            }
            frameField.Modes.Push(brushFrameHit, flutter);
            trackField.Modes.Push(brushTrackHit, flutter);

            // ── The ends: each stile meets its bumper, the leaf following through the stile's spring ──
            double jamb = auto ? BumperLambda : PatioJambLambda;
            double fShut, fOpen = Contact(bumperK, jamb, x + stileD[1] - travel, u + stileV[1]);
            if (auto) fShut = Contact(bumperK, jamb, -(x + stileD[0]), -(u + stileV[0]));
            else
            {
                double face = x + stileD[0], faceRate = u + stileV[0];
                double squeeze = Math.Min(CushionDepth - face, CushionDepth);
                double cushion = squeeze > 0 ? Math.Max(0, CushionK * squeeze * (1 - PatioJambLambda * faceRate)) : 0;
                double metal = Contact(FrameOnFrameK, FrameOnFrameLambda, -face, -faceRate);
                fShut = cushion + metal;
                Note("frame-on-frame", metal);
            }
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
            if (auto)
            {
                double endDrive = endPort.Step(fShut + fOpen, dt, out _);
                frameField.Modes.Push(endFrameHit, endDrive);
                trackField.Modes.Push(endTrackHit, endDrive * 0.5);
            }
            else
            {
                frameField.Modes.Push(endFrameHit, stileFace!.Step(fShut + fOpen, dt, out _));
                trackField.Modes.Push(endTrackHit, jambWeb!.Step(fShut + fOpen, dt, out _));
            }
            Note("shut-bumper", fShut); Note("open-bumper", fOpen);
            // A hand pushing a leaf home lets go as it meets the jamb: it does not lean on the bumper.
            if (auto ? fShut > 0 || fOpen > 0 : shutting && fShut > 0) handReleased = true;
            if (holdingHome) sideForce -= HoldHome + ArmDamping * u;

            // ── Hand (patio) ──
            if (!auto)
            {
                // The hand leans into the leaf's known drag, leaving the arm's stiffness only the slip from its
                // path; without it the arm lagged 3 mm and pulled the stopped leaf on, a knock at the end of every
                // opening.
                handForce = 0;
                if (handOn)
                {
                    double fh = mass * handA + ch.Pile * Math.Tanh(handV / 0.01) + 1200 * (handV - u - handleRate) + 15000 * (handX - x - handle);
                    double g = Math.Clamp(Math.Min(time - gripFrom, gripTo - time) / HandGrip, 0, 1);
                    handForce = Math.Clamp(fh * armShare, -250, 250) * g * g * (3 - 2 * g);
                }
            }

            // ── Drive (automatic) ──
            if (auto)
            {
                double pulleyRev = motorU / (Math.PI * PulleyD);
                double motorRev = pulleyRev * GearRatio;
                meshPhase += motorRev * WormStarts * dt;             // a tooth of the wheel for each start, each turn
                motorPhase += motorRev * MotorSlots * dt;
                // The worm's transmission error moves the belt's driven end and is a force between worm and wheel.
                int teeth = toothError.Length;
                int tooth = (int)(Math.Floor(meshPhase) % teeth + teeth) % teeth;
                double frac = meshPhase - Math.Floor(meshPhase);
                double profile = 0;
                for (int h = 0; h < profilePhase.Length; h++) profile += Math.Sin(2 * Math.PI * (h + 1) * frac + profilePhase[h]) / (h + 1);
                double te = ch.Tooth * (profile + toothError[tooth] * (1 - frac) + toothError[(tooth + 1) % teeth] * frac);
                double beltK = BeltK, beltC = 2 * BeltZeta * Math.Sqrt(BeltK * mass);
                double belt = beltK * (motorX + te * PulleyD / 2 / WormWheelR - clamp) + beltC * (motorU - clampRate);
                double rel = clamp - x, relRate = clampRate - u;
                double lug = Contact(LugK, LugLambda, rel - ClampPlay / 2, relRate) - Contact(LugK, LugLambda, -rel - ClampPlay / 2, -relRate);
                double clampAcc = (belt - lug) / ClampKg;
                clampRate += clampAcc * dt; clamp += clampRate * dt;
                sideForce += lug;
                // Only the knock, through the rail's patch, bends the rail's faces; the steady pull just carries
                // the leaf. Fed whole into the faces, braking onto the clamp rang the frame at 82 dBA.
                frameField.Modes.Push(lugHit, lugPort.Step(lugHigh.Next(lug), dt, out _));
                pDrive += clampNoise.Pressure(clampAcc);
                Note("lug", Math.Abs(lug) > 0 && Math.Abs(rel) > ClampPlay / 2 ? Math.Abs(lug) : 0);
                double load = Math.Abs(belt) + 5;
                double turning = Math.Tanh(Math.Abs(motorRev) / 2);
                gearbox.Push(gearShape, MeshK * te * turning * (0.5 + load / 100));
                double torque = load * PulleyD / 2 / GearRatio;
                drive.F += -belt + 0.1 * torque / 0.03 * Math.Sin(2 * Math.PI * motorPhase);
                drive.Step(dt);
                trackField.Modes.Push(driveHit, drive.Reaction);
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
                double knock = Contact(HandleStopK, HandleStopLambda, rel - ch.HandlePlay, relRate)
                               - Contact(HandleStopK, HandleStopLambda, -rel - ch.HandlePlay, -relRate);
                double centring = HandleCentring * handle + 2 * HandleZeta * Math.Sqrt(HandleCentring * HandleKg) * handleRate;
                // Relative to the leaf. The fingers hold it to the hand at about 150 N s/m (the hand-arm's
                // driving-point impedance is 100-300 N s/m, ISO 10068).
                double hacc = ((handOn ? handForce - GripDamping * handleRate : 0) - knock - centring) / HandleKg - lastLeafAcc;
                handleRate += hacc * dt; handle += handleRate * dt;
                sideForce += knock + centring;
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
