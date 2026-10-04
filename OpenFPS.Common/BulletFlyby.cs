using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>One moment of a bullet's flight: how long it has flown, where it is and how fast it goes.</summary>
public readonly record struct FlightSample(float Seconds, Vector3 Position, Vector3 Velocity);

/// <summary>
/// What a bullet sounds like as it goes past somebody: the CRACK of a supersonic round's shock wave,
/// and the WHIZZ of a subsonic one's wake. Neither comes from the shooter.
///
/// THE CRACK. A round faster than sound drags a Mach cone, and a listener hears it when the cone
/// sweeps over them. Which point of the flight that sound left from, and when it arrives, is one
/// statement: the first sound of the flight to reach the ear is the one that minimises
/// <c>t + |ear − P(t)| / c</c> over the flight (Fermat). Where that minimum is inside the flight its
/// derivative is zero, <c>1 − (v/c)·cos θ = 0</c>, which is the cone: the ray from the emission point
/// to the ear leaves at the Mach angle to the flight. A slowing, falling bullet is handled by the same
/// sum, because it is evaluated on the flown path. A round that never outruns sound has no interior
/// minimum (the derivative is positive everywhere), and neither does a listener the cone has not yet
/// swept when the bullet stops in a wall. Placed at the emission point and delayed by the bullet's
/// time to get there, the client's own flight-time delay (distance over the speed of sound) then
/// brings it in at the right moment and from the right direction, before the muzzle's report for
/// anyone downrange.
///
/// Its strength and length are Whitham's far-field N-wave (G. B. Whitham, "The flow pattern of a
/// supersonic projectile", Comm. Pure Appl. Math. 5, 1952), in the form used for small arms by
/// Stoughton (JASA 102(2), 1997) and Maher (IEEE SAFE 2006):
/// <code>
///   Δp_max = 0.53 · p0 · (M² − 1)^(1/8) · d / (b^(3/4) · l^(1/4))
///   T      = 1.82 · M · b^(1/4) · d / (c · (M² − 1)^(3/8) · l^(1/4))
/// </code>
/// with p0 the ambient pressure, M the Mach number where the sound left, d and l the bullet's diameter
/// and length, b the miss distance and c the speed of sound. A .308 at Mach 2.2 passing 5 m away:
/// 350 Pa (145 dB) and 0.2 ms.
///
/// THE WHIZZ. A subsonic round makes no shock; what is heard is the turbulence of its wake as it goes
/// by, a short rush of broadband noise. A compact body's fluctuating force radiates as a dipole
/// (N. Curle, "The influence of solid boundaries upon aerodynamic sound", Proc. R. Soc. A 231, 1955):
/// with a fluctuating lift coefficient C' over the side area d·l, at a Strouhal number St, the rms at
/// r abeam is <c>ρ U³ l C' St / (4√2 c r)</c>. C' = 0.02 is chosen for a streamlined bullet, an order
/// of magnitude below a bluff cylinder's, and gives 91 dB at a metre for a .45 at 253 m/s: an
/// estimate, there being no measurement of a subsonic fly-by to fit.
///
/// Its SPECTRUM is the wake's, broad: rising as f² below the peak (a dipole radiates the rate of
/// change of a force whose spectrum is flat at low frequency) and falling as f^-2.5 above it (the
/// turbulent force's roll-off), the peak at a Strouhal number of 0.2 on the bullet's base diameter
/// (St·U/d, 4.4 kHz for the .45: the helical shedding of an axisymmetric base wake, Fuchs, Mercker and
/// Michel, J. Fluid Mech. 93, 1979, a broad hump in a turbulent wake, not a line). Its half-power
/// width is 2.4 octaves, and within 10 dB of its peak it spans 4.9. The bullet's gyroscopic nutation
/// and precession (from the rifling's spin) swing its yaw, and with it the side force: a 15 % and a
/// 10 % flutter on the noise.
///
/// It is heard through the moving source's own geometry: each instant of the pass is heard when its
/// sound arrives, so the approach is compressed in time and raised in pitch and the retreat stretched
/// and lowered (the Doppler fall of a whizz), louder ahead by the convective factor (1 − M cos θ)^-2.
/// Noise of a moving source is noise of the Doppler-shifted spectrum, so it is rendered as noise whose
/// spectrum is the source's at f/D for the Doppler factor D of the moment that sound left
/// (<see cref="ShapedNoise"/>). It was a sum of 96 sine partials wandering about the shedding
/// frequency, and that was heard as one tone sweeping from 9.9 to 2.8 kHz, "a quick laser" (Cody,
/// 2026-10-04). It is played as three pieces from three places along the path (approach, abeam, going
/// away), crossfaded, so it moves past the listener rather than coming from one point.
///
/// Both are rendered on the client from their keys; nothing here is recorded.
/// </summary>
public static class BulletFlyby
{
    public const string CrackPrefix = "bullet:crack:";
    public const string WhizzPrefix = "bullet:whizz:";

