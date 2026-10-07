using System.Collections.Generic;
using System.Text;
using static OpenFPS.Common.DoorPhysics;

namespace OpenFPS.Common;

/// <summary>
/// A knob door, simulated as the object it is, and the sound is whatever the object does.
///
/// Cody, 2026-10-03: "are we modeling the doors or modeling the sound? ... we need to model the
/// physical doors." The round before this one fitted a recipe to each event, and every open sounded
/// the same whatever the door was doing. Here nothing is a recipe. There are parts, each with its
/// mass, stiffness and loss, and contacts between them; a hand moves the leaf and turns the knob,
/// and the pressure radiated by everything that vibrates is the sound.
///
/// The parts:
///
///   The LEAF: a rigid rotation about the hinge axis plus its bending modes, from its construction
///   (a hollow-core leaf is two 3 mm fibreboard skins on a 28 mm core; a solid one is 40 mm of wood).
///   The modes and how well each one radiates come from the plate's size, stiffness and mass, and the
///   radiation from the Rayleigh integral over the face, so coincidence and the cancellation of the
///   low modes are not assumed.
///
///   Three HINGES. Each knuckle turns on its pin with dry friction (LuGre), and the knuckle is held to
///   the leaf by its screws, which makes it a small torsional resonator. Friction that falls with
///   sliding speed (a dry or rusty pin) feeds that resonator, and it squeaks; an oiled pin's friction
///   rises with speed and it is silent. Each hinge has its own wear and its own load (the top and
///   bottom hinges carry the leaf's weight as a couple, the middle almost none), so each sings at its
///   own pitch, and only when the leaf is moving slowly enough for its pin to stick.
///
///   The LATCH: a sprung bolt with a 45 degree bevel. Closing, the bevel meets the strike's lip, the
///   lip drives the bolt in, and once past it the spring snaps the bolt out into the strike, against
///   its own stop in the housing. The bolt sits in the strike with play, so a leaf that bounces off
///   the stop hits the keeper with the bolt's flat face. Opening, the knob's spindle takes up its play,
///   picks up the cam and draws the bolt in while the hand's pull presses its flat face on the keeper.
///   Let go of the knob and the spring throws the bolt back out against its stop and the knob back
///   against its rose.
///
///   The STOP: the leaf's face meets the frame's stop at five places (three down the latch edge, two
///   along the head). A real leaf is not flat, so they land at different moments, which is why a shut
///   door is a short flam rather than one tick.
///
///   The FRAME and WALL: what the stop and the strike are fixed to, a plasterboard panel beside the
///   opening, takes every reaction and radiates as a plate.
///
///   HARDWARE: the strike plate, the latch faceplate and housing, and the knob's brass shell each ring
///   at their own modes from their own dimensions. The knob is damped by the hand while it is held.
///
/// The simulation runs at four times the output rate, because metal on metal contacts last tens of
/// microseconds, and is filtered down. Pressure is computed at a metre; <see cref="PascalsAtFullScale"/>
/// converts to samples.
/// </summary>
public static class KnobDoor
{
    /// <summary>How a leaf is built.</summary>
    public enum Construction
    {
        /// <summary>Two 3.2 mm fibreboard skins on a 28 mm paper core with a pine frame: most interior
        /// doors in houses.</summary>
        HollowCore,
        /// <summary>40 mm of solid wood.</summary>
        SolidWood,
    }

    /// <summary>How the door is shut: the latch edge's speed when the hand lets go, m/s.</summary>
    /// <summary>
    /// How the door is shut: its latch edge's speed as it reaches the strike. Nobody has published
    /// building-door closing speeds; the recordings' latch-to-thump gaps (40-150 ms over about 10 mm of
    /// bevel) put a hand close at 0.06-0.5 m/s, guided in to the latch except when it is thrown.
    /// </summary>
    public enum Shut { Gentle, Normal, Hard, Slam }

    /// <summary>The latch edge's speed at the strike for each way of shutting, m/s.</summary>
    public static double StrikeSpeed(Shut how) => how switch
    {
        Shut.Gentle => 0.08, Shut.Normal => 0.22, Shut.Hard => 0.4, _ => 1.2,
    };

    /// <summary>
    /// The latch edge's speed when a slamming hand lets go of the leaf 0.25 m out: what the hand of
    /// <see cref="RenderClose"/> reaches by then from 85 degrees (0.47 m/s on a 1.4 m leaf). It meets the stop at
    /// about 0.42 m/s, 11 dB over a normal close, the gap the one measurement of a slammed interior door against
    /// a normal one gives (US 11,674,342: 84 and 95 dB at 2.1 m).
    /// </summary>
    public const double SlamLetGoSpeed = 0.45;

    /// <summary>One particular door. The same door always sounds like itself.</summary>
    public sealed class Door
    {
        public Construction Leaf = Construction.HollowCore;
        public float Width = 0.9f, Height = 2.1f;
        /// <summary>Hinge wear, top, middle, bottom: 0 oiled, 1 dry and rusty. Null draws them from
        /// the seed.</summary>
        public float[]? HingeWear;
        public int Seed = 1;
    }

    /// <summary>A sample of 1.0 is this many pascals at a metre (120 dB SPL peak). Every render uses
    /// it, so a slam and a gentle close keep their real difference.</summary>
    public const double PascalsAtFullScale = 20.0;

    /// <summary>
    /// How much of the bolt's blow on the keeper bends the strike plate. The blow is along the plate, but
    /// the keeper edge is the root of the plate's curved lip, so some of it bends the plate and rings it:
    /// in a shut the door rebounds onto the bolt before it can snap out, so that blow is the latch's
    /// click. The least known coupling in the door; the lab brackets it (--knob-door latch=X).
    /// </summary>
    public static double KeeperBendsStrike = 0.3;

    /// <summary>The lab's instrument: when set, every render also writes each part's pressure alone,
    /// at the internal rate, as PART.raw (float32) in this folder.</summary>
    public static string? StemFolder;
    /// <summary>The lab's instrument: when set, the bottom hinge's pin is traced here every fourth step.</summary>
    public static List<string>? PinTrace;

    /// <summary>What the render did, for the lab: when each contact happened and how hard.</summary>
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

    /// <summary>Opening: grip, turn, pull, let the knob go, swing to about 85 degrees. With
    /// <paramref name="push"/>, from the stop's side: the hand turns the knob and pushes the leaf away.</summary>
    public static float[] RenderOpen(Door door, int sampleRate, double swingSeconds = 0.9, Report? report = null, bool push = false)
    {
        var sim = new Sim(door, sampleRate, report);
        sim.StartShut();
        sim.ScriptOpen(swingSeconds, push);
        return sim.Output();
    }

    /// <summary>Closing from about 85 degrees: push, let go, the leaf coasts into the frame.</summary>
    public static float[] RenderClose(Door door, Shut how, int sampleRate, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        sim.StartOpen(85.0 * Math.PI / 180.0);
        sim.ScriptHandClose(how);
        return sim.Output();
    }

    // ---------------------------------------------------------------------------------------------
    // The game: what the server names and the client renders.

    public const string KeyPrefix = "knobdoor:";

    /// <summary>How many characters of door there are. A door's id picks one, so the same door always
    /// sounds like itself and the next one along does not.</summary>
    public const int Variants = 4;

    /// <summary>
    /// Each character's hinge wear, top, middle, bottom. Most doors are not squeaky: an earlier round
    /// put a creak on every door and the building sounded haunted. Three in four are oiled or barely
    /// worn; one in four is dry (a merely worn pin, 0.55, never stuck at a game's opening speed, and
    /// Cody heard no squeak at all).
    /// </summary>
    public static float[] WearOf(int variant) => (variant % Variants) switch
    {
        0 => new[] { 0f, 0f, 0f },
        1 => new[] { 0.1f, 0.05f, 0.15f },
        2 => new[] { 0.2f, 0.1f, 0.3f },
        _ => new[] { 0.95f, 0.8f, 1.0f },   // dry and rusty: it sings whenever it turns slowly
    };

    /// <summary>
    /// The level the server declares, dB at a metre. A world sound's level is its buffer's FULL SCALE at a
    /// metre (<see cref="Speech.LevelDb"/>, <see cref="CarWindow.LevelDb"/>), and <see cref="RenderKey"/>
    /// brings a render's peak to full scale, so what a door declares is its render's peak: the median, over
    /// the four characters at the game's 1.1 and 1.4 m leaves, of the model's own (AudioLab --knob-renders,
    /// 2026-10-04, round 4). The client puts each render's own peak in its place once it has rendered it
    /// (WorldAudioPlayer), so the level heard is the model's to the decibel.
    ///
    /// These used to be the model's LAFmax less a 14 dB calibration. Played as full scale, a close whose
    /// crack stands 21-29 dB over its own LAFmax came out that far under it as well: a normal close heard
    /// at 51 dBA at 1.5 m (Cody on "real") where the model puts 86, under a pedestrian's greeting at 60.
    /// Cody, 2026-10-03: "way way way too quiet ... if I'm 5 feet away from a door at these levels I'd
    /// barely know someone opened a door", and no calibration taking level off the doors.
    /// </summary>
    public const float OpenLevelDb = 105.5f;
    /// <summary>A close's declared level by how it was shut: the model's peak, as <see cref="OpenLevelDb"/>. A
    /// gentle and a normal close peak within a few decibels: the peak of both is the bolt snapping out.
    /// (The server sends no slam; the game's render of one comes in thrown, <see cref="SlamLetGoSpeed"/>.)</summary>
    public static float CloseLevelDb(Shut how) => how switch
    {
        Shut.Gentle => 107f, Shut.Hard => 128.5f, Shut.Slam => 129f, _ => 109.5f,
    };

