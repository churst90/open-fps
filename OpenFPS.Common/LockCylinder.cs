using System.Collections.Generic;
using System.Text;
using static OpenFPS.Common.DoorPhysics;

namespace OpenFPS.Common;

/// <summary>
/// A key unlocking a door, simulated as the objects: the ring brought up to a pin-tumbler cylinder, the key
/// pushed in over the pins, the shoulder on the plug's face, the key turned until the cam draws the latch.
/// Usable on any keyed door; what the cylinder is set in is what its blows ring.
/// <list type="bullet">
/// <item>The keyring: a 25 mm steel split ring on the bow; each hanging key is a pendulum that strikes its
/// neighbours and the door, and a free plate ringing at its bending modes (about 2.3, 6.4 and 12.6 kHz for a
/// 55 mm key) with almost no loss: the jingle.</item>
/// <item>The key: five cuts to its bitting, 0.38 mm a step, 3.96 mm apart, 100 degree V cuts.</item>
/// <item>The pins: five stacks on springs riding the blade; where a flank turns from falling to rising each
/// reverses at once, a small blow on key and plug. Over a quarter second these are the zip of a key going in.</item>
/// <item>The plug and cam: the cam turns free through its play, picks up the hub with a knock, and draws the
/// latch against its spring until the hub meets its stop.</item>
/// <item>The host: the door, a dense field struck where the cylinder's collar sits.</item>
/// </list>
/// </summary>
public static class LockCylinder
{
    /// <summary>What the cylinder is set in.</summary>
    public enum Host
    {
        /// <summary>A narrow aluminium stile: a storefront or a building's glass front door.</summary>
        AluminiumStile,
        /// <summary>A hollow steel door's 1.2 mm skin.</summary>
        SteelDoor,
        /// <summary>A wooden door.</summary>
        WoodDoor,
    }

    public const double PascalsAtFullScale = 20.0;
    public const int Variants = 4;

    /// <summary>The lab's instrument: when set, each part's pressure alone is written here as key-PART.raw.</summary>
    public static string? StemFolder;

