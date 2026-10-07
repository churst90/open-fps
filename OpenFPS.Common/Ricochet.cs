using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// What a round is in flight: its mass, its size, and how fast it is turning end over end. A bullet
/// from the barrel is point first and spin-stable (no tumble); after a ricochet it is a deformed slug,
/// flattened on the side that met the face, tumbling.
/// </summary>
public readonly record struct Slug(float MassKg, float Length, float Width, float Thickness, float TumbleRadPerSec)
{
    public static Slug Of(WeaponDefinition w)
    {
        float d = w.BulletDiameterMetres > 0f ? w.BulletDiameterMetres : 0.009f;
        float l = w.BulletLengthMetres > 0f ? w.BulletLengthMetres : 0.0156f;
        float m = w.BulletMassKg > 0f ? w.BulletMassKg : 124f * WeaponRegistry.Grain;
        return new Slug(m, l, d, d, 0f);
    }

    public bool Tumbling => TumbleRadPerSec > 0f;

    /// <summary>Its volume, m³: a box of its three sizes with the corners taken off (π/4).</summary>
    public float Volume => MathF.PI / 4f * Length * Width * Thickness;

    /// <summary>kg/m³: a jacketed lead core is about 10,000.</summary>
    public float Density => MassKg / MathF.Max(1e-9f, Volume);

    /// <summary>The mean area it presents tumbling at random: a quarter of its surface, for any convex
    /// body (Cauchy's theorem).</summary>
    public float MeanArea => 0.5f * (Length * Width + Length * Thickness + Width * Thickness);

    /// <summary>Its moment of inertia end over end, kg m².</summary>
    public float PitchInertia => MassKg * (Length * Length + Width * Width) / 12f;

    /// <summary>Across, as the wake sees it: the geometric mean of its two short sides.</summary>
    public float Across => MathF.Sqrt(Width * Thickness);
}

/// <summary>
/// A round striking a face at a grazing angle, and what it does next.
///
/// WHETHER. Below a critical angle off the face a round does not dig in: the face pushes it back out
/// and it leaves, flattened and tumbling. The critical angle is a property of the pair (the face's
/// hardness and yielding, the bullet's construction and speed) and is measured by firing at it; the
/// figures here are the ones forensic reconstruction works with (L. C. Haag, Shooting Incident
/// Reconstruction, 2nd ed., 2011; Haag, "Bullet ricochet from water", AFTE J. 11(3), 1979), as ranges
/// for common handgun and rifle bullets, the middle taken. Soft stuff (grass, wood, plaster, carpet)
/// takes the round in instead, at any angle. Water is worked from the physics: a body skips off water
/// below θc = 18°/√(ρ_body/ρ_water) (G. Birkhoff et al., "Ricochet off water", AMP Memo 42.4M, 1944,
/// confirmed by W. Johnson and S. R. Reid, J. Mech. Eng. Sci. 17, 1975), 5.6° for a lead-cored bullet.
/// Within ±15 % of the critical angle the outcome is a matter of the particular round, and is drawn.
///
/// HOW IT LEAVES. Off a hard, non-yielding face (steel, concrete, stone) the departure angle is LESS
/// than the incidence, a fraction of it; off a yielding one (water, soil) it is greater, the round
/// having ploughed a ramp (Haag). It keeps most of its speed: the loss rises towards the critical
/// angle, to about 35 % there (the figure Haag and the Wikipedia article give for a deflection). It
/// leaves a few degrees to one side or the other of straight on.
///
/// WHAT IT IS AFTERWARDS. Flattened where it met the face (shorter, wider, thinner), lighter by what it
/// left behind, and TUMBLING: the blow's impulse at the end of the slug spins it end over end at
/// I·(L/4)/J. In flight, a tumbling body's drag is a bluff body's (Cd 1.0 subsonic, rising to 1.45
/// supersonic; S. F. Hoerner, Fluid-Dynamic Drag, 1965) on the mean area it presents, several times a
/// bullet's: it slows in tens of metres, and it has no stability to keep it straight. Its tumble
/// relaxes, over a time 4J/(ρvAL²), towards the rate air holds a tumbling flat body at, its tip moving
/// at about 0.3 of its speed (H. J. Lugt, "Autorotation", Annu. Rev. Fluid Mech. 15, 1983).
///
/// WHAT IT SOUNDS LIKE: the strike (<see cref="BulletImpact"/>) with the energy it lost, and then THE
/// WHINE: an asymmetric body tumbling hundreds of times a second turns its side force round with it,
/// and a rotating force radiates at its rotation rate and harmonics, as a propeller does (Gutin). The
/// torque-free tumble of an asymmetric body is quasi-periodic, two rates not in a simple ratio, so the
/// whine is two lines and their harmonics, wandering as the axis precesses; on top, the bluff wake's
/// broadband noise, swelling each time the slug turns broadside. All of it falls in pitch as the slug
/// slows and goes away, Doppler-shifted for each listener exactly as the whizz is
/// (<see cref="WhineKey"/>). Recorded ricochets (Freesound 486343, 523403, 148827, 78092: .22 and air
/// rifle slugs) show the same: a broadband strike, then harmonic lines from 0.7 to 4 kHz falling over
/// a second.
/// </summary>
public static class Ricochet
{
    public const string WhinePrefix = "bullet:whine:";