    /// <summary>
    /// What the model puts at a metre, LAFmax (A-weighted, fast, after a 20 Hz high-pass: a leaf's swing
    /// pushes the air at 1-2 Hz, which nobody hears and which leaks 4-8 dB into the A-weighting filter of the
    /// lab's meter at 48 kHz), a metre from the latch. Medians over the same survey. The model's own, not
    /// published figures. The one measurement of an interior door (US 11,674,342: 84 dB normal, 95 slammed,
    /// at 2.1 m, weighting unstated) puts a normal latching close at no more than about 90 dB at 1 m; the
    /// research note's working range (70-82 dBA normal, 60-70 gentle, 85-98 slam) is its own estimate. Kyles'
    /// light wood door opens 14 dB under its closes (9-16); these open 17.5 under a normal close (13.5-21.5),
    /// their turn's clicks being quieter than the recording's.
    /// </summary>
    public const float OpenLafDb = 70.5f;
    public static float CloseLafDb(Shut how) => how switch
    {
        Shut.Gentle => 81.5f, Shut.Hard => 106f, Shut.Slam => 105.5f, _ => 88f,
    };

    /// <summary>
    /// A door's sound for the game. Opening is grip, turn, pull, swing over <paramref name="swingSeconds"/>;
    /// closing is a hand close of the kind <paramref name="how"/> names, sent when the leaf arrives.
    /// </summary>
    /// <remarks>An opening pushed from the stop's side ends ":push"; a pull (the approved one) has no eighth
    /// field, so its keys are as they were.</remarks>
    public static string Key(bool closing, Construction leaf, int variant, float swingSeconds, Shut how,
                             float width, float height, bool push = false)
        => FormattableString.Invariant(
            $"{KeyPrefix}{(closing ? "close" : "open")}:{(leaf == Construction.SolidWood ? "solid" : "hollow")}:{((variant % Variants) + Variants) % Variants}:{(int)MathF.Round(swingSeconds * 100f)}:{(int)how}:{(int)MathF.Round(width * 100f)}:{(int)MathF.Round(height * 100f)}{(push && !closing ? ":push" : "")}");

    public static bool TryParseKey(string? key, out bool closing, out Door door, out float swingSeconds, out Shut how)
        => TryParseKey(key, out closing, out door, out swingSeconds, out how, out _);

    public static bool TryParseKey(string? key, out bool closing, out Door door, out float swingSeconds, out Shut how, out bool push)
    {
        closing = false; door = new Door(); swingSeconds = 0.9f; how = Shut.Normal; push = false;
        if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var p = key.Substring(KeyPrefix.Length).Split(':');
        if (p.Length == 8 && p[7] == "push" && p[0] == "open") { push = true; Array.Resize(ref p, 7); }
        if (p.Length != 7 || (p[0] != "open" && p[0] != "close")) return false;
        if (!int.TryParse(p[2], out int variant) || !int.TryParse(p[3], out int swing) || !int.TryParse(p[4], out int from)
            || !int.TryParse(p[5], out int w) || !int.TryParse(p[6], out int h)) return false;
        closing = p[0] == "close";
        swingSeconds = Math.Clamp(swing / 100f, 0.2f, 5f);
        how = (Shut)Math.Clamp(from, 0, 3);
        door = new Door
        {
            Leaf = p[1] == "solid" ? Construction.SolidWood : Construction.HollowCore,
            Width = Math.Clamp(w / 100f, 0.4f, 1.5f),
            Height = Math.Clamp(h / 100f, 1.5f, 3f),
            HingeWear = WearOf(variant),
            Seed = 1 + variant,
        };
        return true;
    }

    /// <summary>The sound a key names, peak one, as the client's renderer wants it.</summary>
    public static float[] RenderKey(string key, int sampleRate) => RenderKey(key, sampleRate, out _);

    /// <summary>The same, and the level its full scale stands for: the render's own peak, dB SPL at a
    /// metre, which is the level to place it at.</summary>
    public static float[] RenderKey(string key, int sampleRate, out float fullScaleDb)
    {
        fullScaleDb = 0f;
        if (!TryParseKey(key, out bool closing, out var door, out float swing, out Shut how, out bool push)) return new float[16];
        float[] pcm = closing ? RenderGameClose(door, sampleRate, how) : RenderOpen(door, sampleRate, swing, null, push);
        return PeakToFullScale(pcm, PascalsAtFullScale, out fullScaleDb);
    }

    /// <summary>
    /// A model's render (pressure at a metre, in units of <paramref name="pascalsAtFullScale"/>) brought to a
    /// peak of one, with the level that peak is, dB SPL at a metre: a world sound's level is its buffer's
    /// full scale. Shared by the door models.
    /// </summary>
    public static float[] PeakToFullScale(float[] pcm, double pascalsAtFullScale, out float fullScaleDb)
    {
        float peak = 1e-9f;
        foreach (float v in pcm) peak = MathF.Max(peak, MathF.Abs(v));
        for (int i = 0; i < pcm.Length; i++) pcm[i] /= peak;
        fullScaleDb = (float)(20.0 * Math.Log10(peak * pascalsAtFullScale / 2e-5));
        return pcm;
    }

    /// <summary>
    /// Closing as the game sends it, when the leaf arrives: the last 12 degrees of a hand close, so the
    /// bevel's scrape, the latch and the stop fall at their own gaps and the settle follows.
    /// </summary>
    public static float[] RenderGameClose(Door door, int sampleRate, Shut how, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        sim.StartOpen(12 * Math.PI / 180);
        // A slammed leaf has been thrown before the last 12 degrees (a hand lets go of a slam 0.25 m out, and 12
        // degrees of a 0.9-1.1 m leaf is 0.19-0.23 m): it comes in already coasting at its speed. Started at rest
        // and let go at once, it drifted shut at 39 dBA. (The game sends no slam today: see SlamLetGoSpeed.)
        if (how == Shut.Slam) sim.ThrowShut(SlamLetGoSpeed);
        sim.ScriptHandClose(how);
        return sim.Output();
    }

    // ---------------------------------------------------------------------------------------------
    // Constants, each a property of a part. None is a level or a tone.

    private const double G = 9.81;

    // Hollow core: HDF skins (E 4 GPa, 850 kg/m3), 3.2 mm, 28 mm apart; core and frame add 3.1 kg.
    private const double SkinE = 4.0e9, SkinRho = 850, SkinT = 0.0032, CoreDepth = 0.028, CoreAndFrameKg = 3.1;
    // Solid: wood across and along the grain averaged (sqrt of 11 and 0.9 GPa is 3.1), 40 mm.
    private const double SolidE = 3.1e9, SolidRho = 650, SolidT = 0.04;
    /// <summary>Loss a hung leaf has beyond its material: rubbing at hinges and stop, the paint.</summary>
    private const double MountingLoss = 0.01;
    /// <summary>Fibreboard skins (about 0.02) on a glued paper core. Above 0.05 a leaf stops sounding like
    /// wood and starts sounding like plastic, which is what Cody heard in the first round.</summary>
    private const double HollowLoss = 0.03;
    /// <summary>Timber along and across the grain, about 0.015. (The registry's 0.03 for Wood is a
    /// fitted figure for wood as built, joints and all.)</summary>
    private const double SolidWoodLoss = 0.015;
    private const double LeafModeMaxHz = 14000;
    /// <summary>The latch jamb's face: 110 mm of 30 mm pine, its bending across and along the grain averaged
    /// (sqrt of 10 and 0.7 GPa). It bends faster than sound above about 900 Hz, so it gives a blow on its stop
    /// back as 1-4 kHz, as the patio door's jamb walls do. Without it a shut's blow was the leaf and the stud,
    /// 300 Hz-1 kHz, and its 1-4 kHz 6-8 dB under the recordings' (re its own 300 Hz-1 kHz).</summary>
    private const double JambFace = 0.11, JambThickness = 0.03, JambE = 2.6e9;
    /// <summary>What holds a patch of the jamb's face to the stud behind it: as the leaf's skin over its stile.</summary>
    private const double JambBacking = 2e7;

    // Hinges: 3.5 in butt hinges, 1/4 in pin, three screws a leaf into wood.
    private static readonly double[] HingeHeights = { 1.85, 1.05, 0.25 };
    private const double PinRadius = 0.0032;
    /// <summary>The knuckle's end face carries the leaf's weight; mean radius of that face.</summary>
    private const double KnuckleFaceRadius = 0.0045;
    /// <summary>Lateral stiffness of a No. 9 wood screw in pine, about 1.2 kN/mm, three per leaf, at a
    /// mean 25 mm from the pin: the knuckle's torsional stiffness against the leaf.</summary>
    private const double ScrewLateralStiffness = 1.2e6, ScrewArm = 0.025;
    /// <summary>The knuckle and its steel hinge leaf, 60 g at the screw arm, twisting on the screws
    /// against the door's edge (which is the ground here: it is far heavier). That puts the squeak's
    /// resonator near 1.2 kHz.</summary>
    private const double KnuckleMass = 0.06;
    /// <summary>Steel screwed into wood: the joint is lossy.</summary>
    private const double KnuckleLoss = 0.15;
    /// <summary>How much a worn pin's grip varies round it (rust patches, grit), and over what length
    /// one patch gives way to the next: the pin is 20 mm round.</summary>
    private const double SurfacePatchiness = 0.3, SurfaceGrain = 0.0003;