    /// <summary>A listener further than this from the flight is sent no crack. Not 50 m: the NIJ take
    /// 23 degrees off the line at 150 m passes 59 m from the bullet, and its crack is 17 dB over
    /// anything else in the take, the report included (inbox/gunfire-references-2026-09-24,
    /// m16_downrange_23deg_150m).</summary>
    public const float HearingMetres = 100f;

    /// <summary>A subsonic round's whizz is a quiet thing: past this it is under any street's noise.</summary>
    public const float WhizzMetres = 15f;

    /// <summary>Whitham's law is a far-field law, of no use inside a few bullet lengths of the path.</summary>
    public const float MinMissMetres = 0.3f;

    /// <summary>The shock's rise, seconds: the N-wave's corners rounded as a Gaussian of this width.
    /// A weak shock in air rises in tens of microseconds (10-90 % in 2.56 σ = 26 µs here), not
    /// instantly, and that is also what keeps a 48 kHz render from aliasing.</summary>
    public const float ShockRiseSigma = 10e-6f;

    /// <summary>Strouhal number of the wake's shedding.</summary>
    public const float Strouhal = 0.2f;
    /// <summary>The fluctuating lift coefficient of a streamlined bullet: see the class remarks.</summary>
    public const float FluctuatingLift = 0.02f;

    private const float RefPascals = 20e-6f;

    /// <summary>dB SPL of a pressure.</summary>
    public static float Spl(float pascals) => 20f * MathF.Log10(MathF.Max(1e-9f, pascals) / RefPascals);

    private static (float D, float L) Size(WeaponDefinition w)
        => (w.BulletDiameterMetres > 0f ? w.BulletDiameterMetres : 0.009f,
            w.BulletLengthMetres > 0f ? w.BulletLengthMetres : 0.0156f);

    // ── Whitham ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The N-wave's peak overpressure, Pa: Whitham, as in the class remarks.</summary>
    public static float CrackPeakPascals(float mach, float missMetres, float diameter, float length, float ambientPascals)
    {
        float m2 = MathF.Max(1e-4f, mach * mach - 1f);
        float b = MathF.Max(MinMissMetres, missMetres);
        return 0.53f * ambientPascals * MathF.Pow(m2, 0.125f) * diameter / (MathF.Pow(b, 0.75f) * MathF.Pow(length, 0.25f));
    }

    /// <summary>The N-wave's length, seconds: Whitham, as in the class remarks.</summary>
    public static float CrackSeconds(float mach, float missMetres, float diameter, float length, float speedOfSound)
    {
        // Near Mach 1 the law's length runs off to infinity; a round that slow has barely a shock.
        float m2 = MathF.Max(0.05f, mach * mach - 1f);
        float b = MathF.Max(MinMissMetres, missMetres);
        return 1.82f * mach * MathF.Pow(b, 0.25f) * diameter / (speedOfSound * MathF.Pow(m2, 0.375f) * MathF.Pow(length, 0.25f));
    }

    // ── Where a listener meets the flight ───────────────────────────────────────────────────────

    /// <summary>The flight at a time, on the straight pieces between its samples.</summary>
    public static FlightSample At(IReadOnlyList<FlightSample> path, float seconds)
    {
        if (seconds <= path[0].Seconds) return path[0];
        for (int i = 1; i < path.Count; i++)
        {
            if (seconds > path[i].Seconds) continue;
            var a = path[i - 1]; var b = path[i];
            float u = (seconds - a.Seconds) / MathF.Max(1e-9f, b.Seconds - a.Seconds);
            return new FlightSample(seconds, Vector3.Lerp(a.Position, b.Position, u), Vector3.Lerp(a.Velocity, b.Velocity, u));
        }
        return path[^1];
    }