    /// <summary>A face's part in a ricochet: the critical angle off the face (degrees; zero: never),
    /// the departure angle as a share of the incidence, the share of its speed it loses at the critical
    /// angle, and the share of its mass the slug keeps.</summary>
    public readonly record struct Face(float CriticalDegrees, float DepartureRatio, float LossAtCritical, float MassKept, float Flatten);

    private static readonly Dictionary<string, Face> _faces = new(StringComparer.OrdinalIgnoreCase)
    {
        // Hard, non-yielding: departure under incidence.
        ["Metal"] = new(25f, 0.25f, 0.35f, 0.92f, 0.7f),
        ["Concrete"] = new(15f, 0.35f, 0.35f, 0.85f, 1f),
        ["Marble"] = new(18f, 0.3f, 0.3f, 0.88f, 1f),
        ["Tile"] = new(15f, 0.3f, 0.35f, 0.88f, 1f),
        ["Brick"] = new(12f, 0.4f, 0.4f, 0.85f, 0.9f),
        // Asphalt yields a little: a lower angle, departure nearer incidence.
        ["Asphalt"] = new(10f, 0.6f, 0.4f, 0.95f, 0.6f),
        // Yielding: departure over incidence.
        ["Gravel"] = new(8f, 1.0f, 0.5f, 0.95f, 0.4f),
        ["Dirt"] = new(5f, 1.4f, 0.5f, 1f, 0.2f),
        ["Water"] = new(-1f, 1.3f, 0.4f, 1f, 0.05f),   // -1: Birkhoff, from the slug's density
    };

    /// <summary>A face's part, or none (the round goes in, at any angle).</summary>
    public static bool TryFace(string? material, out Face face)
    {
        face = default;
        return material != null && _faces.TryGetValue(material, out face);
    }

    /// <summary>The critical angle off a face for this slug, radians; zero where a round never skips.</summary>
    public static float CriticalRadians(string? material, Slug slug)
    {
        if (!TryFace(material, out var f)) return 0f;
        float degrees = f.CriticalDegrees >= 0f ? f.CriticalDegrees : 18f / MathF.Sqrt(MathF.Max(1f, slug.Density / 1000f));
        return degrees * MathF.PI / 180f;
    }

    /// <summary>What a ricochet left: where it goes (velocity), what it is now, how much energy it left
    /// in the face, and the angle it struck at.</summary>
    public readonly record struct Outcome(Vector3 Velocity, Slug Slug, float DumpedJoules, float GrazeRadians, float Kept);