    // Latch: tubular 60 mm backset latch, 12 mm throw, brass bolt.
    private const double BoltMass = 0.012, Throw = 0.012, SpringPreload = 3.5, SpringRate = 350;
    /// <summary>How far the bolt reaches across the gap before it is over the strike.</summary>
    private const double LatchGap = 0.003;
    /// <summary>Play between the bolt's flat face and the keeper when the leaf is on its stop: an eighth of an
    /// inch, as strikes are set so a shut door neither rattles nor binds. The bolt snaps out as its face passes
    /// the keeper, so this, over the edge's speed, is the gap between the latch and the door (Cody: "the door
    /// and the latch are too close together"). At 1.5 mm it was 10 ms; at 3 mm a normal close's is 20-40 ms,
    /// the recordings' 6-38 (Kyles' light wood door, five closes).</summary>
    private const double KeeperPlay = 0.003;
    private const double LatchHeight = 0.95, KnobInset = 0.06;
    /// <summary>The knob on its spindle and the rose on its screws rock in a little play, a tenth of a
    /// millimetre or two; a shut throws them about in it, which is the jiggle after a door closes.</summary>
    private const double KnobPlayMetres = 0.00015, KnobMass = 0.12, KnobVolume = 4e-5;
    /// <summary>The springs' hold on a free knob in its play, N/m (a few newtons over a tenth of a mm).</summary>
    private const double SpindleCentring = 3e4;
    /// <summary>What holds a patch of skin to the stile under it where the stop lands: N/m.</summary>
    private const double SkinOverStile = 2e7;
    /// <summary>The stile's wood under that patch moves with it: 25 mm of a 30 by 35 mm pine stile, 13 g.</summary>
    private const double StileUnderPatch = 0.013;
    /// <summary>The bolt moves in the leaf's plane, but its housing sits in a stile whose faces are the
    /// skins, glued on: a blow along the stile shears into the skins at the edge and bends them. A tenth of
    /// it, against the mortise's eccentricity alone (a millimetre in 35, 0.03), which left the latch's
    /// click inaudible under a shut.</summary>
    private const double LatchBending = 0.1;

    // Knob: 55 mm brass knob (two on the spindle), spindle play 10 degrees, full turn 50 degrees.
    private const double KnobInertia = 6e-5, KnobPlay = 10 * Math.PI / 180, KnobFull = 50 * Math.PI / 180;
    private const double KnobReturnPreload = 0.05, KnobReturnRate = 0.12, KnobStopArm = 0.010;
    /// <summary>A torsional blow on the spindle does not drive an axisymmetric shell's ring modes; the
    /// shank and the set screw make it not quite axisymmetric.</summary>
    private const double KnobShellCoupling = 0.05;
    private const double CamStiffness = 2e6, CamDamping = 40;
    /// <summary>The spindle turning in its rose bushing and the latch's slide: dry friction, N m, and the
    /// grease, N m s.</summary>
    private const double KnobFriction = 0.015, KnobGrease = 2e-4;
    /// <summary>The bolt sliding in its housing, N.</summary>
    private const double BoltFriction = 0.3;
    /// <summary>How hard a hand pulls on a knob while it turns it, N: the bolt's flat face drags on the
    /// keeper under this until it clears, and then the door comes away. Round 1 pulled with a sixth of
    /// it and the opening was a faint click.</summary>
    private const double HandPull = 12;
    /// <summary>A hand pushing a door open from the stop's side leans on the knob as it turns it, harder than a
    /// pull's tug, and comes up to it a little faster: about 20 N in 0.15 s.</summary>
    private const double HandPush = 20, PushRamp = 0.15;
    /// <summary>The arm holding a knob is not a force alone: it gives way to the knob's motion like a damper,
    /// about 150 N s/m at the hand (ISO 10068's hand-arm impedance, as the patio door's handle). While a hand
    /// pulls on a knob it is turning, the leaf comes off its stop onto the keeper against it, and the bolt's
    /// face lands on the keeper without the knock that was the loudest part of an opening (a 55 N metal blow
    /// that rang the leaf, 82 dBA, under 300 Hz, where the recordings' openings are their clicks).</summary>
    private const double PullDamping = 150;
    /// <summary>A hand guiding a door shut does not let go as the bolt drops: it leans on the leaf, its arm
    /// giving way to it as when it pulls, and lets go this long after the leaf meets its stop. (Let go as the
    /// bolt dropped, the leaf bounced off the stop onto the keeper and back three times in 200 ms, each within
    /// 2-9 dB of the shut; the recordings have one blow and nothing within 33 dB of it after. At the patio
    /// door's 300 N s/m the arm all but stopped the leaf in its last 3 mm: it met the stop at 0.02 m/s, 80 ms
    /// after the latch.)</summary>
    private const double LeanDamping = 150, HoldHomeSeconds = 0.15;
    /// <summary>How hard that hand leans on the leaf, N.</summary>
    private const double PressHome = 5;
    /// <summary>The bolt's flat face and the keeper's edge: dry brass on steel, worn to a ridged track by
    /// years of the same drag. The ridges, 0.1 mm apart, are what makes the drag a grind.</summary>
    private const double KeeperRidges = 0.0001, KeeperRidgeDepth = 0.35;
    /// <summary>The rose's stops are a die-cast zinc lug against a nylon bush: a dead contact, not a bell. A
    /// knob let go strikes the rest stop and comes back off it at four tenths of its speed, at any speed (with
    /// Hunt-Crossley's fixed damping a slow knob kept almost all of its speed and bounced twelve times over 190
    /// ms, a rattle where the recordings have one click and a small one after).</summary>
    private const double RoseRestitution = 0.4;
    /// <summary>The other stop on the rose, a few degrees past where the cam has the bolt fully in: a hand
    /// turns a knob until it meets it. (The recordings' turn is two to five clicks over the first 60 ms of an
    /// opening, within 0-6 dB of the release; this stop and the spindle's play taken up are ours, and they stay
    /// 30 dB under the release above 2 kHz: the knob's mass behind a 10 mm lever makes a 1 ms contact.)</summary>
    private const double KnobTurnStop = KnobFull + 5 * Math.PI / 180;
    /// <summary>Where the hand means to turn the knob to: past the stop, so it meets it moving and then
    /// holds it there through its fingers.</summary>
    private const double KnobTurnAim = KnobFull * 1.3;
    /// <summary>The fingers on a 55 mm knob: about 4 N/mm across the pads at the rim, so 3 N m/rad on the
    /// spindle, damped near half critical on the two knobs.</summary>
    private const double GripStiffness = 3, GripDamping = 0.02;
    /// <summary>Brass bolt on the zinc housing's stop: about half its speed comes back.</summary>
    private const double BoltStopLambda = 0.3;
    /// <summary>The latch body (housing and faceplate, 40 g) sits in its bore and mortise: wood
    /// holding it (2e7 N/m) and wood taking its energy away as waves into the leaf's edge (the stile's
    /// in-plane impedance, about 1500 N s/m). It thuds; it does not ring.</summary>
    private const double LatchBodyMass = 0.04, LatchMountStiffness = 2e7, LatchMountDamping = 1500;
    /// <summary>The faceplate's area, flush in the leaf's edge, which radiates the body's motion.</summary>
    private const double FaceplateArea = 0.057 * 0.025;
    /// <summary>The bolt meets the strike metal on metal, but each is held in wood: the bolt sideways
    /// by its sliding fit and the housing's bore (1e7 N/m), the strike plate (25 g) by its two screws
    /// into the jamb (2e7 N/m).</summary>
    private const double BoltSideStiffness = 1e7, StrikeMass = 0.025, StrikeScrewStiffness = 2e7;
    private const double StrikeArea = 0.07 * 0.028;

    // Contacts (Hunt-Crossley): stiffness N/m^1.5 and damping s/m.
    /// <summary>The stop is a 10 by 32 mm pine moulding, tacked and painted onto the jamb's face: each
    /// stretch of it a 50 g strip that the leaf strikes (painted wood on painted wood, a hard contact)
    /// and that is held to the solid jamb along a painted 32 mm wide joint, so it is close to part of
    /// the jamb: wood across the grain, about 0.5 GPa, over that joint is about 150 N per micrometre for
    /// each 300 mm. Against that the leaf's own give decides how long a shut lasts.</summary>
    private const double StopMass = 0.05, StopStiffness = 1.5e8, StopMountDamping = 0.3, StopArea = 0.3 * 0.032;
    /// <summary>Painted face on painted face: flat, so stiffer than a ball on a plate, and the paint
    /// crushes, so about half the speed comes back.</summary>
    private const double WoodContactK = 3e10, WoodContactLambda = 1.0;
    private const double MetalContactK = 4e9, MetalContactLambda = 0.05;

    // The frame: the latch jamb (30 by 110 mm pine) nailed to a 38 by 89 mm stud, 2.4 m between the
    // plates, bending across the wall. The stop's force bends it on its strong axis.
    private const double StudE = 10e9, StudDepth = 0.089, StudWidth = 0.038, StudRho = 500, StudLength = 2.4;
    private const double JambKgPerM = 0.03 * 0.11 * 500;
    /// <summary>Plasterboard (12.5 mm, 8.75 kg/m2) screwed to the stud moves with it for about half a
    /// bending wavelength each side at the frequencies the stop drives (0.23 m at 200 Hz): the board
    /// strip the stud carries, and what radiates.</summary>
    private const double BoardKgPerM2 = 8.75, CarriedBoard = 0.25;
    private const double FrameLoss = 0.03, FrameModeMaxHz = 5000;
    /// <summary>The plasterboard panel beside the opening, between the jamb's stud and the next one
    /// 0.4 m away. The stud carries its edge, and the rest of it lags behind as a plate: the boom of a
    /// slammed door is this wall, not the door.</summary>
    private const double PanelWidth = 0.4, PanelHeight = 2.4, BoardE = 2.5e9, BoardT = 0.0125;
    private const double PanelLoss = 0.03, PanelModeMaxHz = 3000;