    /// <summary>
    /// Where and when the shock that reaches <paramref name="ear"/> left the flight, if one does: the
    /// interior minimum of t + |ear − P(t)|/c. False for a round that never outran sound near enough
    /// to matter, and for one that stopped before its cone reached the ear.
    /// </summary>
    public static bool FindCrack(IReadOnlyList<FlightSample> path, Vector3 ear, float speedOfSound, out FlightSample emitted)
    {
        emitted = default;
        if (path.Count < 2 || speedOfSound <= 1f) return false;
        float F(float t) { var s = At(path, t); return t + Vector3.Distance(ear, s.Position) / speedOfSound; }

        int best = 0; float min = float.MaxValue;
        for (int i = 0; i < path.Count; i++)
        {
            float f = path[i].Seconds + Vector3.Distance(ear, path[i].Position) / speedOfSound;
            if (f < min) { min = f; best = i; }
        }
        // Golden section over the two pieces either side of the best sample: the sum is convex there.
        float lo = path[Math.Max(0, best - 1)].Seconds, hi = path[Math.Min(path.Count - 1, best + 1)].Seconds;
        const float g = 0.381966f;
        float x1 = lo + g * (hi - lo), x2 = hi - g * (hi - lo);
        float f1 = F(x1), f2 = F(x2);
        for (int k = 0; k < 60 && hi - lo > 1e-7f; k++)
        {
            if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = lo + g * (hi - lo); f1 = F(x1); }
            else { lo = x1; x1 = x2; f1 = f2; x2 = hi - g * (hi - lo); f2 = F(x2); }
        }
        float t = 0.5f * (lo + hi);
        // Interior, and faster than sound there: otherwise the first sound to arrive is from the muzzle
        // end of the flight (no shock reached the ear) or from where the bullet stopped.
        const float edge = 2e-4f;
        if (t <= path[0].Seconds + edge || t >= path[^1].Seconds - edge) return false;
        emitted = At(path, t);
        return emitted.Velocity.Length() > speedOfSound * 1.001f;
    }

    /// <summary>
    /// Where the flight passes nearest <paramref name="ear"/>: the moment, the point, how far off,
    /// and how much of the flight lies before and after it. False when the nearest point is an end of
    /// the flight: the bullet never went by.
    /// </summary>
    public static bool FindPass(IReadOnlyList<FlightSample> path, Vector3 ear, out FlightSample nearest, out float miss,
                                out float before, out float after)
    {
        nearest = default; miss = float.MaxValue; before = after = 0f;
        if (path.Count < 2) return false;
        int seg = -1; float segU = 0f;
        float total = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            Vector3 a = path[i - 1].Position, d = path[i].Position - a;
            float len2 = d.LengthSquared();
            float u = len2 < 1e-12f ? 0f : Math.Clamp(Vector3.Dot(ear - a, d) / len2, 0f, 1f);
            float dist = Vector3.Distance(ear, a + d * u);
            if (dist < miss) { miss = dist; seg = i; segU = u; }
            total += MathF.Sqrt(len2);
        }
        if (seg < 0) return false;
        if ((seg == 1 && segU <= 1e-4f) || (seg == path.Count - 1 && segU >= 1f - 1e-4f)) return false;
        for (int i = 1; i < seg; i++) before += Vector3.Distance(path[i - 1].Position, path[i].Position);
        before += Vector3.Distance(path[seg - 1].Position, path[seg].Position) * segU;
        after = MathF.Max(0f, total - before);
        var p = path[seg - 1]; var q = path[seg];
        nearest = new FlightSample(p.Seconds + (q.Seconds - p.Seconds) * segU,
                                   Vector3.Lerp(p.Position, q.Position, segU), Vector3.Lerp(p.Velocity, q.Velocity, segU));
        return true;
    }

    /// <summary>
    /// What one listener hears of a bullet's flight, as world sounds timed from the shot: the crack
    /// where its shock left the path, or the whizz of a subsonic one going by, or nothing. Each sound's
    /// <see cref="TransientSound.DelaySeconds"/> is from the trigger; the client adds the flight of the
    /// sound itself from where it is placed.
    /// </summary>
    public static List<TransientSound> Sounds(IReadOnlyList<FlightSample> path, Vector3 ear, WeaponDefinition weapon, Air air)
    {
        var (d, l) = Size(weapon);
        var sounds = CrackSounds(path, ear, d, l, air);
        if (path.Count < 2) return sounds;
        float c = air.SpeedOfSound;

        if (FindPass(path, ear, out var n, out float miss, out float before, out float after)
            && miss <= WhizzMetres && n.Velocity.Length() < c && before >= 1f)
        {
            Vector3 u = Vector3.Normalize(n.Velocity);
            for (int piece = 0; piece < 3; piece++)
            {
                var w = new Whizz((int)MathF.Round(n.Velocity.Length()), (int)MathF.Round(miss * 10f),
                                  (int)MathF.Round(d * 1e4f), (int)MathF.Round(l * 1e4f), (int)MathF.Round(c),
                                  (int)MathF.Floor(before), (int)MathF.Floor(MathF.Min(after, 999f)),
                                  (int)MathF.Round(air.Density * 1000f), piece,
                                  weapon.RiflingTwistMetres > 0f ? (int)MathF.Round(weapon.RiflingTwistMetres / d) : 0);
                if (!TryPiece(w, out float start, out _, out float x)) continue;
                float rk = MathF.Sqrt(w.Miss * w.Miss + x * x);
                sounds.Add(new TransientSound
                {
                    Character = SoundCharacter.Hiss,
                    Position = n.Position + u * x,
                    // Its piece of the pass is heard from `start` after the bullet is abeam; the client
                    // adds rk/c of its own.
                    DelaySeconds = MathF.Max(0f, n.Seconds + start - rk / c),
                    LevelDb = Spl(WhizzFullScalePascals(w)),
                    SynthKey = WhizzKey(w),
                    DecaySeconds = 0.05f,
                    Noisiness = 1f,
                });
            }
        }
        return sounds;
    }

    /// <summary>The crack alone, for a body of diameter <paramref name="d"/> and length
    /// <paramref name="l"/> flying <paramref name="path"/>: a bullet, or a ricochet's slug while it is
    /// still faster than sound.</summary>
    public static List<TransientSound> CrackSounds(IReadOnlyList<FlightSample> path, Vector3 ear, float d, float l, Air air)
    {
        var sounds = new List<TransientSound>();
        if (path.Count < 2) return sounds;
        float c = air.SpeedOfSound;
        if (FindCrack(path, ear, c, out var e))
        {
            Vector3 u = Vector3.Normalize(e.Velocity);
            Vector3 rel = ear - e.Position;
            float r = rel.Length();
            float b = Vector3.Cross(rel, u).Length();
            if (b <= HearingMetres)
            {
                float mach = e.Velocity.Length() / c;
                float peak = CrackPeakPascals(mach, b, d, l, air.PressureMb * 100f);
                float seconds = CrackSeconds(mach, b, d, l, c);
                sounds.Add(new TransientSound
                {
                    Character = SoundCharacter.Knock,
                    Position = e.Position,
                    DelaySeconds = e.Seconds,
                    // The buffer's full scale is the shock's peak (see RenderCrack), carried back to a
                    // metre by the inverse law the client will apply over the distance it is placed at.
                    LevelDb = Spl(peak * MathF.Max(1f, r)),
                    SynthKey = CrackKey(seconds),
                    DecaySeconds = 0.05f,
                    Noisiness = 1f,
                });
            }
        }
        return sounds;
    }

    // ── The crack's key and render ──────────────────────────────────────────────────────────────

    /// <summary>"bullet:crack:196": the N-wave's length in microseconds, all its render needs.</summary>
    public static string CrackKey(float seconds)
        => CrackPrefix + Math.Clamp((int)MathF.Round(seconds * 1e6f), 20, 5000).ToString(CultureInfo.InvariantCulture);

    public static bool TryParseCrack(string? key, out float seconds)
    {
        seconds = 0f;
        if (key == null || !key.StartsWith(CrackPrefix, StringComparison.Ordinal)) return false;
        if (!int.TryParse(key.AsSpan(CrackPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int us)
            || us < 20 || us > 5000) return false;
        seconds = us * 1e-6f;
        return true;
    }

    /// <summary>
    /// The N-wave: a jump to +1, a straight fall to −1 over <paramref name="seconds"/>, and a jump back
    /// to nothing, its corners rounded by the shock's rise (<see cref="ShockRiseSigma"/>). Evaluated in
    /// closed form (the ramp convolved with a Gaussian) at each sample, so nothing is lost to a 48 kHz
    /// grid that a 0.2 ms pulse is only ten samples of. Full scale is the shock's peak overpressure;
    /// the rounding takes a little off it, as the air does.
    /// </summary>
    public static float[] RenderCrack(float seconds, int sampleRate)
    {
        float s = ShockRiseSigma;
        float lead = 5f * s;
        int n = (int)MathF.Ceiling((seconds + 10f * s) * sampleRate) + 8;
        var pcm = new float[Math.Max(n, 64)];
        for (int i = 0; i < pcm.Length; i++)
        {
            float t = i / (float)sampleRate - lead;
            pcm[i] = SmoothedN(t, seconds, s);
        }
        return pcm;
    }

    /// <summary>(1 − 2t/T) on [0, T] convolved with a Gaussian of width σ.</summary>
    internal static float SmoothedN(float t, float T, float sigma)
    {
        double z0 = t / sigma, z1 = (t - T) / sigma;
        double i0 = Phi(z0) - Phi(z1);
        double i1 = t * i0 + sigma * (Pdf(z0) - Pdf(z1));
        return (float)(i0 - 2.0 / T * i1);
    }

    private static double Pdf(double z) => Math.Exp(-0.5 * z * z) / Math.Sqrt(2 * Math.PI);
    private static double Phi(double z) => 0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));

    /// <summary>The error function (Abramowitz and Stegun 7.1.26, to 1.5e-7).</summary>
    internal static double Erf(double x)
    {
        double sign = Math.Sign(x);
        x = Math.Abs(x);
        double t = 1.0 / (1.0 + 0.3275911 * x);
        double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }

    // ── The whizz's key and render ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A subsonic pass, quantised for the key: speed m/s, miss distance in decimetres, the bullet's
    /// diameter and length in tenths of a millimetre, the speed of sound m/s, the metres of flight before
    /// and after the nearest point, the air's density in g/m³, which of the three pieces, and the
    /// rifling's twist in calibres per turn (0: a smooth bore, or a key from before it was sent).
    /// </summary>
    public readonly record struct Whizz(int Speed, int MissDm, int DiameterTenthMm, int LengthTenthMm, int SoundSpeed,
                                        int BeforeMetres, int AfterMetres, int DensityGrams, int Piece, int TwistCalibres = 0)
    {
        public float Miss => MathF.Max(MinMissMetres, MissDm / 10f);
        public float SpeedOfSound => MathF.Max(200f, SoundSpeed);
        /// <summary>The speed the geometry is worked at: held under Mach 0.95, where the subsonic
        /// picture still holds.</summary>
        public float V => MathF.Min(MathF.Max(1f, Speed), 0.95f * SpeedOfSound);
        public float Diameter => MathF.Max(1e-3f, DiameterTenthMm * 1e-4f);
        public float Length => MathF.Max(1e-3f, LengthTenthMm * 1e-4f);
        public float Density => DensityGrams > 0 ? DensityGrams / 1000f : 1.225f;
    }

    public static string WhizzKey(Whizz w) => string.Create(CultureInfo.InvariantCulture,
        $"{WhizzPrefix}{w.Speed}:{w.MissDm}:{w.DiameterTenthMm}:{w.LengthTenthMm}:{w.SoundSpeed}:{w.BeforeMetres}:{w.AfterMetres}:{w.DensityGrams}:{w.Piece}:{w.TwistCalibres}");

    public static bool TryParseWhizz(string? key, out Whizz w)
    {
        w = default;
        if (key == null || !key.StartsWith(WhizzPrefix, StringComparison.Ordinal)) return false;
        var parts = key[WhizzPrefix.Length..].Split(':');
        if (parts.Length is not (9 or 10)) return false;
        var v = new int[10];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]) || v[i] < 0) return false;
        if (v[8] > 2 || v[0] < 1 || v[4] < 200 || v[9] > 1000) return false;
        w = new Whizz(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9]);
        return true;
    }

    /// <summary>The share of the wake's noise (<see cref="WakeDensity"/>) below <paramref name="hz"/>.</summary>
    public static float WakeShareBelow(float hz, float peakHz)
    {
        if (peakHz <= 0f) return 1f;
        float x = hz / peakHz;
        if (x <= 0f) return 0f;
        // Simpson's rule in log x from 1e-3 up: the density is negligible below.
        const int steps = 48;
        double a = Math.Log(1e-3), b = Math.Log(Math.Max(1.001e-3, x)), h = (b - a) / steps, sum = 0;
        for (int i = 0; i <= steps; i++)
        {
            double u = Math.Exp(a + i * h), f = u * u / Math.Pow(1 + u * u, 2.25) * u;
            sum += f * (i == 0 || i == steps ? 1 : i % 2 == 1 ? 4 : 2);
        }
        return (float)Math.Min(1.0, sum * h / 3 / 0.47924);
    }

    /// <summary>
    /// The turbulent wake's radiated spectrum, a density per hertz whose integral is one: x²/(1+x²)^2.25
    /// in x = f/fp, rising as f² and falling as f^-2.5, its peak at 0.89 fp. ∫ x²/(1+x²)^2.25 dx over
    /// all x is B(3/2, 3/4)/2 = 0.47924.
    /// </summary>
    public static float WakeDensity(float hz, float peakHz)
    {
        if (hz <= 0f || peakHz <= 0f) return 0f;
        float x = hz / peakHz, x2 = x * x;
        return x2 / MathF.Pow(1f + x2, 2.25f) / (0.47924f * peakHz);
    }

    /// <summary>The crossfade between the pieces, seconds either side of each join.</summary>
    private const float Crossfade = 0.0015f;

    /// <summary>The stretch of the path the whizz is heard from, metres either side of the nearest
    /// point: six miss distances, where the dipole is 31 dB down on abeam.</summary>
    private static (float From, float To) Stretch(Whizz w)
    {
        float span = Math.Clamp(6f * w.Miss, 5f, 40f);
        return (-MathF.Min(span, w.BeforeMetres), MathF.Min(span, w.AfterMetres));
    }

    /// <summary>When the sound emitted at <paramref name="x"/> metres along the path from the nearest
    /// point arrives, seconds after the sound emitted there would.</summary>
    private static float Heard(Whizz w, float x) => x / w.V + MathF.Sqrt(w.Miss * w.Miss + x * x) / w.SpeedOfSound;

    /// <summary>The boundaries in x of the three pieces: approaching, abeam (within a miss distance
    /// either side), going away.</summary>
    private static (float A, float B, float C, float D) Joins(Whizz w)
    {
        var (from, to) = Stretch(w);
        float b = w.Miss;
        return (from, Math.Clamp(-b, from, to), Math.Clamp(b, from, to), to);
    }

    /// <summary>
    /// One piece of the pass: when it starts and ends, seconds of arrival after the nearest point's,
    /// crossfades included, and where along the path it is placed. False for a piece with nothing in
    /// it (a pass cut short by the muzzle or a wall).
    /// </summary>
    public static bool TryPiece(Whizz w, out float start, out float end, out float x)
    {
        var (a, b, c, d) = Joins(w);
        float lo = w.Piece switch { 0 => a, 1 => b, _ => c };
        float hi = w.Piece switch { 0 => b, 1 => c, _ => d };
        start = end = x = 0f;
        if (hi - lo < 0.05f) return false;
        float first = Heard(w, a), last = Heard(w, d);
        start = MathF.Max(first, Heard(w, lo) - (w.Piece == 0 ? 0f : Crossfade));
        end = MathF.Min(last, Heard(w, hi) + (w.Piece == 2 ? 0f : Crossfade));
        float m = w.Miss;
        x = w.Piece switch { 0 => MathF.Max(lo, -2f * m), 1 => Math.Clamp(0f, lo, hi), _ => MathF.Min(hi, 2f * m) };
        return end > start;
    }

    /// <summary>Its rms pressure at the listener when the sound left <paramref name="x"/> metres along
    /// the path: Curle's dipole abeam, carried by sin θ / r and the convective factor.</summary>
    private static float Envelope(Whizz w, float x)
    {
        float b = w.Miss, r = MathF.Sqrt(b * b + x * x);
        float mach = w.V / w.SpeedOfSound;
        float doppler = 1f / (1f + mach * x / r);         // 1 / (1 − M cos θ), cos θ = −x/r
        float u = MathF.Max(1f, w.Speed);
        float atOneMetre = w.Density * u * u * u * w.Length * FluctuatingLift * Strouhal
                         / (4f * MathF.Sqrt(2f) * w.SpeedOfSound);
        return atOneMetre * (b / r) / r * doppler * doppler;
    }

    /// <summary>
    /// The pressure, Pa at a metre, that a whizz buffer's full scale stands for: four times the
    /// loudest rms any piece carries of what can be heard of it (the share of its raised spectrum under
    /// 16 kHz), scaled up by its own distance, which the client's inverse law takes off again, so the
    /// noise's peaks fit. The same for all three pieces, so they are placed
    /// alike and join without a step; worked from the key alone, so the server declares exactly what
    /// the client renders.
    /// </summary>
    public static float WhizzFullScalePascals(Whizz w)
    {
        float max = 1e-6f;
        var (a, b, c, d) = Joins(w);
        float m = w.Miss;
        for (int piece = 0; piece < 3; piece++)
        {
            float lo = piece switch { 0 => a, 1 => b, _ => c };
            float hi = piece switch { 0 => b, 1 => c, _ => d };
            if (hi - lo < 0.05f) continue;
            float x = piece switch { 0 => MathF.Max(lo, -2f * m), 1 => Math.Clamp(0f, lo, hi), _ => MathF.Min(hi, 2f * m) };
            float rk = MathF.Sqrt(m * m + x * x);
            for (int i = 0; i <= 200; i++)
            {
                // Only what can be heard of it: the share of the raised spectrum left under 16 kHz.
                float xi = lo + (hi - lo) * i / 200f, ri = MathF.Sqrt(m * m + xi * xi);
                float doppler = 1f / (1f + w.V / w.SpeedOfSound * xi / ri);
                float scale = Strouhal * MathF.Max(1f, w.Speed) / w.Diameter / 0.894f;
                max = MathF.Max(max, Envelope(w, xi) * rk * MathF.Sqrt(WakeShareBelow(16000f / doppler, scale)));
            }
        }
        return 4f * 1.3f * max;
    }

    /// <summary>
    /// One piece of a whizz, as the listener hears it: the wake's noise, emitted along the path and
    /// heard at its own arrival time, so its spectrum slides down and its level swells and dies as it
    /// would.
    ///
    /// Each output sample is worked back to the EMISSION time whose sound arrives then (solved in closed
    /// form), which gives the Doppler factor D of that moment, the distance and the angle. The noise is
    /// broadband (<see cref="ShapedNoise"/>), its spectrum the wake's (<see cref="WakeDensity"/>) at f/D,
    /// so the whole hump rides up on the approach and down on the retreat; anything the raised spectrum
    /// puts above the bands simply is not there, so nothing aliases. A flutter rides on it at the
    /// bullet's two epicyclic yaw rates (McCoy, Modern Exterior Ballistics, 1999, ch. 10): the fast
    /// nutation and the slow precession, (Ix/2Iy)·spin·(1 ± √(1 − 1/Sg)) at a gyroscopic stability Sg
    /// of 2, spin being speed over the twist. The whole pass is run from its first sound for every
    /// piece, so all three pieces of one pass carry one noise and join without a seam.
    /// </summary>
    public static float[] RenderWhizz(Whizz w, int sampleRate, int seed)
    {
        if (!TryPiece(w, out float start, out float end, out float xk)) return new float[16];
        float b = w.Miss, v = w.V, c = w.SpeedOfSound, mach = v / c;
        float rk = MathF.Sqrt(b * b + xk * xk);
        float fullScale = WhizzFullScalePascals(w);
        var (ja, jb, jc, jd) = Joins(w);
        float first = Heard(w, ja);
        float join1 = Heard(w, jb), join2 = Heard(w, jc);

        uint hash = Mix(Mix(Mix(Mix((uint)w.Speed, (uint)w.MissDm), (uint)(w.DiameterTenthMm * 7919 + w.LengthTenthMm)),
                            (uint)(w.BeforeMetres * 1009 + w.AfterMetres)), (uint)(seed & 3));
        var noise = new ShapedNoise(sampleRate, (int)hash);
        var rng = new Random((int)(hash & 0x7fffffff));
        float speed = MathF.Max(1f, w.Speed);
        // The hump's peak at St·U/d; the density's own peak is at 0.894 of its scale.
        float scale = Strouhal * speed / w.Diameter / 0.894f;

        float twist = w.TwistCalibres > 0 ? w.TwistCalibres : 30f;
        float spinHz = speed / (twist * w.Diameter);
        float r2 = 0.25f * w.Diameter * w.Diameter;
        float inertia = 6f * r2 / (3f * r2 + w.Length * w.Length);      // Ix/Iy of a solid cylinder
        float root = MathF.Sqrt(1f - 1f / 2f);
        float nutationHz = spinHz * 0.5f * inertia * (1f + root);
        float precessionHz = spinHz * 0.5f * inertia * (1f - root);
        float p1 = (float)(rng.NextDouble() * 2 * Math.PI), p2 = (float)(rng.NextDouble() * 2 * Math.PI);

        int skip = Math.Max(0, (int)MathF.Floor((start - first) * sampleRate));
        int n = Math.Max(16, (int)MathF.Ceiling((end - start) * sampleRate));
        var pcm = new float[n];
        float c2 = c * c, v2 = v * v;
        const int Block = 32;
        for (int j = 0; j < skip + n; j++)
        {
            float t = first + j / (float)sampleRate;
            // The emission time whose sound arrives at t: c²(t − τ)² = b² + v²τ², the earlier root.
            float tau = (c2 * t - MathF.Sqrt(c2 * v2 * t * t + b * b * (c2 - v2))) / (c2 - v2);
            float x = v * tau;
            float r = MathF.Sqrt(b * b + x * x);
            if (j % Block == 0)
            {
                // The Doppler factor half a block on, so the glide lands on it mid-block.
                float tm = t + 0.5f * Block / sampleRate;
                float taum = (c2 * tm - MathF.Sqrt(c2 * v2 * tm * tm + b * b * (c2 - v2))) / (c2 - v2);
                float xm = v * taum, rm = MathF.Sqrt(b * b + xm * xm);
                float dm = 1f / (1f + mach * xm / rm);
                Func<float, float> density = f => WakeDensity(f / dm, scale) / dm;
                if (j == 0) noise.SetSpectrumNow(density); else noise.SetSpectrum(density, Block);
            }
            float carrier = noise.Next();
            if (j < skip) continue;

            float flutter = 1f + 0.15f * MathF.Sin(2f * MathF.PI * nutationHz * tau + p1)
                               + 0.10f * MathF.Sin(2f * MathF.PI * precessionHz * tau + p2);
            // The crossfade weights of the three pieces sum to one at every moment.
            float s1 = Step(t, join1), s2 = Step(t, join2);
            float weight = w.Piece switch { 0 => 1f - s1, 1 => s1 - s2, _ => s2 };
            float pressure = Envelope(w, x) * carrier * flutter;
            pcm[j - skip] = Math.Clamp(pressure * rk / fullScale * weight, -1f, 1f);
        }
        return pcm;
    }

    /// <summary>A deterministic mix of two words (the same on every machine, unlike HashCode).</summary>
    public static uint Mix(uint a, uint b)
    {
        uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u + (a << 6) + (a >> 2));
        h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
        return h;
    }


    /// <summary>A raised-cosine step from 0 to 1 across <see cref="Crossfade"/> either side of a join.</summary>
    private static float Step(float t, float at)
    {
        if (t <= at - Crossfade) return 0f;
        if (t >= at + Crossfade) return 1f;
        return 0.5f - 0.5f * MathF.Cos(MathF.PI * (t - at + Crossfade) / (2f * Crossfade));
    }
}