    public sealed class Report
    {
        public readonly List<string> Events = new();
        public double PeakPascals;
        public double DrawnAt = -1;
        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var e in Events) sb.AppendLine("    " + e);
            sb.Append($"    peak {20 * Math.Log10(Math.Max(1e-9, PeakPascals) / 2e-5):F1} dB SPL at 1 m");
            return sb.ToString();
        }
    }

    /// <summary>When, in a render, the latch is drawn: the server sends the door's opening this long after
    /// the key.</summary>
    public const float UnlockSeconds = 0.7f;

    /// <summary>When, after the tip meets the keyway, the hand turns the key (DoorSystem.KeyTurnSeconds).</summary>
    public const float TurnSeconds = 0.45f;

    /// <summary>Each character: keys hanging besides the one in the lock, the reach (s), the push (m/s), the
    /// wear (crest rounding, mm) and the bitting.</summary>
    /// <remarks>One key on every ring: with two, four and six the free keys rang on at 63-82 dB through the
    /// unlock, "like a coin dropping on the ground" (Cody, 2026-10-05), and the single key was the one he
    /// wanted for every door.</remarks>
    private static (int Keys, double Reach, double Insert, double WearMm, int[] Bitting) Character(int variant)
        => (((variant % Variants) + Variants) % Variants) switch
        {
            0 => (1, 0.5, 0.15, 0.05, new[] { 3, 6, 2, 5, 4 }),
            1 => (1, 0.45, 0.18, 0.1, new[] { 5, 2, 7, 3, 6 }),
            2 => (1, 0.55, 0.13, 0.2, new[] { 2, 4, 3, 7, 1 }),
            _ => (1, 0.4, 0.2, 0.3, new[] { 6, 1, 5, 2, 8 }),
        };

    /// <summary>The script's pace: the last 30 mm found slowly (with the approach), the turn.</summary>
    private const double Aim = 0.18, TurnTime = 0.3;

    /// <summary>The unlock: the ring brought up, the key in, turned, the latch drawn and held.</summary>
    /// <remarks>The game's render starts with the key's tip at the keyway (the server's key-insert) and keeps
    /// the server's pace: turned at <see cref="TurnSeconds"/>, the latch drawn by <see cref="UnlockSeconds"/>.
    /// With <paramref name="approach"/> the ring is first brought up to the lock from 20 cm below (the lab's).</remarks>
    public static float[] RenderUnlock(Host host, int variant, int sampleRate, Report? report = null, bool approach = false)
    {
        var sim = new Sim(host, variant, sampleRate, report);
        sim.Script(approach);
        return sim.Output();
    }

    // ── The game ─────────────────────────────────────────────────────────────────────────────────

    public const string KeyPrefix = "lockcylinder:";

    /// <summary>Declared level, dB at a metre: the render's peak (the client puts each render's own in its
    /// place). Measured with AudioLab --door-models, 2026-10-05.</summary>
    public static float LevelDb(Host host) => host switch { Host.SteelDoor => 102f, Host.WoodDoor => 102f, _ => 102f };

    public static string Key(Host host, int variant)
        => FormattableString.Invariant($"{KeyPrefix}unlock:{HostName(host)}:{((variant % Variants) + Variants) % Variants}");

    private static string HostName(Host host) => host switch { Host.SteelDoor => "steel", Host.WoodDoor => "wood", _ => "aluminium" };

    public static bool TryParseKey(string? key, out Host host, out int variant)
    {
        host = Host.AluminiumStile; variant = 0;
        if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var p = key.Substring(KeyPrefix.Length).Split(':');
        if (p.Length != 3 || p[0] != "unlock" || !int.TryParse(p[2], out variant)) return false;
        host = p[1] switch { "steel" => Host.SteelDoor, "wood" => Host.WoodDoor, "aluminium" => Host.AluminiumStile, _ => (Host)(-1) };
        return (int)host >= 0;
    }

    public static float[] RenderKey(string key, int sampleRate) => RenderKey(key, sampleRate, out _);

    public static float[] RenderKey(string key, int sampleRate, out float fullScaleDb)
    {
        fullScaleDb = 0f;
        if (!TryParseKey(key, out var host, out int variant)) return new float[16];
        return KnobDoor.PeakToFullScale(RenderUnlock(host, variant, sampleRate), PascalsAtFullScale, out fullScaleDb);
    }

    // ── Constants, each a property of a part ─────────────────────────────────────────────────────

    private const double G = 9.81;
    /// <summary>Keys: nickel silver (E 125 GPa, 8700 kg/m3), a 55 mm key about 2.3 mm thick through the
    /// blade and 22 mm across the bow, about 6 g. Hanging on the ring by its bow hole, 40 mm from the ring to
    /// its centre of mass.</summary>
    private const double KeyE = 125e9, KeyRho = 8700, KeyLength = 0.055, KeyT = 0.0023, KeyWide = 0.022, KeyKg = 0.006;
    private const double HangLength = 0.04;
    /// <summary>A key hanging from a ring loses little: the ring's grip at its bow hole and the air (0.004).</summary>
    private const double KeyLoss = 0.004;
    /// <summary>Metal on metal between keys, and a key on the door: brass edges on brass faces, Hertz.</summary>
    private const double KeyContactK = 2e9, KeyContactLambda = 0.15;
    /// <summary>The split ring: 25 mm across, 1.5 mm hardened steel wire.</summary>
    private const double RingRadius = 0.0125, RingWire = 0.0015;
    /// <summary>How far the hanging keys sit off the door's face (the bow of the key in the lock stands
    /// 25 mm out, and the ring hangs below it), m.</summary>
    private const double OffFace = 0.012;
    /// <summary>The ring's friction takes a swinging key's motion, about 0.3 of critical (at 0.1 they chattered
    /// on for a second after the hand stopped).</summary>
    private const double SwingZeta = 0.3;

    // The cylinder: Schlage-type spacing, five pins.
    private const int Pins = 5;
    private const double FirstCut = 0.0059, CutSpacing = 0.00396, CutStep = 0.000381, CutAngle = 50 * Math.PI / 180;
    /// <summary>The blade's uncut height above where a pin rests with no key in, and the tip's ramp.</summary>
    private const double BladeTop = 0.004, TipRamp = 0.003;
    private const double KeyInDepth = FirstCut + (Pins - 1) * CutSpacing + 0.004;
    /// <summary>A pin stack: brass key pin and driver, 0.115 in across, about 0.7 g; its spring about 0.6 N at
    /// rest and 120 N/m more as it is lifted.</summary>
    private const double PinKg = 0.0007, PinPreload = 0.6, PinRate = 120;
    private const double PinContactK = 3e9;
    /// <summary>The blade slides on the keyway's wards and the pins' tips: brass on brass, a little grease.</summary>
    private const double BladeFriction = 0.15;
    /// <summary>The plug and shell, brass, about 60 g together, held in the lock body by its set screw: how
    /// the pins' blows reach the door.</summary>
    private const double PlugKg = 0.06, PlugMountK = 4e7, PlugMountZeta = 0.1, PlugVolume = 7e-6;
    /// <summary>The cam: a steel tailpiece with 35 degrees of free play before it meets the hub's lever, which
    /// draws the 12.7 mm latch over the next 70 degrees. The hub and latch as the cam feels them: 8 g at the
    /// cam's 10 mm arm, against the latch spring's 6 N + 400 N/m and its friction.</summary>
    private const double CamPlay = 35 * Math.PI / 180, CamDraw = 70 * Math.PI / 180, CamArm = 0.010, HubKg = 0.008;
    private const double LatchThrow = 0.0127, LatchPreload = 6, LatchRate = 400, LatchFriction = 1.5;
    private const double CamContactK = 4e9, CamContactLambda = 0.15;
    /// <summary>The fingers on the bow: a 22 mm bow, turned through the pads' give (about 0.8 N m/rad), damped
    /// near half critical. The plug and key turn as about 2e-6 kg m2.</summary>
    private const double TurnStiffness = 0.8, TurnDamping = 0.0017, PlugInertia = 2e-6;

    private sealed class Sim
    {
        private readonly int rate;
        private readonly double dt;
        private readonly Report? report;
        private readonly Random rng;
        private readonly int keys;
        private readonly double reach, insert, wear;
        private readonly int[] bitting;
        private double time;

        // The hand and its ring: the ring's point, x across the face, y up, z away from the face.
        private double hx, hy, hz, hvx, hvy, hvz, hax, hay, haz;
        private readonly double[] kx, kz, kvx, kvz, hang;    // each hanging key's offset from below the ring
        private readonly Modes[] keyModes;
        private readonly Modes ring;

        // The key in the lock and the pins.
        private double depth, depthRate;
        private readonly double[] pinY, pinV, pinApproach;
        private readonly double[] pinAt;
        private readonly Modes keyInLock;
        private readonly Mount plug, lockBody;
        private readonly AccelerationNoise plugNoise, hubNoise;
        private readonly AccelerationNoise[] pinNoise;

        // The turn.
        private double turn, turnRate, hub, hubRate, turnAim;
        private bool turning, drawn;

        // The host.
        private readonly DenseField hostField;
        private readonly Port hostPort;
        private readonly double[] hostHit, faceHit;
        private readonly Port facePort;
        private readonly Modes hostLeaf;
        private readonly double[] leafAtLock, leafBelow;

        private readonly List<float> outHi = new();
        private readonly Dictionary<string, (double Start, double Peak, bool On)> contactLog = new();
        private static readonly string[] PeakNames = { "keys", "ring", "pins", "cylinder", "door" };
        private readonly double[] peaks = new double[PeakNames.Length];
        private List<float>[]? stems;

        public Sim(Host host, int variant, int sampleRate, Report? report)
        {
            this.report = report;
            rate = sampleRate * Oversample; dt = 1.0 / rate;
            rng = new Random(7001 + 13 * (((variant % Variants) + Variants) % Variants) + (int)host);
            (keys, reach, insert, double wearMm, bitting) = Character(variant);
            wear = wearMm / 1000;

            // The hanging keys: each a free plate, its bending modes along its length and one across.
            kx = new double[keys]; kz = new double[keys]; hang = new double[keys]; kvx = new double[keys]; kvz = new double[keys];
            keyModes = new Modes[keys];
            for (int i = 0; i < keys; i++)
            {
                double len = KeyLength * (0.85 + 0.3 * rng.NextDouble()), t = KeyT * (0.9 + 0.2 * rng.NextDouble());
                var hzs = new List<double>(); var l = new List<double>(); var m = new List<double>(); var g = new List<double>();
                foreach (double bl in new[] { 4.730, 7.853, 10.996 })
                    AddFreeMode(hzs, l, m, g, Beam(len, t, KeyRho, KeyE, bl), len * KeyWide);
                // Across the bow: a 22 mm free plate, a third of the way along its first bending mode.
                AddFreeMode(hzs, l, m, g, Beam(KeyWide, t, KeyRho, KeyE, 4.730), len * KeyWide);
                keyModes[i] = new Modes(hzs, l, m, g, dt);
                // About 1.5 mm between neighbours' faces, each hanging its own length, so they drift in phase.
                kx[i] = (i - (keys - 1) / 2.0) * (KeyT + 0.0015);
                hang[i] = HangLength * (0.75 + 0.5 * rng.NextDouble());
            }
            {
                var hzs = new List<double>(); var l = new List<double>(); var m = new List<double>(); var g = new List<double>();
                for (int n = 2; n <= 4; n++)
                {
                    double f = Ring(RingRadius, RingWire, 7850, 200e9, n);
                    hzs.Add(f); l.Add(0.003); m.Add(0.0015);
                    double ka = 2 * Math.PI * f / C0 * RingWire;
                    g.Add(SmallPlateGain(2 * RingRadius * RingWire, 0.5 / n) * ka / Math.Sqrt(1 + ka * ka) * 4);
                }
                ring = new Modes(hzs, l, m, g, dt);
            }

            // The key in the lock: a 30 mm cantilever from its shoulder, lossy in the fingers.
            {
                var hzs = new List<double>(); var l = new List<double>(); var m = new List<double>(); var g = new List<double>();
                foreach (double bl in new[] { 1.875, 4.694, 7.855 })
                {
                    double f = Beam(0.03, KeyT, KeyRho, KeyE, bl);
                    hzs.Add(f); l.Add(0.15); m.Add(KeyKg / 4);
                    double ka = 2 * Math.PI * f / C0 * KeyWide / 2;
                    g.Add(SmallPlateGain(0.03 * KeyWide, 0.4) * ka / Math.Sqrt(1 + ka * ka));
                }
                keyInLock = new Modes(hzs, l, m, g, dt);
            }

            pinY = new double[Pins]; pinV = new double[Pins]; pinApproach = new double[Pins];
            pinAt = new double[Pins];
            pinNoise = new AccelerationNoise[Pins];
            for (int k = 0; k < Pins; k++) { pinAt[k] = FirstCut + k * CutSpacing; pinNoise[k] = new AccelerationNoise(PinKg / 8500, dt); }
            plug = new Mount(PlugKg, PlugMountK, PlugMountZeta);
            lockBody = new Mount(0.35, 3e7, 0.15);
            plugNoise = new AccelerationNoise(PlugVolume, dt);
            hubNoise = new AccelerationNoise(HubKg / 7850, dt);

            // The host has its own generator, so it draws nothing from the keys' numbers.
            var hrng = new Random(911 + (int)host);
            hostField = host switch
            {
                Host.SteelDoor => new DenseField(1.0, 2.1, 0.0012, 200e9, 7850, Poisson, SandwichLoss, 300, 16000, hrng, dt),
                Host.WoodDoor => new DenseField(0.9, 2.1, 0.04, 3.1e9, 650, Poisson, f => WoodLoss(f) + 0.01, 300, 16000, hrng, dt),
                _ => new DenseField(0.267, 4.2, 0.0032, 70e9, 2700, Poisson, f => ThinPanelLoss(f) + 0.004, 300, 16000, hrng, dt, DenseField.CapSpacing, 0.089),
            };
            hostPort = new Port(hostField.PatchMass, 2e7, hostField.Impedance);
            // The whole leaf bending on its hinges gives the lock's blows their body.
            {
                double w = host == Host.WoodDoor ? 0.9 : 1.0, h = 2.1, d, rhoH;
                Func<double, double> loss;
                switch (host)
                {
                    case Host.SteelDoor: d = 2 * 200e9 * 0.0012 * 0.0222 * 0.0222 / (1 - Poisson * Poisson); rhoH = 22.2; loss = SandwichLoss; break;
                    case Host.WoodDoor: d = 3.1e9 * 0.04 * 0.04 * 0.04 / (12 * (1 - Poisson * Poisson)); rhoH = 26; loss = f => 0.025; break;
                    default: d = 45000; rhoH = 29.5; loss = f => 0.03 + 3 / f; break;
                }
                var plate = new Plate(w, h, d, rhoH, 0, 1500, true, hrng, 0.03);
                for (int k = 0; k < plate.Loss.Count; k++) plate.Loss[k] += loss(plate.Hz[k]);
                hostLeaf = new Modes(plate.Hz, plate.Loss, plate.Mass, plate.Gain, dt, plate.GainQuad);
                leafAtLock = plate.Shape(w - 0.06, 1.0);
                leafBelow = plate.Shape(w - 0.06, 0.95);
            }
            facePort = new Port(hostField.PatchMass, 2e7, hostField.Impedance);
            hostHit = hostField.Point(); faceHit = hostField.Point();

            // The hand starts 20 cm below and 10 cm out from the lock.
            hx = 0.03; hy = -0.2; hz = 0.1;
        }

        private static void AddFreeMode(List<double> hz, List<double> l, List<double> m, List<double> g, double f, double area)
        {
            hz.Add(f); l.Add(KeyLoss + 2.0 / Math.Max(f, 100)); m.Add(KeyKg / 4);
            // A small free plate radiates from both faces at once: a dipole until it is a wavelength across.
            double ka = 2 * Math.PI * f / C0 * Math.Sqrt(area / Math.PI);
            g.Add(SmallPlateGain(area, 0.5) * ka / Math.Sqrt(1 + ka * ka));
        }

        // ── The script ───────────────────────────────────────────────────────────────────────────

        public void Script(bool approach)
        {
            // A minimum-jerk reach to 30 mm short of the keyway, the last 30 mm slowly, the push, the turn.
            double t0 = time;
            const double shortOf = 0.03;
            if (!approach) { hx = 0; hy = 0; hz = KeyInDepth + 0.001; }
            var from = (hx, hy, hz);
            double aimAt = approach ? -1 : 0, insertAt = approach ? -1 : 0, shoulder = -1, turnAt = -1, end = 3;
            if (!approach) Log("0 ms  the tip at the keyway");
            while (time < end)
            {
                if (aimAt < 0)
                {
                    double s = MinJerk(Math.Clamp((time - t0) / reach, 0, 1));
                    MoveHand(from.hx * (1 - s), from.hy * (1 - s), from.hz * (1 - s) + (KeyInDepth + shortOf) * s);
                    if (time - t0 >= reach) aimAt = time;
                }
                else if (insertAt < 0)
                {
                    double s = MinJerk(Math.Clamp((time - aimAt) / Aim, 0, 1));
                    MoveHand(0, 0, KeyInDepth + shortOf * (1 - s) + 0.001 * s);
                    if (time - aimAt >= Aim) { insertAt = time; Log($"{time * 1000:F0} ms  the tip at the keyway"); }
                }
                else if (shoulder < 0)
                {
                    depthRate = insert * Math.Clamp((time - insertAt) / 0.02, 0, 1);
                    depth += depthRate * dt;
                    if (depth >= KeyInDepth)
                    {
                        depth = KeyInDepth; depthRate = 0; shoulder = time;
                        Log($"{time * 1000:F0} ms  the shoulder on the plug's face");
                    }
                    MoveHand(0, 0, KeyInDepth - depth);
                }
                else if (turnAt < 0)
                {
                    // The hand stops with the key home: the bunch swings on.
                    MoveHand(0, 0, 0);
                    if (time >= insertAt + TurnSeconds) { turnAt = time; turning = true; Log($"{time * 1000:F0} ms  the turn"); }
                }
                else
                {
                    // Turned until it will go no further; the ring goes round with the bow.
                    double tu = Math.Clamp((time - turnAt) / TurnTime, 0, 1);
                    turnAim = MinJerk(tu) * (CamPlay + CamDraw + 0.6);
                    double r = 0.02;
                    MoveHand(r * Math.Sin(turn) * 0.5, -r * (1 - Math.Cos(turn)) * 0.5, 0);
                    if (drawn && end > time + 0.45) end = time + 0.45;
                }
                Tick();
            }
            if (report != null) report.DrawnAt = drawnAt - Math.Max(0, insertAt);
        }

        private double drawnAt = -1;

        /// <summary>The hand (the ring's point) to where the script has it, its acceleration from its motion.</summary>
        private void MoveHand(double x, double y, double z)
        {
            double vx = (x - hx) / dt, vy = (y - hy) / dt, vz = (z - hz) / dt;
            // The arm is not a servo: its motion smooths over about 10 ms.
            double a = 1 - Math.Exp(-dt / 0.004);
            double nvx = hvx + a * (vx - hvx), nvy = hvy + a * (vy - hvy), nvz = hvz + a * (vz - hvz);
            hax = (nvx - hvx) / dt; hay = (nvy - hvy) / dt; haz = (nvz - hvz) / dt;
            hvx = nvx; hvy = nvy; hvz = nvz;
            hx += hvx * dt; hy += hvy * dt; hz += hvz * dt;
        }

        // ── One step ─────────────────────────────────────────────────────────────────────────────

        private void Tick()
        {
            double hostForce = 0, faceForce = 0, ringForce = 0;

            // The hanging keys: pendulums driven by the ring's acceleration.
            var fx = new double[keys]; var fz = new double[keys];
            Array.Clear(fx); Array.Clear(fz);
            double keySum = 0;
            for (int i = 0; i + 1 < keys; i++)
            {
                // The contact includes each key's bending, so the blow takes it back (fed one way, a 6 g key
                // struck at 0.5 m/s rang with nine times the energy it came in with).
                double gap = kx[i + 1] + keyModes[i + 1].At(keyPoint) - kx[i] - keyModes[i].At(keyPoint) - KeyT;
                double gapRate = kvx[i + 1] + keyModes[i + 1].RateAt(keyPoint) - kvx[i] - keyModes[i].RateAt(keyPoint);
                double f = Contact(KeyContactK, KeyContactLambda, -gap, -gapRate);
                if (f > 0)
                {
                    fx[i] -= f; fx[i + 1] += f;
                    keyModes[i].Push(keyPoint, -f);
                    keyModes[i + 1].Push(keyPoint, f);
                    ringForce += f * 0.2;
                }
                keySum += f;
            }
            Note("key-on-key", keySum);
            double doorSum = 0;
            for (int i = 0; i < keys; i++)
            {
                double w0 = Math.Sqrt(G / hang[i]);
                // The face is at z = -OffFace from where the key hangs at rest when the hand is at the lock.
                double zAbs = hz + kz[i] + keyModes[i].At(keyPoint), zRate = hvz + kvz[i] + keyModes[i].RateAt(keyPoint);
                double f = Contact(KeyContactK * 0.5, KeyContactLambda * 2, -(zAbs + OffFace), -zRate);
                if (f > 0) { fz[i] += f; keyModes[i].Push(keyPoint, f); faceForce -= f; doorSum += f; }
                double ax = -w0 * w0 * kx[i] - 2 * SwingZeta * w0 * kvx[i] - hax + fx[i] / KeyKg;
                double az = -w0 * w0 * kz[i] - 2 * SwingZeta * w0 * kvz[i] - haz + fz[i] / KeyKg;
                ringForce += Math.Abs(KeyKg * hay) * 0.05;
                kvx[i] += ax * dt; kx[i] += kvx[i] * dt;
                kvz[i] += az * dt; kz[i] += kvz[i] * dt;
            }
            Note("key-on-door", doorSum);
            if (ringForce != 0) ring.Push(one3, ringForce);

            double plugForce = 0, bladeForce = 0, pinsP = 0;
            for (int k = 0; k < Pins; k++)
            {
                double s = depth - pinAt[k];                  // how far along the key from its tip this pin sits
                double top = s < 0 ? 0 : Profile(s);
                double over = top - pinY[k];
                double rateTop = s < 0 ? 0 : (Profile(s + depthRate * dt) - top) / dt;
                double f = ContactRestitution(PinContactK, 0.5, over, rateTop - pinV[k], ref pinApproach[k]);
                double spring = PinPreload + PinRate * Math.Max(0, pinY[k]);
                double acc = (f - spring) / PinKg;
                if (pinY[k] <= 0 && acc < 0 && f == 0) { acc = 0; pinV[k] = 0; pinY[k] = 0; }
                pinV[k] += acc * dt; pinY[k] += pinV[k] * dt;
                plugForce += f - spring + PinPreload;
                bladeForce += f;
                pinsP += pinNoise[k].Pressure(acc);
                Note($"pin{k + 1}", f);
            }
            keyInLock.Push(one3, bladeForce * 0.3);
            if (depthRate > 0) keyInLock.Push(one3, BladeFriction * bladeForce * 0.2);

            // The shoulder on the plug's face: a short blow, brass on brass through the fingers.
            if (depth >= KeyInDepth && !shoulderDone)
            {
                shoulderDone = true;
                shoulderLeft = 2e-4; shoulderForce = KeyKg * 3 * insert / 2e-4 * 1.5;
            }
            if (shoulderLeft > 0)
            {
                double ph = 1 - shoulderLeft / 2e-4;
                double f = shoulderForce * Math.Sin(Math.PI * ph);
                plugForce += f; keyInLock.Push(one3, f);
                shoulderLeft -= dt;
                Note("shoulder", f);
            }

            double tq = 0, hubForce = 0;
            if (turning)
            {
                tq = TurnStiffness * (turnAim - turn) - TurnDamping * turnRate;
                double camAt = (turn - CamPlay) * CamArm;          // the cam's reach along the hub's travel
                double drawDepth = camAt - hub;
                double fc = Contact(CamContactK, CamContactLambda, drawDepth, turnRate * CamArm - hubRate);
                tq -= fc * CamArm;
                hubForce += fc;
                Note("cam", fc);
                double latch = LatchPreload + LatchRate * hub + LatchFriction * Math.Tanh(hubRate / 0.005);
                hubForce -= hub > 0 || fc > 0 ? latch : 0;
                double fs = Contact(CamContactK, CamContactLambda * 2, hub - LatchThrow, hubRate);
                hubForce -= fs;
                Note("hub-stop", fs);
                if (fs > 0 && !drawn) { drawn = true; drawnAt = time; Log($"{time * 1000:F0} ms  the latch drawn: the hub on its stop"); }
                double hubAcc = hubForce / HubKg;
                if (hub <= 0 && hubAcc < 0) { hubAcc = 0; hubRate = Math.Max(0, hubRate); hub = 0; }
                hubRate += hubAcc * dt; hub += hubRate * dt;
                turnRate += tq / PlugInertia * dt; turn += turnRate * dt;
                plugForce += (fc + fs) * 0.5;
                pinsP += hubNoise.Pressure(hubAcc);
            }

            // The blows reach the door through the plug's set screw and the lock body (a 0.35 kg steel case).
            plug.F += plugForce;
            plug.Step(dt);
            lockBody.F += plug.Reaction;
            lockBody.Step(dt);
            hostForce += lockBody.Reaction;
            hostField.Modes.Push(hostHit, hostPort.Step(hostForce, dt, out double toLeaf));
            hostField.Modes.Push(faceHit, facePort.Step(faceForce, dt, out double toLeaf2));
            hostLeaf.Push(leafAtLock, toLeaf); hostLeaf.Push(leafBelow, toLeaf2);

            double pKeys = 0;
            for (int i = 0; i < keys; i++) pKeys += keyModes[i].Step();
            pKeys += keyInLock.Step();
            double pRing = ring.Step();
            double pCyl = plugNoise.Pressure(plug.Acc);
            double pDoor = hostField.Modes.Step() + hostLeaf.Step();
            double p = pKeys + pRing + pinsP + pCyl + pDoor;
            double[] parts = { pKeys, pRing, pinsP, pCyl, pDoor };
            for (int i = 0; i < parts.Length; i++) peaks[i] = Math.Max(peaks[i], Math.Abs(parts[i]));
            if (StemFolder != null)
            {
                stems ??= new List<float>[parts.Length];
                for (int i = 0; i < parts.Length; i++) (stems[i] ??= new List<float>()).Add((float)(parts[i] / PascalsAtFullScale));
            }
            outHi.Add((float)p);
            time += dt;
        }

        private bool shoulderDone;
        private double shoulderLeft, shoulderForce;
        private readonly double[] one3 = { 1, 1, 1, 1 };
        private readonly double[] keyPoint = { 1, -0.8, 0.6, 0.5 };

        /// <summary>The blade's top edge <paramref name="s"/> metres back from the tip: the tip's ramp, then
        /// V cuts to the bitting, crests and troughs rounded by wear and the pin's tip.</summary>
        private double Profile(double s)
        {
            double ramp = Math.Min(1, s / TipRamp) * BladeTop;
            // Cut j is where pin j sits when the key is home: KeyInDepth - pinAt[j] from the tip.
            double h = BladeTop;
            for (int j = 0; j < Pins; j++)
            {
                double at = KeyInDepth - pinAt[j];
                double bottom = BladeTop - (bitting[j] + 1) * CutStep;
                double flank = Math.Abs(s - at) / Math.Tan(CutAngle);   // rising away from the cut's root
                double v = bottom + flank;
                h = SmoothMin(h, v, wear + 0.00005);
            }
            return Math.Min(ramp, h);
        }

        private static double SmoothMin(double a, double b, double r)
        {
            // Polynomial smooth minimum: a crest rounded to about r.
            double h = Math.Max(r - Math.Abs(a - b), 0) / r;
            return Math.Min(a, b) - h * h * r / 4;
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
                if (c.Peak > 0.5) Log($"{c.Start * 1000:F1} ms  {name}: peak {c.Peak:F1} N, {(time - c.Start) * 1e6:F0} us");
                contactLog[name] = (c.Start, c.Peak, false);
            }
        }

        public float[] Output()
        {
            var sb = new StringBuilder("peaks by part, dB SPL at 1 m:");
            for (int i = 0; i < peaks.Length; i++) sb.Append($" {PeakNames[i]} {20 * Math.Log10(Math.Max(1e-9, peaks[i]) / 2e-5):F0}");
            Log(sb.ToString());
            if (StemFolder != null && stems != null)
                for (int i = 0; i < stems.Length; i++)
                    using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, "key-" + PeakNames[i] + ".raw"))))
                        foreach (var v in stems[i]) f.Write(v);
            var y = Decimate(outHi, rate, PascalsAtFullScale, out double peak);
            if (report != null) report.PeakPascals = peak;
            return y;
        }
    }
}