    // ---------------------------------------------------------------------------------------------

    private sealed class Hinge
    {
        public double Height, Wear, Load, Twist, TwistRate;
        public LuGre Friction;
        public double MuStatic, MuSliding;
        public double[] Slope = Array.Empty<double>();
        /// <summary>The pin's surface round its circumference: how its grip varies from place to place,
        /// RMS one. A worn pin is patchy with rust and grit, an oiled one is not.</summary>
        public double[] Surface = Array.Empty<double>();
        public double Travel;
        public double PeakSlip, PeakMoment;

        public double SurfaceAt(double travel)
        {
            double circ = 2 * Math.PI * PinRadius, at = (travel % circ + circ) % circ / circ * Surface.Length;
            int i = (int)at; double f = at - i;
            return Surface[i % Surface.Length] * (1 - f) + Surface[(i + 1) % Surface.Length] * f;
        }
    }

    private sealed class Sim
    {
        private readonly Door door;
        private readonly int rate;
        private readonly double dt;
        private readonly Report? report;
        private readonly Random rng;

        // Leaf
        private readonly double width, height, mass, inertia;
        private double theta, omega, prevOmega;
        private readonly Modes leaf, frame, panel;
        private readonly double[,] panelCoupling;
        private readonly double panelRhoH;
        private readonly double frameLength;
        private readonly Plate leafPlate;
        private readonly (double X, double Y, double Warp, double[] Shape)[] stops;
        private readonly double[] latchShape, knobShape;
        private readonly double rigidGain;
        private double rigidLow; // the rigid rotation's acceleration, through the piston's corner

        // Hinges
        private readonly Hinge[] hinges;
        private readonly double knuckleInertia, knuckleStiffness, knuckleDamping;

        // Latch and knob
        private double bolt = Throw, boltRate, knob, knobRate;
        private bool boltInStrike = true, holdingKnob, holdingLeaf;
        private LuGre keeperFriction;
        private double[] keeperSurface = Array.Empty<double>();
        private readonly Modes strike, housing;
        private double latchBody, latchBodyRate;
        private readonly Mount[] mouldings;
        private readonly Mount boltSide, strikeBody;
        private readonly SmallRadiator[] mouldingSound;
        private readonly SmallRadiator strikeSound, latchSound;
        private Modes knobShell;
        private readonly DenseField skinField;
        private HighPass[] skinHigh = Array.Empty<HighPass>();
        private Port[]? skinPorts;
        private readonly DenseField jambField;
        private readonly Port[] jambPorts;
        private readonly double[][] jambAtStops;
        private double[] skinAtLatch = Array.Empty<double>(), skinAtKnob = Array.Empty<double>();
        private double[][]? skinAtStops;
        private readonly AccelerationNoise boltNoise, strikeNoise, latchNoise;
        private readonly AccelerationNoise[] knobNoise = new AccelerationNoise[2];
        private readonly double[] knobPlay = new double[2], knobY = new double[2], knobV = new double[2];
        private double keeperPlay;
        private bool knobsSeated;
        private readonly double[] knobAccNow = new double[2];
        private readonly double[] ones4 = { 1, 1, 1, 1, 1, 1, 1, 1 };
        private readonly double knobHeldLoss = 0.3;
        private double lastGrip, roseApproach, turnStopApproach;
        private readonly double[] knobFreeLoss;

        // The hand
        private Func<double, (double Angle, double Rate)>? leafPath;
        private Func<double, double>? knobPath;
        private double pullTorque, handK, handC;
        private bool pressing;
        private double time;

        private readonly List<float> outHi = new();
        private readonly Dictionary<string, (double Start, double Peak, bool On)> contactLog = new();