    /// <summary>
    /// Whether a round striking <paramref name="material"/> at <paramref name="velocity"/> on a face
    /// whose outward normal is <paramref name="normal"/> ricochets, and if so how it leaves. The
    /// <paramref name="dice"/> decide what a particular round does near the critical angle, its exact
    /// departure and its sideways kick; the same dice give the same ricochet.
    /// </summary>
    public static bool TryBounce(string? material, Vector3 velocity, Vector3 normal, Slug slug, Random dice, out Outcome outcome)
    {
        outcome = default;
        float speed = velocity.Length();
        if (speed < 30f || !TryFace(material, out var face)) return false;
        Vector3 dir = velocity / speed;
        normal = Vector3.Normalize(normal);
        float into = -Vector3.Dot(dir, normal);
        if (into <= 0f) return false;
        float graze = MathF.Asin(Math.Clamp(into, 0f, 1f));
        float critical = CriticalRadians(material, slug);
        float drawn = critical * (0.85f + 0.3f * (float)dice.NextDouble());
        if (graze >= drawn) return false;

        float ratio = graze / MathF.Max(1e-4f, critical);
        float departure = Math.Clamp(face.DepartureRatio * graze * (0.75f + 0.5f * (float)dice.NextDouble()),
                                     0.3f * MathF.PI / 180f, 30f * MathF.PI / 180f);
        float kept = Math.Clamp(1f - face.LossAtCritical * ratio * (0.8f + 0.4f * (float)dice.NextDouble()), 0.3f, 0.97f);

        // Along the face, turned a little to one side, then lifted off it by the departure angle.
        Vector3 along = dir - Vector3.Dot(dir, normal) * normal;
        along = along.LengthSquared() < 1e-10f ? Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitX + new Vector3(0, 0, 1e-3f))) : Vector3.Normalize(along);
        double g = Math.Sqrt(-2 * Math.Log(1 - dice.NextDouble())) * Math.Cos(2 * Math.PI * dice.NextDouble());
        float side = (float)(g * 2.5 * Math.PI / 180);
        along = Vector3.Normalize(Vector3.Transform(along, Quaternion.CreateFromAxisAngle(normal, side)));
        Vector3 leaving = Vector3.Normalize(along * MathF.Cos(departure) + normal * MathF.Sin(departure));
        float speedOut = speed * kept;

        // Flattened on the side that met the face, by how hard and how squarely it met it.
        float squash = face.Flatten * Math.Clamp(0.4f + 2f * MathF.Sin(graze), 0.4f, 1f);
        float mass = slug.MassKg * face.MassKept;
        float length = slug.Length * (1f - 0.15f * squash);
        float thick = slug.Thickness * (1f - 0.3f * squash);
        float width = slug.Width * (1f + 0.3f * squash);
        var after = new Slug(mass, length, width, thick, 0f);
        // The tumble the blow starts: the impulse into the face, at a quarter of the slug's length from
        // its centre, over its moment of inertia; added to any it had.
        float impulse = slug.MassKg * (speed * MathF.Sin(graze) + speedOut * MathF.Sin(departure));
        float spin = impulse * 0.25f * length / after.PitchInertia;
        after = after with { TumbleRadPerSec = MathF.Max(50f, spin + slug.TumbleRadPerSec * 0.5f) };

        float dumped = 0.5f * slug.MassKg * speed * speed - 0.5f * mass * speedOut * speedOut;
        outcome = new Outcome(leaving * speedOut, after, MathF.Max(0f, dumped), graze, kept);
        return true;
    }

    // ── A tumbling slug in flight ───────────────────────────────────────────────────────────────

    /// <summary>A tumbling body's drag coefficient on its mean presented area: 1.0 subsonic, rising
    /// through the transonic to 1.45 (Hoerner).</summary>
    public static float TumblingDrag(float mach)
    {
        if (mach <= 0.8f) return 1.0f;
        if (mach >= 1.2f) return 1.45f;
        return 1.0f + 0.45f * (mach - 0.8f) / 0.4f;
    }

    public static Vector3 Acceleration(Vector3 velocity, Vector3 wind, Slug slug, Air air)
    {
        Vector3 through = velocity - wind;
        float speed = through.Length();
        float k = 0.5f * air.Density * TumblingDrag(speed / air.SpeedOfSound) * slug.MeanArea / MathF.Max(1e-5f, slug.MassKg);
        return -k * speed * through + new Vector3(0f, -ExternalBallistics.Gravity, 0f);
    }

    /// <summary>Flies a tumbling slug on, as <see cref="ExternalBallistics.Advance"/> flies a bullet.</summary>
    public static void Advance(ref BulletState s, float seconds, Slug slug, Air air, Vector3 wind)
    {
        float left = seconds;
        while (left > 1e-7f)
        {
            float h = MathF.Min(ExternalBallistics.StepSeconds, left);
            Vector3 a0 = Acceleration(s.Velocity, wind, slug, air);
            Vector3 v1 = s.Velocity + a0 * h;
            Vector3 a1 = Acceleration(v1, wind, slug, air);
            Vector3 v = s.Velocity + 0.5f * (a0 + a1) * h;
            Vector3 step = 0.5f * (s.Velocity + v) * h;
            s.Position += step;
            s.Travelled += step.Length();
            s.Velocity = v;
            s.Seconds += h;
            left -= h;
        }
    }

    /// <summary>The tip-speed ratio a tumbling flat body settles at (Lugt).</summary>
    public const float TipSpeedRatio = 0.3f;

    /// <summary>The tumble rate air holds it at, rad/s: tip speed 0.3 of its speed.</summary>
    public static float AutorotationRadPerSec(float speed, Slug s) => 2f * TipSpeedRatio * speed / MathF.Max(1e-3f, s.Length);

    /// <summary>
    /// The slug's flight along its line for the whine, in still air and without gravity (over the
    /// second it is heard it falls a few metres at most, at the far end): emission time, distance
    /// along, speed, and the tumble's phase, every <paramref name="step"/> seconds, until it has gone
    /// <paramref name="range"/> metres, has slowed under 25 m/s, or 2.5 s have passed.
    /// </summary>
    public static List<(float T, float S, float V, float Phase, float Omega)> Line(float speed, Slug slug, float range, Air air, float step = 2.5e-4f)
    {
        var line = new List<(float, float, float, float, float)>();
        float t = 0f, s = 0f, v = speed, phase = 0f;
        float omega0 = slug.TumbleRadPerSec, auto0 = AutorotationRadPerSec(speed, slug);
        while (true)
        {
            float auto = AutorotationRadPerSec(v, slug);
            float relax = 4f * slug.PitchInertia / MathF.Max(1e-12f, air.Density * MathF.Max(5f, v) * slug.MeanArea * slug.Length * slug.Length);
            float omega = auto + (omega0 - auto0) * MathF.Exp(-t / MathF.Max(1e-3f, relax));
            omega = MathF.Max(0.2f * auto, omega);
            line.Add((t, s, v, phase, omega));
            if (s >= range || v < 25f || t > 2.5f) break;
            float k = 0.5f * air.Density * TumblingDrag(v / air.SpeedOfSound) * slug.MeanArea / slug.MassKg;
            float dv = -k * v * v * step;
            s += (v + 0.5f * dv) * step;
            v += dv;
            phase += omega * step;
            t += step;
        }
        return line;
    }

    // ── The whine's key ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One listener's whine, quantised: the slug's speed leaving (m/s), how far it flies (dm), the
    /// listener's distance off its line (dm) and along it from the ricochet (dm, signed), the speed of
    /// sound (m/s), the air's density (g/m³), the slug's mass (mg), length, width and thickness (tenths
    /// of a mm), its tumble leaving (rad/s), and which of three pieces.
    /// </summary>
    public readonly record struct Whine(int Speed, int RangeDm, int MissDm, int AlongDm, int SoundSpeed, int DensityGrams,
                                        int MassMg, int LengthTenthMm, int WidthTenthMm, int ThickTenthMm, int Tumble, int Piece)
    {
        public float Miss => MathF.Max(0.3f, MissDm / 10f);
        public float Along => AlongDm / 10f;
        public float Range => MathF.Max(0.5f, RangeDm / 10f);
        public float SpeedOfSound => MathF.Max(200f, SoundSpeed);
        public Air Air => AirFor(SoundSpeed, DensityGrams);
        public Slug Slug => new(MathF.Max(1e-4f, MassMg * 1e-6f), MathF.Max(1e-3f, LengthTenthMm * 1e-4f),
                                MathF.Max(1e-3f, WidthTenthMm * 1e-4f), MathF.Max(5e-4f, ThickTenthMm * 1e-4f), MathF.Max(50f, Tumble));
    }

    /// <summary>The air a speed of sound and a density describe (its temperature from the one, its
    /// pressure from both).</summary>
    private static Air AirFor(int soundSpeed, int densityGrams)
    {
        float c = MathF.Max(200f, soundSpeed);
        float tc = (c / 331.3f) * (c / 331.3f) * 273.15f - 273.15f;
        float rho = densityGrams > 0 ? densityGrams / 1000f : 1.225f;
        return new Air(tc, rho * 287.05f * (tc + 273.15f) / 100f);
    }

    public static string WhineKey(Whine w) => string.Create(CultureInfo.InvariantCulture,
        $"{WhinePrefix}{w.Speed}:{w.RangeDm}:{w.MissDm}:{w.AlongDm}:{w.SoundSpeed}:{w.DensityGrams}:{w.MassMg}:{w.LengthTenthMm}:{w.WidthTenthMm}:{w.ThickTenthMm}:{w.Tumble}:{w.Piece}");

    public static bool TryParseWhine(string? key, out Whine w)
    {
        w = default;
        if (key == null || !key.StartsWith(WhinePrefix, StringComparison.Ordinal)) return false;
        var p = key[WhinePrefix.Length..].Split(':');
        if (p.Length != 12) return false;
        var v = new int[12];
        for (int i = 0; i < 12; i++)
            if (!int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]) || (i != 3 && v[i] < 0)) return false;
        if (v[0] < 1 || v[4] < 200 || v[11] > 2 || v[6] < 1) return false;
        w = new Whine(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11]);
        return true;
    }

    /// <summary>A listener further than this from a ricochet's line is sent no whine.</summary>
    public const float WhineMetres = 60f;

    /// <summary>The share of the dynamic pressure the tumbling slug's side force swings by, on its
    /// mean area: an estimate (a flat plate's normal force runs from 0 to about 1.2 q A as it turns).</summary>
    public const float SideForceSwing = 0.6f;

    /// <summary>The bluff wake's fluctuating lift coefficient: a circular cylinder's is 0.1-0.6
    /// (C. Norberg, J. Fluids Struct. 17, 2003); 0.2 is taken.</summary>
    public const float BluffLift = 0.2f;

    /// <summary>The harmonics of the first tumble rate, and the weight of the second, quasi-periodic one.</summary>
    private static readonly float[] Harmonics = { 1f, 0.7f, 0.35f, 0.2f };
    private const float SecondLine = 0.5f;

    /// <summary>The piece boundaries, metres along the line: from where the slug is subsonic, to a
    /// miss distance short of abeam, a miss distance past, and to its end.</summary>
    private static bool Joins(Whine w, List<(float T, float S, float V, float Phase, float Omega)> line,
                              out float a, out float b, out float c, out float d)
    {
        a = b = c = d = 0f;
        int first = line.FindIndex(x => x.V < 0.95f * w.SpeedOfSound);
        if (first < 0) return false;
        a = line[first].S;
        d = line[^1].S;
        if (d - a < 0.2f) return false;
        float abeam = Math.Clamp(w.Along, a, d);
        b = Math.Clamp(abeam - w.Miss, a, d);
        c = Math.Clamp(abeam + w.Miss, a, d);
        return true;
    }

    /// <summary>One piece: where along the line it is placed, metres, and the stretch of the line it
    /// carries. False for an empty piece.</summary>
    public static bool TryPiece(Whine w, out float lo, out float hi, out float x)
    {
        lo = hi = x = 0f;
        var line = Line(w.Speed, w.Slug, w.Range, w.Air);
        if (!Joins(w, line, out float a, out float b, out float c, out float d)) return false;
        lo = w.Piece switch { 0 => a, 1 => b, _ => c };
        hi = w.Piece switch { 0 => b, 1 => c, _ => d };
        if (hi - lo < 0.05f) return false;
        float m = w.Miss, abeam = Math.Clamp(w.Along, a, d);
        x = w.Piece switch { 0 => MathF.Max(lo, abeam - 2f * m), 1 => Math.Clamp(abeam, lo, hi), _ => MathF.Min(hi, abeam + 2f * m) };
        return true;
    }

    /// <summary>The tonal and broadband rms at a metre, Pa, of the slug at a speed and tumble rate.</summary>
    private static (float Tonal, float Noise) Strengths(Slug s, float v, float omega, Air air) => Strengths(s, v, omega, air, 0f);

    /// <summary>The same, counting only what a listener hearing it at Doppler factor
    /// <paramref name="doppler"/> can hear of it: the harmonics under 16 kHz once raised, and the
    /// share of the wake's noise left under it (0: all of it).</summary>
    private static (float Tonal, float Noise) Strengths(Slug s, float v, float omega, Air air, float doppler)
    {
        float q = 0.5f * air.Density * v * v;
        float force = q * s.MeanArea * SideForceSwing;
        // A force turning at Ω radiates its rate of change: k·Ω·F_k / (4π c) at a metre for harmonic k,
        // with a random orientation of the tumble axis taking 1/√3 of it on average.
        float tonal = 0f;
        bool Heard(float multiple) => doppler <= 0f || multiple * omega / (2f * MathF.PI) * doppler < 16000f;
        for (int k = 0; k < Harmonics.Length; k++) if (Heard(k + 1)) tonal += Sq((k + 1) * omega * force * Harmonics[k]);
        if (Heard(1.9f)) tonal += Sq(1.75f * omega * force * SecondLine);
        tonal = MathF.Sqrt(tonal) / (4f * MathF.PI * air.SpeedOfSound) / MathF.Sqrt(2f) / MathF.Sqrt(3f);
        float noise = air.Density * v * v * v * s.Length * BluffLift * BulletFlyby.Strouhal / (4f * MathF.Sqrt(2f) * air.SpeedOfSound);
        if (doppler > 0f)
            noise *= MathF.Sqrt(BulletFlyby.WakeShareBelow(16000f / doppler, BulletFlyby.Strouhal * MathF.Max(1f, v) / s.Across / 0.894f));
        return (tonal, noise);
    }

    private static float Sq(float x) => x * x;

    /// <summary>The pressure, Pa at a metre, that a whine buffer's full scale stands for: the loudest
    /// the slug gets at this listener (tonal peaks plus four times the noise's rms, through the
    /// distance and the convective factor), carried to a metre by the piece's own distance, the same
    /// for all three pieces so they join without a step.</summary>
    public static float WhineFullScalePascals(Whine w)
    {
        var line = Line(w.Speed, w.Slug, w.Range, w.Air);
        if (!Joins(w, line, out float a, out _, out _, out float d)) return 1e-3f;
        float max = 1e-6f;
        float b = w.Miss, c = w.SpeedOfSound;
        float rk = 0f;
        for (int piece = 0; piece < 3; piece++)
            if (TryPiece(w with { Piece = piece }, out _, out _, out float xk))
                rk = MathF.Max(rk, MathF.Sqrt(b * b + Sq(w.Along - xk)));
        foreach (var p in line)
        {
            if (p.S < a || p.S > d) continue;
            float dx = w.Along - p.S;
            float r = MathF.Sqrt(b * b + dx * dx);
            float conv = 1f / MathF.Max(0.05f, 1f - p.V / c * dx / r);
            var (tonal, noise) = Strengths(w.Slug, p.V, p.Omega, w.Air, conv);
            max = MathF.Max(max, (tonal * 1.6f * MathF.Sqrt(2f) + 4f * noise) * conv * conv / r);
        }
        return max * MathF.Max(1f, rk);
    }

    /// <summary>
    /// One piece of the whine as a listener hears it. Each output sample is worked back to the emission
    /// time whose sound arrives then (the slug's line is tabulated, and t(τ) = τ + R(τ)/c is monotonic
    /// once it is subsonic), giving its speed, its tumble's phase, its distance and its Doppler factor.
    /// The tonal part is the rotating side force's harmonics at that phase (the first rate and its
    /// harmonics, and a second rate 1.6-1.9 times it), each wandering in phase as the axis precesses
    /// and the rate jittering a few per cent; the noise is the bluff wake's (<see cref="ShapedNoise"/>)
    /// at f/D, its level swelling when the slug is broadside. Harmonics that the Doppler would carry
    /// over 16 kHz are faded out.
    /// </summary>
    public static float[] RenderWhine(Whine w, int sampleRate, int seed) => RenderWhine(w, sampleRate, seed, out _);

    /// <summary>The same, saying how far over full scale its loudest sample would have been.</summary>
    public static float[] RenderWhine(Whine w, int sampleRate, int seed, out float rawPeak)
    {
        rawPeak = 0f;
        if (!TryPiece(w, out float lo, out float hi, out float xk)) return new float[16];
        var slug = w.Slug;
        var air = w.Air;
        var line = Line(w.Speed, slug, w.Range, air);
        Joins(w, line, out float a, out float jb, out float jc, out float d);
        float b = w.Miss, c = w.SpeedOfSound;
        float fullScale = WhineFullScalePascals(w);
        float rk = MathF.Sqrt(b * b + Sq(w.Along - xk));

        // Arrival of each tabulated emission: t = τ + R/c.
        var arrive = new float[line.Count];
        for (int i = 0; i < line.Count; i++) arrive[i] = line[i].T + MathF.Sqrt(b * b + Sq(w.Along - line[i].S)) / c;
        int ia = line.FindIndex(x => x.S >= a);
        if (ia < 0) return new float[16];
        float tFirst = arrive[ia], tLast = arrive[^1];
        float Heard(float s)
        {
            int i = Math.Clamp(line.FindIndex(x => x.S >= s), ia, line.Count - 1);
            return arrive[i];
        }
        float join1 = Heard(jb), join2 = Heard(jc);
        float start = MathF.Max(tFirst, Heard(lo) - (w.Piece == 0 ? 0f : Crossfade));
        float end = MathF.Min(tLast, Heard(hi) + (w.Piece == 2 ? 0f : Crossfade));
        if (end <= start) return new float[16];

        uint hash = BulletFlyby.Mix(BulletFlyby.Mix((uint)w.Speed, (uint)(w.MassMg * 7 + w.Tumble)), (uint)(w.RangeDm + 977 * (seed & 3)));
        var rng = new Random((int)(hash & 0x7fffffff));
        var noise = new ShapedNoise(sampleRate, (int)hash);
        float second = 1.6f + 0.3f * (float)rng.NextDouble();
        var psi = new float[Harmonics.Length + 1];
        for (int k = 0; k < psi.Length; k++) psi[k] = (float)(rng.NextDouble() * 2 * Math.PI);
        float wobbleHz = 3f + 4f * (float)rng.NextDouble(), wobblePhase = (float)(rng.NextDouble() * 2 * Math.PI);
        float jitterHz = 9f + 6f * (float)rng.NextDouble(), jitterPhase = (float)(rng.NextDouble() * 2 * Math.PI);
        float precessHz = 1.5f + 2f * (float)rng.NextDouble(), precessPhase = (float)(rng.NextDouble() * 2 * Math.PI);

        int skip = Math.Max(0, (int)MathF.Floor((start - tFirst) * sampleRate));
        int n = Math.Max(16, (int)MathF.Ceiling((end - start) * sampleRate));
        var pcm = new float[n];
        int idx = ia;
        float phase = 0f, secondPhase = 0f, lastTau = line[ia].T;
        const int Block = 32;
        for (int j = 0; j < skip + n; j++)
        {
            float t = tFirst + j / (float)sampleRate;
            while (idx < line.Count - 2 && arrive[idx + 1] < t) idx++;
            var p0 = line[idx]; var p1 = line[idx + 1 < line.Count ? idx + 1 : idx];
            float span = MathF.Max(1e-9f, arrive[idx + 1 < line.Count ? idx + 1 : idx] - arrive[idx]);
            float u = Math.Clamp((t - arrive[idx]) / span, 0f, 1f);
            float tau = p0.T + u * (p1.T - p0.T);
            float s = p0.S + u * (p1.S - p0.S);
            float v = p0.V + u * (p1.V - p0.V);
            float omega = p0.Omega + u * (p1.Omega - p0.Omega);
            // The tumble's phase, its rate jittering a few per cent as the slug's attitude wanders.
            float jitter = 0.03f * MathF.Sin(2f * MathF.PI * jitterHz * tau + jitterPhase);
            float dTau = MathF.Max(0f, tau - lastTau);
            phase += omega * (1f + jitter) * dTau;
            secondPhase += second * omega * (1f - jitter) * dTau;
            lastTau = tau;
            float dx = w.Along - s;
            float r = MathF.Sqrt(b * b + dx * dx);
            float mach = v / c;
            float dop = 1f / MathF.Max(0.05f, 1f - mach * dx / r);

            if (j % Block == 0)
            {
                float across = slug.Across;
                float peakHz = BulletFlyby.Strouhal * MathF.Max(1f, v) / across / 0.894f;
                float dd = dop;
                Func<float, float> density = f => BulletFlyby.WakeDensity(f / dd, peakHz) / dd;
                if (j == 0) noise.SetSpectrumNow(density); else noise.SetSpectrum(density, Block);
            }
            float carrier = noise.Next();
            if (j < skip) continue;

            var (tonalRms, noiseRms) = Strengths(slug, v, omega, air);
            // The harmonics, normalised so their sum has the rms Strengths gives.
            float wander = 0.6f * MathF.Sin(2f * MathF.PI * wobbleHz * tau + wobblePhase);
            float tone = 0f, norm = 0f;
            for (int k = 0; k < Harmonics.Length; k++)
            {
                float heard = (k + 1) * omega / (2f * MathF.PI) * dop;
                float wk = (k + 1) * Harmonics[k];
                norm += wk * wk;
                if (heard > 16000f) continue;
                float fade = heard < 12000f ? 1f : 0.5f + 0.5f * MathF.Cos(MathF.PI * (heard - 12000f) / 4000f);
                tone += fade * wk * MathF.Sin((k + 1) * phase + psi[k] + (k + 1) * wander);
            }
            {
                float heard = second * omega / (2f * MathF.PI) * dop;
                float wk = 1.75f * SecondLine;
                norm += wk * wk;
                if (heard < 16000f)
                {
                    float fade = heard < 12000f ? 1f : 0.5f + 0.5f * MathF.Cos(MathF.PI * (heard - 12000f) / 4000f);
                    tone += fade * wk * MathF.Sin(secondPhase + psi[^1]);
                }
            }
            tone *= MathF.Sqrt(2f / norm);                  // unit rms
            float precess = 0.75f + 0.25f * MathF.Sin(2f * MathF.PI * precessHz * tau + precessPhase);
            float broadside = 0.55f + 0.45f * MathF.Abs(MathF.Sin(phase));
            float pressure = (tonalRms * precess * tone + noiseRms * broadside * carrier) * dop * dop / r;

            float s1 = Step(t, join1), s2 = Step(t, join2);
            float weight = w.Piece switch { 0 => 1f - s1, 1 => s1 - s2, _ => s2 };
            // The start of the subsonic stretch fades in over a few milliseconds rather than switching on.
            float fadeIn = MathF.Min(1f, (t - tFirst) / 0.004f);
            float sample = pressure * rk / fullScale * weight * fadeIn;
            rawPeak = MathF.Max(rawPeak, MathF.Abs(sample));
            pcm[j - skip] = Math.Clamp(sample, -1f, 1f);
        }
        // Fade the end over 20 ms: the slug lands or flies out of hearing.
        int tail = Math.Min(pcm.Length, (int)(0.02f * sampleRate));
        if (w.Piece == 2 || hi >= d - 1e-3f)
            for (int i = 0; i < tail; i++) pcm[pcm.Length - 1 - i] *= i / (float)tail;
        return pcm;
    }

    private const float Crossfade = 0.0015f;

    private static float Step(float t, float at)
    {
        if (t <= at - Crossfade) return 0f;
        if (t >= at + Crossfade) return 1f;
        return 0.5f - 0.5f * MathF.Cos(MathF.PI * (t - at + Crossfade) / (2f * Crossfade));
    }

    /// <summary>When one piece's first sound arrives, seconds after the ricochet, and where along the
    /// line it is placed: what the server needs to time and place it.</summary>
    public static bool PieceTiming(Whine w, out float startSeconds, out float x)
    {
        startSeconds = 0f;
        if (!TryPiece(w, out float lo, out _, out x)) return false;
        var line = Line(w.Speed, w.Slug, w.Range, w.Air);
        Joins(w, line, out float a, out _, out _, out _);
        float b = w.Miss, c = w.SpeedOfSound;
        int ia = line.FindIndex(p => p.S >= a);
        int il = Math.Max(ia, line.FindIndex(p => p.S >= lo));
        float tFirst = line[ia].T + MathF.Sqrt(b * b + Sq(w.Along - line[ia].S)) / c;
        float tLo = line[il].T + MathF.Sqrt(b * b + Sq(w.Along - line[il].S)) / c;
        startSeconds = MathF.Max(tFirst, tLo - (w.Piece == 0 ? 0f : Crossfade));
        return true;
    }

    /// <summary>
    /// What one listener at <paramref name="ear"/> hears of a ricochet's slug flying off from
    /// <paramref name="from"/> at <paramref name="velocity"/>, <paramref name="seconds"/> after the
    /// shot: the whine in three pieces placed along its line, each declared at the whine's full scale
    /// and timed so that, with the client's own flight-time delay from where it is placed, it arrives
    /// when it should. Nothing for a listener more than <see cref="WhineMetres"/> off its line.
    /// </summary>
    public static List<TransientSound> WhineSounds(Vector3 from, Vector3 velocity, Slug slug, float range, float seconds,
                                                   Vector3 ear, Air air)
    {
        var sounds = new List<TransientSound>();
        float speed = velocity.Length();
        if (speed < 30f || range < 0.5f) return sounds;
        Vector3 u = velocity / speed;
        Vector3 rel = ear - from;
        float along = Vector3.Dot(rel, u);
        float miss = (rel - along * u).Length();
        if (miss > WhineMetres || along < -WhineMetres || along > range + WhineMetres) return sounds;
        for (int piece = 0; piece < 3; piece++)
        {
            var w = new Whine((int)MathF.Round(speed), (int)MathF.Round(MathF.Min(range, 999f) * 10f), (int)MathF.Round(miss * 10f),
                              (int)MathF.Round(Math.Clamp(along, -999f, 999f) * 10f), (int)MathF.Round(air.SpeedOfSound),
                              (int)MathF.Round(air.Density * 1000f), (int)MathF.Round(slug.MassKg * 1e6f),
                              (int)MathF.Round(slug.Length * 1e4f), (int)MathF.Round(slug.Width * 1e4f), (int)MathF.Round(slug.Thickness * 1e4f),
                              (int)MathF.Round(slug.TumbleRadPerSec), piece);
            if (!PieceTiming(w, out float start, out float x)) continue;
            Vector3 at = from + u * x;
            float rk = Vector3.Distance(ear, at);
            sounds.Add(new TransientSound
            {
                Character = SoundCharacter.Hiss,
                Position = at,
                DelaySeconds = MathF.Max(0f, seconds + start - rk / air.SpeedOfSound),
                LevelDb = BulletFlyby.Spl(WhineFullScalePascals(w)),
                SynthKey = WhineKey(w),
                DecaySeconds = 0.3f,
                Noisiness = 0.5f,
            });
        }
        return sounds;
    }
}