        public Sim(Door door, int sampleRate, Report? report)
        {
            this.door = door; this.report = report;
            rate = sampleRate * Oversample; dt = 1.0 / rate;
            rng = new Random(door.Seed);
            width = door.Width; height = door.Height;

            double d, rhoH, loss;
            if (door.Leaf == Construction.HollowCore)
            {
                double arm = (CoreDepth + SkinT) / 2;
                d = 2 * SkinE * SkinT * arm * arm / (1 - Poisson * Poisson);
                mass = 2 * SkinT * SkinRho * width * height + CoreAndFrameKg;
                loss = HollowLoss + MountingLoss;
            }
            else
            {
                d = SolidE * Math.Pow(SolidT, 3) / (12 * (1 - Poisson * Poisson));
                mass = SolidRho * SolidT * width * height;
                loss = SolidWoodLoss + MountingLoss;
            }
            rhoH = mass / (width * height);
            inertia = mass * width * width / 3;
            // A hollow leaf bends as a whole only up to a couple of kilohertz; above that each skin answers
            // for itself, at a fibreboard sheet's own density (a mode every 2 Hz): the dense field below.
            leafPlate = new Plate(width, height, d, rhoH, loss, door.Leaf == Construction.HollowCore ? 2000 : LeafModeMaxHz,
                                  true, rng, 0.04);
            leaf = new Modes(leafPlate.Hz, leafPlate.Loss, leafPlate.Mass, leafPlate.Gain, dt, leafPlate.GainQuad);
            // The rigid rotation radiates as a piston in the doorway: its pressure at a metre per unit
            // angular acceleration is rho/(2 pi) times the face's first moment.
            rigidGain = Rho0 / (2 * Math.PI) * height * width * width / 2;
            if (door.Leaf == Construction.HollowCore)
                skinField = new DenseField(width, height, SkinT, SkinE, SkinRho, Poisson, f => WoodLoss(f) + MountingLoss,
                                           400, 16000, rng, dt);
            else
                skinField = new DenseField(width, height, SolidT, SolidE, SolidRho, Poisson, f => WoodLoss(f) + MountingLoss,
                                           LeafModeMaxHz, 16000, rng, dt);
            skinAtLatch = skinField.Point();
            skinAtKnob = skinField.Point();
            boltNoise = new AccelerationNoise(BoltMass / 8500, dt);
            strikeNoise = new AccelerationNoise(StrikeMass / 7850, dt);
            latchNoise = new AccelerationNoise(LatchBodyMass / 7850, dt);
            for (int i = 0; i < 2; i++)
            {
                knobNoise[i] = new AccelerationNoise(KnobVolume, dt);
                knobPlay[i] = KnobPlayMetres * (0.6 + 0.8 * rng.NextDouble());
            }
            keeperPlay = KeeperPlay * (0.4 + 1.2 * rng.NextDouble());

            frameLength = StudLength;
            frame = FrameModes(dt, rng);
            // The panel: simply supported between the studs, its jamb edge carried by the stud. Its
            // own modes are driven by the inertia of the shape the stud drags it into, (1 - x/a) times
            // the stud's deflection: -rho h times the overlap of each mode with that shape.
            panelRhoH = BoardKgPerM2;
            double boardD = BoardE * BoardT * BoardT * BoardT / (12 * (1 - Poisson * Poisson));
            var panelPlate = new Plate(PanelWidth, PanelHeight, boardD, panelRhoH, PanelLoss, PanelModeMaxHz, false, rng, 0.04);
            panel = new Modes(panelPlate.Hz, panelPlate.Loss, panelPlate.Mass, panelPlate.Gain, dt, panelPlate.GainQuad);
            panelCoupling = new double[panel.N, frame.N];
            for (int k = 0; k < panel.N; k++)
            {
                var (pm, pn) = panelPlate.Index[k];
                var ix = Plate.Integral(x => Math.Sin(pm * Math.PI * x / PanelWidth) * (1 - x / PanelWidth), PanelWidth, 0, pm * Math.PI / PanelWidth);
                for (int j = 0; j < frame.N; j++)
                {
                    int jn = j + 1;
                    var iy = Plate.Integral(y => Math.Sin(pn * Math.PI * y / PanelHeight)
                                                 * Math.Sin(jn * Math.PI * Math.Clamp(y, 0, StudLength) / StudLength),
                                            PanelHeight, 0, (pn + jn) * Math.PI / PanelHeight);
                    panelCoupling[k, j] = ix.re * iy.re;
                }
            }

            // A leaf is never flat: up to a millimetre of wind and bow, its own for each door.
            stops = new (double, double, double, double[])[]
            {
                (width, 0.30, 0, Array.Empty<double>()),
                (width, 1.05, 0, Array.Empty<double>()),
                (width, 1.80, 0, Array.Empty<double>()),
                // The head's stop runs the whole width: two points along it, at the same fractions of any leaf.
                (width * 0.80 / 0.9, height - 0.01, 0, Array.Empty<double>()),
                (width * 0.50, height - 0.01, 0, Array.Empty<double>()),
            };
            for (int i = 0; i < stops.Length; i++)
            {
                double warp = (rng.NextDouble() - 0.3) * 0.0012;
                stops[i] = (stops[i].X, stops[i].Y, warp, leafPlate.Shape(stops[i].X, stops[i].Y));
            }
            // The strike is fitted to the door as it hangs: its keeper stands its play beyond where the bolt sits
            // when the leaf rests on the proudest point of its stop. (Measured from the stop's line instead, a
            // wide leaf resting on a bowed head stood with its bolt half a millimetre into the keeper, and the
            // first step of every opening threw it out with an 8.8 kN blow: 104 dBA, 25 dB over every other.)
            double rest = 0;
            foreach (var st in stops) rest = Math.Max(rest, -st.Warp / st.X);
            keeperPlay += width * rest;
            latchShape = leafPlate.Shape(width, LatchHeight);
            knobShape = leafPlate.Shape(width - KnobInset, LatchHeight);

            // Hinges: the weight's moment is a couple across the top and bottom hinges; the end faces
            // share the weight. Expressed as load at the pin's radius.
            knuckleInertia = KnuckleMass * ScrewArm * ScrewArm;
            knuckleStiffness = 3 * ScrewLateralStiffness * ScrewArm * ScrewArm;
            knuckleDamping = KnuckleLoss * Math.Sqrt(knuckleStiffness * knuckleInertia);
            double couple = mass * G * (width / 2) / (HingeHeights[0] - HingeHeights[2]);
            double axial = mass * G / 3 * KnuckleFaceRadius / PinRadius;
            hinges = new Hinge[3];
            for (int i = 0; i < 3; i++)
            {
                double wear = door.HingeWear != null ? door.HingeWear[i] : Math.Pow(rng.NextDouble(), 1.5);
                var h = new Hinge
                {
                    Height = HingeHeights[i],
                    Wear = wear,
                    Load = (i == 1 ? 0.15 * couple : couple) + axial,
                };
                // Dry steel on steel breaks away at about 0.5 and slides at 0.2, the fall spread over the
                // first few mm/s by rust and grit. Oil takes both to about 0.1 and makes friction rise
                // with speed instead of falling. A pin squeaks when its friction falls faster with speed
                // than the knuckle's own loss can hold: a dry pin sings whenever it turns slowly enough,
                // an oiled one never.
                h.Friction = new LuGre
                {
                    MuStatic = 0.12 + 0.38 * wear,
                    MuSliding = 0.10 + 0.10 * wear,
                    StribeckSpeed = 0.005 - 0.003 * wear,
                    Viscous = 30 * (1 - wear) * (1 - wear),
                };
                h.MuStatic = h.Friction.MuStatic; h.MuSliding = h.Friction.MuSliding;
                h.Surface = SurfaceProfile(rng, 2 * Math.PI * PinRadius, SurfaceGrain);
                h.Slope = new double[leaf.N];
                for (int k = 0; k < leaf.N; k++) h.Slope[k] = leafPlate.SlopeAtHinge(k, h.Height);
                hinges[i] = h;
            }

            mouldings = new Mount[5]; mouldingSound = new SmallRadiator[5];
            for (int i = 0; i < 5; i++) { mouldings[i] = new Mount(StopMass, StopStiffness, StopMountDamping); mouldingSound[i] = new SmallRadiator(StopArea, dt); }
            boltSide = new Mount(BoltMass, BoltSideStiffness, 0.2);
            strikeBody = new Mount(StrikeMass, StrikeScrewStiffness, 0.15);
            strikeSound = new SmallRadiator(StrikeArea, dt);
            latchSound = new SmallRadiator(FaceplateArea, dt);
            keeperFriction = new LuGre { MuStatic = 0.35, MuSliding = 0.2, StribeckSpeed = 0.01, Viscous = 0 };
            keeperSurface = SurfaceProfile(rng, Throw, KeeperRidges);

            // Strike plate: 1.5 mm steel between screws 48 mm apart, clamped, and its curved lip a 20 mm
            // tongue standing out into the opening, free to ring: the "tink" of a latch catching.
            strike = new Modes(new[] { Beam(0.048, 0.0015, 7850, 200e9, 4.730), Beam(0.048, 0.0015, 7850, 200e9, 7.853),
                                       Beam(0.020, 0.0015, 7850, 200e9, 1.875) },
                               new[] { 0.1, 0.1, 0.1 }, new[] { 0.010, 0.010, 0.006 },
                               new[] { SmallPlateGain(0.07 * 0.028, 0.52), SmallPlateGain(0.07 * 0.028, 0.05),
                                       SmallPlateGain(0.020 * 0.028, 0.6) }, dt);
            // Faceplate 2.5 mm steel between screws 45 mm apart; the housing a 0.8 mm tube of 22 mm,
            // tight in its bore. Both lie against the wood, which takes their ring.
            housing = new Modes(new[] { Ring(0.011, 0.0008, 7850, 200e9, 2), Ring(0.011, 0.0008, 7850, 200e9, 3) },
                                new[] { 0.15, 0.15 }, new[] { 0.010, 0.010 },
                                new[] { SmallPlateGain(FaceplateArea, 0.05), SmallPlateGain(FaceplateArea, 0.05) }, dt);
            // The knob: a closed brass ball crimped onto a base disc. A closed ball of 27 mm radius is far
            // too stiff to ring below about 18 kHz (its membrane holds it), so what rings is the base
            // disc, held at its rim by the crimp and at its centre by the shank: a 19 mm wide annulus of
            // 1 mm brass clamped on both edges, which rings like a clamped beam that long, near 10 kHz,
            // with its circumferential variants a little above. The first round modelled the knob as an
            // open cylinder, which is a wine glass, and that is what it sounded like.
            var kh = new List<double>(); var kg = new List<double>(); var km = new List<double>(); var kl = new List<double>();
            double annulus = 0.019, discT = 0.001;
            double f0 = Beam(annulus, discT, 8500, 100e9, 4.730);
            double[] circumferential = { 1.0, 1.12, 1.38 };   // n = 0, 1, 2 round the annulus
            for (int n = 0; n < circumferential.Length; n++)
            {
                kh.Add(f0 * circumferential[n]); km.Add(0.005); kl.Add(knobHeldLoss);
                kg.Add(SmallPlateGain(Math.PI * 0.025 * 0.025, 0.3 / (n + 1)));
            }
            knobFreeLoss = new double[kh.Count];
            // The crimp and the shank take the disc's ring in a few tens of milliseconds.
            for (int i = 0; i < kh.Count; i++) knobFreeLoss[i] = 0.03;
            knobShell = new Modes(kh, kl, km, kg, dt);

            {
                // The latch jamb's face, struck through the stop moulding nailed to it. Built last, from a generator
                // of its own, so it draws nothing from the door's random numbers.
                var jrng = new Random(door.Seed * 7919 + 17);
                jambField = new DenseField(JambFace, height, JambThickness, JambE, StudRho, Poisson, f => WoodLoss(f) + MountingLoss,
                                           300, 16000, jrng, dt);
                jambAtStops = new double[stops.Length][];
                jambPorts = new Port[stops.Length];
                for (int i = 0; i < stops.Length; i++)
                {
                    jambAtStops[i] = jambField.Point();
                    jambPorts[i] = new Port(jambField.PatchMass, JambBacking, jambField.Impedance);
                }
            }

            handK = 170 * inertia;
            handC = 2 * 0.7 * Math.Sqrt(handK * inertia);
        }

        /// <summary>The stud and jamb as a pinned beam carrying its strip of board, radiating as that strip.</summary>
        private static Modes FrameModes(double dt, Random rng)
        {
            double ei = StudE * StudWidth * Math.Pow(StudDepth, 3) / 12;
            double mu = StudWidth * StudDepth * StudRho + JambKgPerM + BoardKgPerM2 * CarriedBoard;
            var hz = new List<double>(); var loss = new List<double>(); var mass = new List<double>();
            var gain = new List<double>(); var quad = new List<double>();
            for (int n = 1; n < 60; n++)
            {
                double k = n * Math.PI / StudLength;
                double f = k * k * Math.Sqrt(ei / mu) / (2 * Math.PI);
                if (f > FrameModeMaxHz) break;
                f *= 1 + 0.04 * (rng.NextDouble() * 2 - 1);
                hz.Add(f); mass.Add(mu * StudLength / 2);
                var (g, q, sigma) = StripRadiation(n, f);
                loss.Add(FrameLoss + Rho0 * C0 * sigma * CarriedBoard / (2 * Math.PI * f * mu));
                gain.Add(g); quad.Add(q);
            }
            return new Modes(hz, loss, mass, gain, dt, quad);
        }

        /// <summary>A strip CarriedBoard wide moving as sin(n pi y / L): its pressure at the listener and
        /// its radiation efficiency, by the same Rayleigh integral as the leaf.</summary>
        private static (double Gain, double Quad, double Sigma) StripRadiation(int n, double f)
        {
            double k = 2 * Math.PI * f / C0, kn = n * Math.PI / StudLength;
            const int rings = 10, spokes = 16;
            double sumP2 = 0, sumW = 0, scale = Rho0 / (2 * Math.PI);
            (double re, double im) Face(double kx, double ky)
            {
                var ix = Plate.Integral(_ => 1.0, CarriedBoard, kx, 0);
                var iy = Plate.Integral(y => Math.Sin(kn * y), StudLength, ky, kn);
                return (ix.re * iy.re - ix.im * iy.im, ix.re * iy.im + ix.im * iy.re);
            }
            for (int i = 0; i < rings; i++)
            {
                double th = (i + 0.5) * (Math.PI / 2) / rings;
                double wgt = Math.Sin(th) * (Math.PI / 2 / rings) * (2 * Math.PI / spokes);
                for (int j = 0; j < spokes; j++)
                {
                    double ph = (j + 0.5) * 2 * Math.PI / spokes;
                    var p = Face(k * Math.Sin(th) * Math.Cos(ph), k * Math.Sin(th) * Math.Sin(ph));
                    sumP2 += (p.re * p.re + p.im * p.im) * wgt; sumW += wgt;
                }
            }
            double meanP2 = sumP2 / sumW * scale * scale;
            var at = Face(k * Math.Sin(Plate.ListenerTheta) * Math.Cos(Plate.ListenerPhi), k * Math.Sin(Plate.ListenerTheta) * Math.Sin(Plate.ListenerPhi));
            double w = 2 * Math.PI * f;
            double power = meanP2 * 2 * Math.PI / (2 * Rho0 * C0);
            double sigma = power / (Rho0 * C0 * CarriedBoard * StudLength * 0.5 * 0.5 / (w * w));
            double mag = Math.Sqrt(at.re * at.re + at.im * at.im);
            if (mag < 1e-30) return (Math.Sqrt(meanP2), 0, Math.Min(sigma, 2.0));
            return (Math.Sqrt(meanP2) * at.re / mag, Math.Sqrt(meanP2) * at.im / mag, Math.Min(sigma, 2.0));
        }

        private double[] FrameShape(double y)
        {
            var s = new double[frame.N];
            for (int n = 0; n < s.Length; n++) s[n] = Math.Sin((n + 1) * Math.PI * Math.Clamp(y + 0.15, 0, frameLength) / frameLength);
            return s;
        }

        public void StartShut()
        {
            // Resting on the stop at whichever point of the leaf stands proudest.
            theta = 0;
            foreach (var s in stops) theta = Math.Max(theta, -s.Warp / s.X);
            omega = 0; bolt = Throw; boltInStrike = true;
            holdingLeaf = false; holdingKnob = false;
        }

        public void StartOpen(double angle)
        {
            theta = angle; omega = 0; bolt = Throw; boltInStrike = false;
            holdingLeaf = true; holdingKnob = false;
            double a0 = angle;
            leafPath = _ => (a0, 0);
        }

        /// <summary>The leaf already swinging shut, its latch edge at <paramref name="edgeSpeed"/>, and no hand on it.</summary>
        public void ThrowShut(double edgeSpeed)
        {
            omega = -edgeSpeed / width; prevOmega = omega;
            holdingLeaf = false; leafPath = null; thrown = true;
        }
        private bool thrown;

        public void ScriptOpen(double swingSeconds, bool push = false)
        {
            const double grip = 0.05, turn = 0.20;
            holdingKnob = true;
            knobPath = t => MinJerk(Math.Clamp((t - grip) / turn, 0, 1)) * KnobTurnAim;
            // While turning, the hand pulls gently: the leaf comes off its stop onto the keeper.
            double cleared = -1, released = -1;
            double end = grip + turn + swingSeconds + 0.6;
            while (time < end)
            {
                // The pull comes up as the knob turns, and the arm gives way to the leaf as it pulls.
                if (time > grip && cleared < 0)
                    pullTorque = push ? Math.Min(1.0, (time - grip) / PushRamp) * HandPush * (width - KnobInset)
                                      : Math.Min(1.0, (time - grip) / turn) * HandPull * (width - KnobInset);
                if (cleared < 0 && !boltInStrike && time > grip)
                {
                    cleared = time;
                    Log($"{time * 1000:F0} ms  bolt clear of the keeper; the hand swings the leaf");
                    // The hand carries on from wherever the door has jumped to, at the speed it jumped.
                    double a0 = theta, w0 = omega, a1 = 85 * Math.PI / 180, t0 = time;
                    holdingLeaf = true; pullTorque = 0;
                    leafPath = t =>
                    {
                        double u = Math.Clamp((t - t0) / swingSeconds, 0, 1);
                        var (pp, vv) = Hermite(u, a0, w0 * swingSeconds, a1, 0);
                        return (pp, vv / swingSeconds);
                    };
                }
                if (cleared > 0 && released < 0 && time > cleared + 0.12)
                {
                    released = time; holdingKnob = false;
                    Log($"{time * 1000:F0} ms  knob let go");
                }
                Tick();
            }
        }

        /// <summary>
        /// A hand shutting the door: it swings it in at an easy pace, slows as the latch comes near and
        /// brings the latch edge to the strike at <see cref="StrikeSpeed"/>. A gentle or normal close is
        /// guided in until the bolt drops into the strike, and from there the hand leans on the leaf until it
        /// has been home a moment; a hard one is let go 30 mm out; a slam is thrown from a quarter of a metre
        /// and coasts.
        /// </summary>
        public void ScriptHandClose(Shut how)
        {
            double vs = StrikeSpeed(how), vMid = Math.Max(vs, 0.9);
            double letGoAt = how switch { Shut.Hard => 0.03, Shut.Slam => 0.25, _ => -1 };
            double e = theta * width, v = 0, t0 = time;
            if (!thrown) leafPath = _ => (e / width, -v / width);
            double released = thrown ? time : -1, firstHit = -1, end = 30, home = -1;
            while (time < end)
            {
                if (released < 0)
                {
                    // The speed the hand wants for where the edge is: easy until 0.2 m out, then down to
                    // the strike speed by 30 mm, and never faster than an arm gets going.
                    double want = vs + (vMid - vs) * Math.Clamp((e - 0.03) / 0.17, 0, 1);
                    v = Math.Min(want, v + 4.0 * dt);
                    // The hand presses home: its target goes 2 mm past the stop, into the moulding.
                    e = Math.Max(-0.002, e - v * dt);
                    // A guided close: once the bolt drops into the strike the hand no longer leads the leaf to a
                    // place but leans on it, the arm giving way to it, and it stays until the leaf has been on its
                    // stop a moment.
                    if (!pressing && letGoAt < 0 && (boltInStrike || contactLog.ContainsKey("stop")))
                    {
                        holdingLeaf = false; pressing = true;
                        Log($"{time * 1000:F0} ms  the hand leans on the leaf, edge {-omega * width:F2} m/s");
                    }
                    if (home < 0 && contactLog.ContainsKey("stop")) home = time;
                    bool caught = home >= 0 && time >= home + HoldHomeSeconds;
                    if ((letGoAt < 0 && caught) || (letGoAt > 0 && e <= letGoAt))
                    {
                        released = time; holdingLeaf = false; pressing = false;
                        Log($"{time * 1000:F0} ms  let go {e * 1000:F0} mm out, edge {-omega * width:F2} m/s");
                    }
                }
                if (firstHit < 0 && contactLog.TryGetValue("stop", out var st)) { firstHit = st.Start; end = firstHit + 1.0; }
                if (time > t0 + 8) break;
                Tick();
            }
        }

        private void Log(string s) => report?.Events.Add(s);

        /// <summary>The lab's instrument: the latch edge's speed as the leaf comes onto a contact and as it
        /// leaves it, so a rebound reads as a ratio.</summary>
        private readonly Dictionary<string, (bool On, double In)> touching = new();
        private void Speeds(string name, bool on)
        {
            if (report == null) return;
            touching.TryGetValue(name, out var t);
            if (on == t.On) return;
            double v = -width * omega;
            if (on) touching[name] = (true, v);
            else
            {
                touching[name] = (false, 0);
                if (Math.Abs(t.In) > 0.005) Log($"{time * 1000:F1} ms  leaf off the {name}: edge in {t.In:F3} m/s, out {v:F3} m/s");
            }
        }

        private void Note(string name, double force)
        {
            contactLog.TryGetValue(name, out var c);
            if (force > 0)
            {
                if (!c.On) { c = (time, force, true); }
                else c.Peak = Math.Max(c.Peak, force);
                contactLog[name] = c;
            }
            else if (c.On)
            {
                Log($"{c.Start * 1000:F1} ms  {name}: peak {c.Peak:F1} N, {(time - c.Start) * 1e6:F0} us");
                contactLog[name] = (c.Start, c.Peak, false);
            }
        }

        private double[]? frameAtLatch, frameAtHead;
        private double[][]? frameAtStops, frameAtHinges;

        private void Tick()
        {
            frameAtLatch ??= FrameShape(LatchHeight);
            if (skinAtStops == null)
            {
                skinAtStops = new double[stops.Length][];
                for (int i = 0; i < stops.Length; i++) skinAtStops[i] = skinField.Point();
                skinPorts = new Port[stops.Length];
                for (int i = 0; i < stops.Length; i++) skinPorts[i] = new Port(skinField.PatchMass + StileUnderPatch, SkinOverStile, skinField.Impedance);
                skinHigh = new HighPass[stops.Length + 2];
                double cross = door.Leaf == Construction.HollowCore ? 400 : LeafModeMaxHz;
                for (int i = 0; i < skinHigh.Length; i++) skinHigh[i] = new HighPass(cross, rate);
            }
            frameAtHead ??= FrameShape(height);
            if (frameAtHinges == null)
            {
                frameAtHinges = new double[hinges.Length][];
                for (int i = 0; i < hinges.Length; i++) frameAtHinges[i] = FrameShape(hinges[i].Height);
            }
            if (frameAtStops == null)
            {
                frameAtStops = new double[stops.Length][];
                for (int i = 0; i < stops.Length; i++) frameAtStops[i] = i < 3 ? FrameShape(stops[i].Y) : frameAtHead;
            }

            double torque = 0;        // on the leaf's rigid rotation
            double handTorque = 0;
            double latchEdgeForce = 0;   // through-thickness force on the leaf at the latch, + opens

            // --- the hand on the leaf
            if (holdingLeaf && leafPath != null)
            {
                var (a, r) = leafPath(time);
                handTorque = handK * (a - theta) + handC * (r - omega);
            }
            handTorque += pullTorque;
            if (pressing) handTorque += -PressHome * (width - KnobInset);
            if (pullTorque != 0 || pressing)
                handTorque -= (pressing ? LeanDamping : PullDamping) * (width - KnobInset) * (width - KnobInset) * omega;
            torque += handTorque;
            // Air: drag on a plate turning about one edge.
            torque -= 0.5 * Rho0 * 1.2 * height * Math.Pow(width, 4) / 4 * omega * Math.Abs(omega);

            bool nearShut = theta * width < 0.02;
            double latchW = 0, latchWRate = 0;
            if (nearShut) { latchW = leaf.At(latchShape); latchWRate = leaf.RateAt(latchShape); }

            // --- the stop
            double stopSum = 0, headSum = 0;
            if (nearShut)
            {
                for (int i = 0; i < stops.Length; i++)
                {
                    var s = stops[i];
                    // The moulding meets a patch of the leaf's skin, over the stile, which gives a little and
                    // passes the blow to the leaf and to the skin around it.
                    var port = skinPorts![i];
                    double pos = s.X * theta + leaf.At(s.Shape) + s.Warp + port.X;
                    double vel = s.X * omega + leaf.RateAt(s.Shape) + port.V;
                    // The moulding is pushed away from the leaf (its X towards the closed side).
                    var m = mouldings[i];
                    double f = Contact(WoodContactK, WoodContactLambda, -(pos + m.X), -(vel + m.V));
                    double skinDrive = port.Step(f, dt, out double toLeaf);
                    skinField.Modes.Push(skinAtStops![i], skinDrive);
                    if (toLeaf != 0)
                    {
                        torque += toLeaf * s.X;
                        leaf.Push(s.Shape, toLeaf);
                    }
                    if (f > 0) m.F += f;
                    if (i < 3) stopSum += f; else headSum += f;
                }
            }
            for (int i = 0; i < mouldings.Length; i++)
            {
                double r = mouldings[i].Reaction;
                // The moulding is nailed to the jamb's face: its push lands on a patch of that face, which passes it
                // to the frame behind and sends what it can into the face's own bending.
                jambField.Modes.Push(jambAtStops[i], jambPorts[i].Step(-r, dt, out double host));
                if (host != 0) frame.Push(frameAtStops[i], host);
            }
            Note("stop", stopSum);
            Note("stop-head", headSum);
            Speeds("stop", stopSum + headSum > 0);

            // --- the latch
            double edge = width * theta + latchW, edgeRate = width * omega + latchWRate;
            double boltForce = SpringPreload + SpringRate * (Throw - bolt);   // the spring, outward
            double latchBodyForce = 0; // along the bolt, outward, on the housing and faceplate
            double strikeForce = 0;    // what reaches the strike plate, normal to it
            double frameForce = 0;     // what the jamb takes at the strike, along the closing direction

            if (bolt > LatchGap)
            {
                if (!boltInStrike)
                {
                    // On the bevel: how far the bolt reaches past where the lip lets it be. The bolt's
                    // place across the gap is the leaf's edge plus its give in its bore; the lip's is the
                    // strike plate's give on its screws.
                    double across = edge + boltSide.X - strikeBody.X, acrossRate = edgeRate + boltSide.V - strikeBody.V;
                    // The strike plate gives under the bevel and springs back at its own modes, so the bolt
                    // meets it again: the few re-strikes in a latch's click.
                    double strikeGive = 0;
                    for (int k = 0; k < strike.N; k++) strikeGive += strike.Q[k];
                    double over = (bolt - LatchGap) - (across - keeperPlay) - strikeGive / Math.Sqrt(2);
                    double overRate = boltRate - acrossRate;
                    if (across > keeperPlay && across < keeperPlay + Throw)
                    {
                        double fn = Contact(MetalContactK, MetalContactLambda, over / Math.Sqrt(2), overRate / Math.Sqrt(2));
                        double slide = (boltRate + acrossRate) / Math.Sqrt(2);
                        // The bevel scrapes over the lip's worn track: the same ridges as the keeper.
                        double ridge = keeperSurface[(int)(Math.Clamp(bolt / Throw, 0, 0.9999) * keeperSurface.Length)];
                        double ft = 0.2 * Math.Max(0.3, 1 + KeeperRidgeDepth * ridge) * fn * Math.Tanh(slide / 0.002);
                        double onBolt = (-fn - ft) / Math.Sqrt(2);
                        double sideways = (fn - ft) / Math.Sqrt(2);
                        boltForce += onBolt;
                        boltSide.F += sideways;
                        strikeBody.F -= sideways;
                        strikeForce += -onBolt;
                        Note("bevel", fn);
                    }
                    else Note("bevel", 0);
                    if (edge + boltSide.X - strikeBody.X <= keeperPlay && bolt > LatchGap)
                    {
                        boltInStrike = true;
                        Log($"{time * 1000:F1} ms  bolt over the strike");
                    }
                }
                if (boltInStrike)
                {
                    // The flat face against the keeper when the leaf comes back.
                    double fk = Contact(MetalContactK, MetalContactLambda, edge + boltSide.X - strikeBody.X - keeperPlay,
                                        edgeRate + boltSide.V - strikeBody.V);
                    boltSide.F -= fk;
                    strikeBody.F += fk;
                    strikeForce += fk * KeeperBendsStrike;
                    if (fk > 0)
                    {
                        double ridge = keeperSurface[(int)(Math.Clamp(bolt / Throw, 0, 0.9999) * keeperSurface.Length)];
                        // A trough can only take the grip so far down; it never goes negative.
                        double groove = Math.Max(0.3, 1 + KeeperRidgeDepth * ridge);
                        keeperFriction.MuStatic = 0.35 * groove;
                        keeperFriction.MuSliding = 0.2 * groove;
                        double ff = keeperFriction.Force(boltRate, fk, BoltMass, dt);
                        boltForce -= ff;
                        strikeForce += ff * 0.3;   // the keeper's edge is in the plate's plane
                    }
                    else keeperFriction.Z = 0;
                    Note("keeper", fk);
                    Speeds("keeper", fk > 0);
                }
            }
            else if (boltInStrike)
            {
                Note("keeper", 0);
                boltInStrike = false;
                Log($"{time * 1000:F1} ms  bolt in: clear of the keeper");
            }

            // --- the knob and its cam
            double gripTorque = 0;
            if (holdingKnob && knobPath != null)
            {
                // The fingers turn the knob through the give of their pads: a stiff spring to where the hand
                // is turning, so the knob has its own inertia and meets its stops.
                double k1 = knobPath(time), k1Rate = (k1 - lastGrip) / dt;
                lastGrip = k1;
                gripTorque = GripStiffness * (k1 - knob) + GripDamping * (k1Rate - knobRate);
            }
            double camArm = Throw / (KnobFull - KnobPlay);
            double cam = Math.Clamp((knob - KnobPlay) * camArm, 0, Throw);
            double camRate = knob > KnobPlay && cam < Throw ? knobRate * camArm : 0;
            double camDepth = bolt - (Throw - cam);
            double fCam = camDepth > 0 && knob > KnobPlay ? Math.Max(0, CamStiffness * camDepth + CamDamping * (boltRate + camRate)) : 0;
            boltForce -= fCam;
            latchBodyForce += fCam * 0.5;
            Note("cam", fCam);
            // The bolt's own stop in the housing, which sits in the wood on its mount.
            double fStop = Contact(MetalContactK, BoltStopLambda, bolt - Throw - latchBody, boltRate - latchBodyRate);
            boltForce -= fStop;
            latchBodyForce += fStop;
            Note("bolt-stop", fStop);

            // The knobs and their spindle: return spring, cam, the hand when it holds them, and the rose's two
            // stops, one at rest and one at the end of the turn. Both knobs turn with the spindle.
            double fRose, fTurnStop;
            {
                double tq = knob > 0 ? -(KnobReturnPreload + KnobReturnRate * knob) : 0;
                tq -= fCam * camArm;
                tq -= KnobFriction * Math.Tanh(knobRate / 0.5) + KnobGrease * knobRate;
                tq += gripTorque;
                fRose = ContactRestitution(MetalContactK, RoseRestitution, -knob * KnobStopArm, -knobRate * KnobStopArm, ref roseApproach);
                fTurnStop = ContactRestitution(MetalContactK, RoseRestitution, (knob - KnobTurnStop) * KnobStopArm, knobRate * KnobStopArm, ref turnStopApproach);
                tq += (fRose - fTurnStop) * KnobStopArm;
                knobRate += tq / (2 * KnobInertia) * dt;
                knob += knobRate * dt;
            }
            Note("rose", fRose);
            Note("turn-stop", fTurnStop);
            fRose += fTurnStop;

            // --- hinges
            double alphaAcc = (omega - prevOmega) / dt; // the leaf's angular acceleration, last step
            prevOmega = omega;
            for (int i = 0; i < hinges.Length; i++)
            {
                var h = hinges[i];
                // The leaf's knuckle turning on the pin, which the frame's knuckles hold.
                double slip = PinRadius * (omega + h.TwistRate);
                h.Travel += slip * dt;
                double patch = Math.Max(0.3, 1 + SurfacePatchiness * h.Wear * h.SurfaceAt(h.Travel));
                h.Friction.MuStatic = h.MuStatic * patch;
                h.Friction.MuSliding = h.MuSliding * patch;
                // The hand's push loads the hinges too, a share of its force at the knob.
                double load = h.Load + Math.Abs(handTorque) / width * 0.3;
                double f = h.Friction.Force(slip, load, knuckleInertia / (PinRadius * PinRadius), dt);
                double moment = knuckleStiffness * h.Twist + knuckleDamping * h.TwistRate;
                double acc = (-f * PinRadius - moment) / knuckleInertia - alphaAcc;
                h.TwistRate += acc * dt;
                h.Twist += h.TwistRate * dt;
                torque += moment;
                leaf.Push(h.Slope, moment);
                // The pin's grip goes straight into the frame's knuckles and the jamb, as a couple across
                // the hinge leaf's screws: the unfiltered stick and slip, where its harmonics come from.
                frame.Push(frameAtHinges![i], f * PinRadius / ScrewArm);
                if (PinTrace != null && i == 2 && ((long)(time * rate)) % 4 == 0)
                    PinTrace.Add($"{time:F5} {slip * 1000:F3} {f:F2} {h.Friction.Z * 1e6:F4} {moment:F4}");
                h.PeakSlip = Math.Max(h.PeakSlip, Math.Abs(slip));
                h.PeakMoment = Math.Max(h.PeakMoment, Math.Abs(moment));
            }

            // --- forces into the bodies
            // The bolt's give in its bore pushes the leaf; the strike's on its screws pushes the jamb.
            latchEdgeForce += boltSide.Reaction;
            frameForce += strikeBody.Reaction;
            if (latchEdgeForce != 0)
            {
                torque += latchEdgeForce * width;
                leaf.Push(latchShape, latchEdgeForce);

            }
            // The latch body on its mount; what the mount passes on goes into the leaf's edge.
            double mount = LatchMountStiffness * latchBody + LatchMountDamping * latchBodyRate;
            double latchBodyAcc = (latchBodyForce - mount) / LatchBodyMass;
            leaf.Push(latchShape, mount * LatchBending);


            // The knobs rock in their play as the leaf is thrown about: each is its own body, held to the
            // leaf only by metal at either end of its play, so when the leaf is stopped dead the knob
            // carries on, strikes, and comes back. Force and reaction are the same contact, so nothing can
            // feed itself (a knob shaken by a filtered copy of the leaf's motion ran away).
            double knobArm = width - KnobInset;
            double leafAtKnob = knobArm * theta + leaf.At(knobShape), leafRateAtKnob = knobArm * omega + leaf.RateAt(knobShape);
            if (!knobsSeated) { for (int i = 0; i < 2; i++) { knobY[i] = leafAtKnob; knobV[i] = leafRateAtKnob; } knobsSeated = true; }
            for (int i = 0; i < 2; i++)
            {
                double gap = knobY[i] - leafAtKnob, gapRate = knobV[i] - leafRateAtKnob;
                // A hand on the knob holds the spindle, and with it both knobs: they go with the leaf.
                if (holdingKnob) { knobY[i] = leafAtKnob; knobV[i] = leafRateAtKnob; knobAccNow[i] = 0; continue; }
                double hit = Contact(MetalContactK, 0.3, Math.Abs(gap) - knobPlay[i], Math.Sign(gap) * gapRate);
                // The latch's and the roses' springs load the spindle, so a free knob is centred in its play
                // and only a real jolt throws it to the end.
                double centring = -SpindleCentring * gap - 2 * 0.3 * Math.Sqrt(SpindleCentring * KnobMass) * gapRate;
                double onKnob = -Math.Sign(gap) * hit + centring;
                double knobAcc = onKnob / KnobMass;
                knobV[i] += knobAcc * dt; knobY[i] += knobV[i] * dt;
                torque -= onKnob * knobArm;
                if (hit > 0)
                {
                    leaf.Push(knobShape, -onKnob);

                    knobShell.Push(ones4, hit * 0.3);
                }
                knobAccNow[i] = knobAcc;
                Note(i == 0 ? "knob-rattle" : "rose-rattle", hit);
            }
            if (fStop != 0) housing.Push(ones4, fStop);
            double spindle = fCam + fRose;
            if (spindle != 0) knobShell.Push(ones4, spindle * KnobShellCoupling);
            if (strikeForce != 0) strike.Push(ones4, strikeForce);
            if (frameForce != 0) frame.Push(frameAtLatch, frameForce);

            // Knob shell damping: the hand holds it.
            SetKnobLoss(holdingKnob);

            // --- rigid motion
            double alpha = torque / inertia;
            omega += alpha * dt;
            theta += omega * dt;
            boltForce -= BoltFriction * Math.Tanh(boltRate / 0.01);
            double boltAcc = boltForce / BoltMass;
            boltRate += boltAcc * dt;
            bolt += boltRate * dt;
            latchBodyRate += latchBodyAcc * dt;
            latchBody += latchBodyRate * dt;
            foreach (var m in mouldings) m.Step(dt);
            boltSide.Step(dt);
            strikeBody.Step(dt);

            // --- radiate
            for (int k = 0; k < panel.N; k++)
            {
                double drive = 0;
                for (int j = 0; j < frame.N; j++) drive += panelCoupling[k, j] * frame.Acc[j];
                panel.F[k] -= panelRhoH * drive;
            }
            double pLeaf = leaf.Step(), pFrame = frame.Step() + panel.Step(), pStrike = strike.Step(), pKnob = knobShell.Step();
            pFrame += jambField.Modes.Step();
            double pLatch = housing.Step() + latchSound.Pressure(latchBodyAcc) + latchNoise.Pressure(latchBodyAcc)
                          + boltNoise.Pressure(boltAcc);
            pStrike += strikeSound.Pressure(strikeBody.Acc) + strikeNoise.Pressure(strikeBody.Acc);
            // The skins ride on the leaf: driven, above where its own modes stop, by its motion where the
            // blows land, through their impedance, so they take what the leaf has and no more.
            skinField.Modes.Push(skinAtLatch, skinField.Impedance * skinHigh[stops.Length].Next(width * omega + leaf.RateAt(latchShape)));
            skinField.Modes.Push(skinAtKnob, skinField.Impedance * skinHigh[stops.Length + 1].Next((width - KnobInset) * omega + leaf.RateAt(knobShape)));
            pLeaf += skinField.Modes.Step();
            pKnob += knobNoise[0].Pressure(knobAccNow[0]) + knobNoise[1].Pressure(knobAccNow[1]);
            double pStop = 0;
            for (int i = 0; i < mouldings.Length; i++) pStop += mouldingSound[i].Pressure(mouldings[i].Acc);
            double p = pLeaf + pFrame + pStrike + pLatch + pKnob + pStop;
            // The rigid rotation: a piston, whose pressure follows its acceleration only below the
            // frequency where the leaf is a wavelength across; above that it follows velocity.
            double corner = C0 / (2 * Math.PI * Math.Sqrt(width * height / Math.PI));
            double aLp = 1 - Math.Exp(-2 * Math.PI * corner * dt);
            rigidLow += aLp * (alpha - rigidLow);
            // Only while the leaf closes its own opening: open, its two faces cancel.
            double baffle = Math.Clamp(1 - theta / 0.15, 0, 1);
            double pRigid = rigidGain * rigidLow * baffle;
            p += pRigid;
            Peak(0, pLeaf); Peak(1, pRigid); Peak(2, pFrame); Peak(3, pStrike); Peak(4, pLatch); Peak(5, pKnob); Peak(6, pStop);
            if (StemFolder != null)
            {
                stems ??= new List<float>[7];
                double[] parts = { pLeaf, pRigid, pFrame, pStrike, pLatch, pKnob, pStop };
                for (int i = 0; i < 7; i++) (stems[i] ??= new List<float>()).Add((float)(parts[i] / PascalsAtFullScale));
            }

            outHi.Add((float)p);
            time += dt;
        }

        private List<float>[]? stems;
        private readonly double[] peaks = new double[7];
        private static readonly string[] PeakNames = { "leaf", "piston", "frame", "strike", "latch", "knob", "stop" };
        private void Peak(int i, double p) => peaks[i] = Math.Max(peaks[i], Math.Abs(p));

        private bool knobHeldState = true;
        private void SetKnobLoss(bool held)
        {
            if (held == knobHeldState) return;
            knobHeldState = held;
            // Rebuild the shell's step with the new loss, keeping its state.
            var q = (double[])knobShell.Q.Clone(); var v = (double[])knobShell.V.Clone();
            var loss = new double[knobShell.N];
            for (int i = 0; i < loss.Length; i++) loss[i] = held ? knobHeldLoss : knobFreeLoss[i];
            var fresh = new Modes(knobShell.Hz, loss, knobShell.Mass, knobShell.Gain, dt);
            Array.Copy(q, fresh.Q, q.Length); Array.Copy(v, fresh.V, v.Length);
            knobShell = fresh;
        }

        public float[] Output()
        {
            foreach (var kv in contactLog)
                if (kv.Value.On) Log($"{kv.Value.Start * 1000:F1} ms  {kv.Key}: peak {kv.Value.Peak:F1} N (still touching)");
            if (StemFolder != null && stems != null)
                for (int i = 0; i < stems.Length; i++)
                    using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, PeakNames[i] + ".raw"))))
                        foreach (var v in stems[i]) f.Write(v);
            var parts = new StringBuilder("peaks by part, dB SPL at 1 m:");
            for (int i = 0; i < peaks.Length; i++) parts.Append($" {PeakNames[i]} {20 * Math.Log10(Math.Max(1e-9, peaks[i]) / 2e-5):F0}");
            Log(parts.ToString());
            Log($"modes: leaf {leaf.N} ({(leaf.N > 0 ? leaf.Hz[0] : 0):F0}-{(leaf.N > 0 ? leaf.Hz[leaf.N - 1] : 0):F0} Hz), skins {skinField.Modes.N}, frame {frame.N}, panel {panel.N}");
            for (int i = 0; i < hinges.Length; i++)
                Log($"hinge {i} (wear {hinges[i].Wear:F2}, load {hinges[i].Load:F0} N): peak pin slip {hinges[i].PeakSlip * 1000:F1} mm/s, peak moment on the leaf {hinges[i].PeakMoment:F3} N m");
            var y = Decimate(outHi, rate, PascalsAtFullScale, out double peak);
            if (report != null) report.PeakPascals = peak;
            return y;
        }
    }
}
