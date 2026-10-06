using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// Thunder, worked out for one listener from the lightning channel that made it.
///
/// The model is the tortuous-channel one (Few, J. Geophys. Res. 74, 1969; Ribner and Roy, J. Acoust.
/// Soc. Am. 72, 1982; Lacroix, Coulouvrat, Marchiano, Farges and Ripoll, Geophys. Res. Lett. 46,
/// 2019). Every bit of the channel is heated at once and sends out the same weak shock, an N-wave;
/// each arrives at the listener after its own distance over the speed of sound. So the thunder is
/// the channel's shape read out in time: the nearest bit is the crack, and the rest, kilometres of
/// it at every distance, is the rumble. A straight piece of channel adds up loudly only where its
/// pieces arrive together, broadside on (Ribner and Roy's "pip"); seen end on its pieces arrive one
/// after another and almost cancel, so the corners carry much of the sound.
///
/// What happens on the way is per distance: the N-wave lengthens a little as its shock outruns its
/// tail (weak-shock theory, Few 1969), the air takes the top off it (ISO 9613-1, Bass 1980), the
/// ground under the listener adds a second arrival, and a source high up and far away is refracted
/// over the listener's head by the temperature falling with height (Fleagle, J. Meteorol. 6, 1949),
/// which is why thunder is rarely heard past about 25 km.
///
/// The level is anchored to the channel simulation Lacroix et al. (2019) matched to thunder measured
/// from 100 m to 25 km: 650 Pa, 2 m from the channel, for a wave whose spectrum peaks at 148 Hz.
/// </summary>
public static class Thunder
{
    /// <summary>The air the thunder travels through.</summary>
    public readonly record struct Air(float TemperatureC, float Humidity, float PressureMb, Vector3 Wind)
    {
        public static Air Standard => new(15f, 0.8f, 1013.25f, Vector3.Zero);
    }

    /// <summary>One part of the thunder, from one direction: its pressure at the ear, pascals, from
    /// <see cref="StartSeconds"/> after the flash.</summary>
    public sealed class Part
    {
        public float[] Pressure = Array.Empty<float>();
        public int SampleRate;
        /// <summary>Unit vector from the listener toward where this part comes from.</summary>
        public Vector3 Direction;
        public float StartSeconds;
        public float PeakPa;
        public float Seconds => Pressure.Length / (float)Math.Max(1, SampleRate);
        public float PeakDb => 20f * MathF.Log10(MathF.Max(1e-9f, PeakPa) / ReferencePa);
    }

    public sealed class Options
    {
        /// <summary>At most this many directions the thunder is split into.</summary>
        public int MaxParts = 3;
        /// <summary>Directions closer than this are one part, degrees.</summary>
        public float MergeDegrees = 30f;
        /// <summary>The ground's reflection under the listener; off for a free-field measurement.</summary>
        public bool Ground = true;
        /// <summary>How high the ear is above the ground it stands on, metres.</summary>
        public float EarAboveGround = 1.7f;
        /// <summary>A part quieter than this at its peak, dB SPL, is not worth a voice.</summary>
        public float MinAudibleDb = 30f;
        /// <summary>Force a sample rate (the lab, for comparing renders); zero lets the distance choose.</summary>
        public int SampleRate;
        /// <summary>Cores to render on; zero is half of them, at most four.</summary>
        public int Threads;
        /// <summary>The channel exactly as given: no structure below its 8 m steps and the same energy
        /// in every metre. For checking the level against a straight line.</summary>
        public bool Plain;
    }

    // ── Constants, each from where it was measured ───────────────────────────────────────────────

    public const float ReferencePa = 20e-6f;

    /// <summary>The source: Lacroix et al. (2019), the channel's wave once it has become acoustic, 2 m
    /// out: 650 Pa (thesis, sec. 5.1.5, matched to the HyMeX thunder energies), spectrum peaking at
    /// 148 Hz. Few's law puts that peak at this energy per metre.</summary>
    public const float SourcePa = 650f, SourceMetres = 2f, SourcePeakHz = 148f;
    public static float SourceEnergyPerMetre(float c = 343f)
    {
        float r = 0.63f * c / SourcePeakHz;
        return LightningPhysics.AmbientPressurePa * r * r;
    }

    /// <summary>The wave's amplitude 2 m out, for a channel that took <paramref name="energyPerMetre"/>.
    /// The acoustic energy per metre is a fixed share of what the channel took, and goes as the
    /// amplitude squared times the wave's length; Few's length goes as the energy's square root, so
    /// the amplitude goes as its fourth root.</summary>
    public static float AmplitudeAt2m(float energyPerMetre, float c = 343f)
        => SourcePa * MathF.Pow(MathF.Max(1f, energyPerMetre) / SourceEnergyPerMetre(c), 0.25f);

    /// <summary>Air's coefficient of nonlinearity, (gamma + 1) / 2.</summary>
    public const float Beta = 1.2f;

    /// <summary>Where the channel's wave stops spreading as a cylinder and starts spreading as a
    /// sphere: the length of one straight piece (Few 1969 treats each piece as an equivalent sphere).</summary>
    public const float CylinderToSphereMetres = LightningPhysics.StepMetres;

    /// <summary>The ground's pressure reflection for sound arriving from well above the horizon at the
    /// frequencies thunder has: near 1 for hard ground and for grass below a few hundred hertz
    /// (Embleton, J. Acoust. Soc. Am. 100, 1996). One figure for every surface; the city's own
    /// materials are not consulted.</summary>
    public const float GroundReflection = 0.9f;

    /// <summary>The standard atmosphere's lapse rate, K/m (ICAO).</summary>
    public const float LapseRate = 0.0065f;

    /// <summary>How deep a layer the ground wind's change with height is spread over, metres: the
    /// wind's shear is taken as the ground wind over this. A judgement; the server has one wind.</summary>
    public const float WindShearDepthMetres = 1000f;

    /// <summary>How much a source in the acoustic shadow loses, dB, at most, and how far past the
    /// shadow's edge (as a share of the distance to it) that is reached. 20-30 dB is where turbulent
    /// scattering into the shadow takes over (Embleton 1996).</summary>
    public const float ShadowMaxDb = 25f, ShadowRampShare = 0.5f;

    /// <summary>
    /// The thunder of <paramref name="strike"/> as heard at <paramref name="listener"/>, in up to
    /// <see cref="Options.MaxParts"/> parts, one per direction it comes from.
    /// </summary>
    public static List<Part> Render(in LightningStrike strike, Vector3 listener, Air air, Options? options = null)
        => Render(strike, LightningChannel.Build(strike), listener, air, options);

    public static List<Part> Render(in LightningStrike strike, LightningChannel channel, Vector3 listener, Air air, Options? options = null)
    {
        options ??= new Options();
        float c = AudioPhysics.SpeedOfSoundAt(air.TemperatureC);
        float groundY = listener.Y - options.EarAboveGround;

        // ── The pieces, each a straight bit of channel with the energy it took ─────────────────
        // The energy varies along the channel: Bestard, Farges and Coulouvrat (J. Geophys. Res. 130,
        // 2025) find the sound power within one flash "highly heterogeneous". Each 8 m step takes the
        // flash's energy per metre times a log-normal draw from the strike's seed, so every client
        // draws the same. It also keeps every piece from sending the identical N-wave, whose
        // spectrum has regularly spaced zeros: summed identical, the thunder has a comb in it.
        var pieces = new List<Piece>(4096);
        var draw = new Random(strike.Seed ^ 0x2C1B3C6D);
        for (int p = 0; p < channel.Paths.Count; p++)
        {
            var path = channel.Paths[p];
            float share = channel.EnergyShares[p];
            for (int i = 1; i < path.Length; i++)
            {
                float spread = MathF.Exp(EnergySpreadSigma * LightningPhysics.Gaussian(draw));
                float e = strike.EnergyPerMetre * share * (options.Plain ? 1f : spread);
                if (options.Plain) { pieces.Add(new Piece(path[i - 1], path[i], 1f, p == 0, e)); continue; }
                // Each step's fine structure from its own seed (not HashCode, which is salted per
                // process), so a listener a few metres away, who follows a different set of steps
                // down, still hears the same channel.
                FineStructure(pieces, path[i - 1], path[i], listener, e, p == 0, unchecked(strike.Seed * 73856093 ^ (p + 1) * 19349663 ^ i * 83492791));
            }
        }
        if (pieces.Count == 0) return new List<Part>();

        float dMin = float.MaxValue;
        foreach (var pc in pieces) dMin = MathF.Min(dMin, MathF.Min(Vector3.Distance(pc.A, listener), Vector3.Distance(pc.B, listener)));
        int fs = options.SampleRate > 0 ? options.SampleRate : ChooseRate(dMin, air);

        // Cut again finely enough near the listener that each piece is in its own far field.
        var fine = new List<Piece>(pieces.Count * 2);
        float lambdaTop = c / (0.4f * fs);
        foreach (var pc in pieces)
        {
            float d = Vector3.Distance((pc.A + pc.B) * 0.5f, listener);
            float max = Math.Clamp(MathF.Sqrt(lambdaTop * MathF.Max(1f, d)), 0.1f, LightningPhysics.StepMetres);
            float len = Vector3.Distance(pc.A, pc.B);
            int n = Math.Max(1, (int)MathF.Ceiling(len / max));
            for (int k = 0; k < n; k++)
                fine.Add(pc with { A = Vector3.Lerp(pc.A, pc.B, k / (float)n), B = Vector3.Lerp(pc.A, pc.B, (k + 1) / (float)n) });
        }

        // ── Which direction each piece comes from, grouped ───────────────────────────────────
        var dirs = new Vector3[fine.Count];
        var weights = new float[fine.Count];
        for (int i = 0; i < fine.Count; i++)
        {
            var mid = (fine[i].A + fine[i].B) * 0.5f;
            var to = mid - listener;
            float d = MathF.Max(1f, to.Length());
            dirs[i] = to / d;
            float len = Vector3.Distance(fine[i].A, fine[i].B);
            weights[i] = fine[i].Weight * len / d;
            weights[i] *= weights[i];
        }
        var (centres, member) = Cluster(dirs, weights, Math.Max(1, options.MaxParts), options.MergeDegrees);

        // ── The kernels: one element's wave, for its energy, as it arrives from each distance ──
        var kernels = new KernelBank(fs, c, air, strike.EnergyPerMetre * MathF.Exp(3f * EnergySpreadSigma));

        var parts = new List<Part>();
        for (int g = 0; g < centres.Count; g++)
        {
            // How long this part runs.
            double tFirst = double.MaxValue, tLast = 0;
            for (int i = 0; i < fine.Count; i++)
            {
                if (member[i] != g) continue;
                float d1 = Vector3.Distance(fine[i].A, listener), d2 = Vector3.Distance(fine[i].B, listener);
                tFirst = Math.Min(tFirst, MathF.Min(d1, d2) / c);
                float far = MathF.Max(d1, d2);
                if (options.Ground)
                    far = MathF.Max(far, MathF.Max(Vector3.Distance(Mirror(fine[i].A, groundY), listener), Vector3.Distance(Mirror(fine[i].B, groundY), listener)));
                tLast = Math.Max(tLast, far / c);
            }
            if (tFirst == double.MaxValue) continue;
            float strokeSpan = channel.StrokeTimes[^1];
            int pre = kernels.Pre;
            double start = tFirst - pre / (double)fs - 0.005;
            int length = (int)((tLast - start + strokeSpan) * fs) + kernels.MaxLength + pre + 16;
            if (length > fs * 240) length = fs * 240;   // four minutes is more thunder than there is
            var mine = new List<int>();
            for (int i = 0; i < fine.Count; i++) if (member[i] == g) mine.Add(i);
            // Split across a few cores, each into a buffer of its own, interleaved so each gets near and
            // far pieces alike: a strike 100 m away is heard a third of a second after the flash.
            int threads = options.Threads > 0 ? options.Threads : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            var buffers = new float[threads][];
            System.Threading.Tasks.Parallel.For(0, threads, t =>
            {
                var b = new float[length];
                for (int j = t; j < mine.Count; j += threads)
                {
                    var pc = fine[mine[j]];
                    Add(b, start, fs, pc.A, pc.B, listener, pc.Weight, pc.Energy, pc.Main, channel, kernels, c, air, groundY);
                    if (options.Ground)
                        Add(b, start, fs, Mirror(pc.A, groundY), Mirror(pc.B, groundY), listener,
                            pc.Weight * GroundReflection, pc.Energy, pc.Main, channel, kernels, c, air, groundY, shadowFrom: (pc.A + pc.B) * 0.5f);
                }
                buffers[t] = b;
            });
            var buf = buffers[0];
            for (int t = 1; t < threads; t++)
                for (int i = 0; i < buf.Length; i++) buf[i] += buffers[t][i];

            float peak = 0f;
            foreach (float x in buf) peak = MathF.Max(peak, MathF.Abs(x));
            if (!float.IsFinite(peak) || peak < ReferencePa * MathF.Pow(10f, options.MinAudibleDb / 20f)) continue;
            parts.Add(new Part { Pressure = buf, SampleRate = fs, Direction = centres[g], StartSeconds = (float)start, PeakPa = peak });
        }
        parts.Sort((x, y) => y.PeakPa.CompareTo(x.PeakPa));
        return parts;
    }

    /// <summary>The spread (natural log) of the energy each 8 m of channel takes about the flash's
    /// figure. A judgement standing for Bestard et al.'s heterogeneity; it is what keeps the summed
    /// N-waves' spectral zeros from lining up.</summary>
    public const float EnergySpreadSigma = 0.35f;

    /// <summary>Every part summed into one mono buffer, from the flash: for measuring.</summary>
    public static (float[] Pressure, int SampleRate, float StartSeconds) Mix(List<Part> parts)
    {
        if (parts.Count == 0) return (Array.Empty<float>(), 48000, 0f);
        int fs = parts[0].SampleRate;
        float start = float.MaxValue, end = 0f;
        foreach (var p in parts) { start = MathF.Min(start, p.StartSeconds); end = MathF.Max(end, p.StartSeconds + p.Seconds); }
        var y = new float[(int)((end - start) * fs) + 1];
        foreach (var p in parts)
        {
            int off = (int)((p.StartSeconds - start) * fs);
            for (int i = 0; i < p.Pressure.Length && off + i < y.Length; i++) y[off + i] += p.Pressure[i];
        }
        return (y, fs, start);
    }

    // ── Pieces ──────────────────────────────────────────────────────────────────────────────

    private readonly record struct Piece(Vector3 A, Vector3 B, float Weight, bool Main, float Energy);

    /// <summary>
    /// How far down the channel's tortuosity goes, as a length: the cylindrical relaxation radius,
    /// R_c = (E_l / (pi P0))^1/2 (Few 1969). Structure finer than the column of air the channel shocks
    /// does not radiate on its own ("micro-tortuosity", masked by the channel's expansion), so the
    /// channel is followed down to that scale and no further: about 0.85 m for a 2.3e5 J/m stroke.
    /// </summary>
    public static float RelaxationRadius(float energyPerMetre)
        => MathF.Sqrt(MathF.Max(1f, energyPerMetre) / (MathF.PI * LightningPhysics.AmbientPressurePa));

    /// <summary>Within this distance of the listener, each 8 m step is followed down to its fine
    /// structure; further off, the air has taken the high frequencies that structure makes (about
    /// 15 dB at 1 kHz by 3 km) and the step is heard as the straight piece it is at 8 m.</summary>
    public const float FineStructureMetres = 3000f;

    /// <summary>
    /// One 8 m step of channel as the pieces it is made of. Hill's 16 degrees is measured at the 8 m
    /// scale and the walk is built to it; below that, the step is taken to be as tortuous again, step
    /// for step, down to the relaxation radius: the same random turn about the same random axis, pulled
    /// to the step's far end so the 8 m geometry is kept. That a lightning channel is tortuous at every
    /// scale photographs resolve is an assumption here (a self-similar channel), and it is what decides
    /// how much of the thunder above a few hundred hertz there is: a straight 8 m piece seen obliquely
    /// sends almost nothing above 1/W (W its arrival spread), and the kinks inside it do.
    /// </summary>
    private static void FineStructure(List<Piece> into, Vector3 a, Vector3 b, Vector3 listener, float energy, bool main, int seed)
    {
        float len = Vector3.Distance(a, b);
        float r0 = RelaxationRadius(energy);
        int n = (int)MathF.Floor(len / MathF.Max(0.25f, r0));
        float d = Vector3.Distance((a + b) * 0.5f, listener);
        if (n < 2 || d > FineStructureMetres || len <= 0f)
        {
            into.Add(new Piece(a, b, 1f, main, energy));
            return;
        }
        var rng = new Random(seed);
        float step = len / n;
        var dir = (b - a) / len;
        float sigma = LightningPhysics.TurnSigmaDegrees * MathF.PI / 180f;
        var p = a;
        for (int k = 0; k < n; k++)
        {
            Vector3 next;
            if (k == n - 1) next = b;
            else
            {
                float theta = MathF.Abs(LightningPhysics.Gaussian(rng)) * sigma;
                float phi = (float)(rng.NextDouble() * Math.PI * 2.0);
                var u = MathF.Abs(dir.Y) < 0.9f ? Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY)) : Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitX));
                var v = Vector3.Cross(dir, u);
                var turned = dir * MathF.Cos(theta) + (u * MathF.Cos(phi) + v * MathF.Sin(phi)) * MathF.Sin(theta);
                // Pulled to the step's far end, harder as fewer sub-steps are left to get there.
                var home = b - p;
                float left = home.Length();
                if (left > 1e-4f) turned += home / left * (LightningPhysics.HomingShare * n / MathF.Max(1, n - k));
                dir = Vector3.Normalize(turned);
                next = p + dir * step;
            }
            into.Add(new Piece(p, next, 1f, main, energy));
            p = next;
        }
    }

    private static Vector3 Mirror(Vector3 p, float groundY) => new(p.X, 2f * groundY - p.Y, p.Z);

    /// <summary>
    /// One straight piece of channel, every stroke, added to the buffer. In its own far field the
    /// piece's elements arrive spread evenly over W = |d2 - d1| / c, so what it adds is the element's
    /// wave averaged over that spread: (K(t - t1) - K(t - t1 - W)) / W, K the wave's running integral
    /// (Ribner and Roy 1982). Broadside on, W is nothing and it is the wave itself, as loud as the
    /// piece is long. Each stroke heats the channel again with its own energy (the main channel only:
    /// subsequent strokes do not light the branches), so each has its own wave.
    /// </summary>
    private static void Add(float[] buf, double start, int fs, Vector3 a, Vector3 b, Vector3 listener, float weight, float energy,
                            bool main, LightningChannel channel, KernelBank kernels, float c, Air air, float groundY,
                            Vector3? shadowFrom = null)
    {
        float d1 = Vector3.Distance(a, listener), d2 = Vector3.Distance(b, listener);
        float len = Vector3.Distance(a, b);
        if (len <= 0f) return;
        float dm = MathF.Max(0.5f, 0.5f * (d1 + d2));
        var mid = shadowFrom ?? (a + b) * 0.5f;
        float amp = weight * len / dm * ShadowGain(mid, listener, groundY, air, c);
        if (amp <= 0f) return;
        double tA = MathF.Min(d1, d2) / c;
        double w = MathF.Abs(d2 - d1) / c;
        int strokes = main ? channel.StrokeTimes.Length : 1;
        for (int s = 0; s < strokes; s++)
        {
            var cls = kernels.ClassFor(energy * channel.StrokeEnergyShares[s]);
            var k = kernels.For(cls, dm);
            float g = amp * cls.Strength;
            double t = tA + channel.StrokeTimes[s] - start;
            if (w * fs < 1.0)
                Deposit(buf, (t + 0.5 * w) * fs - k.Pre, k.Wave, g);
            else
            {
                float gw = (float)(g / w);
                Deposit(buf, t * fs - k.Pre, k.Integral, gw);
                Deposit(buf, (t + w) * fs - k.Pre, k.Integral, -gw);
            }
        }
    }

    /// <summary>Adds <paramref name="kernel"/> × <paramref name="gain"/> starting at a fractional sample.</summary>
    private static void Deposit(float[] buf, double at, float[] kernel, float gain)
    {
        int i0 = (int)Math.Floor(at);
        float f = (float)(at - i0);
        float g0 = gain * (1f - f), g1 = gain * f;
        int n = kernel.Length;
        int lo = Math.Max(0, -i0), hi = Math.Min(n, buf.Length - i0 - 1);
        for (int j = lo; j < hi; j++)
        {
            float v = kernel[j];
            buf[i0 + j] += g0 * v;
            buf[i0 + j + 1] += g1 * v;
        }
    }

    // ── Refraction: the acoustic shadow ────────────────────────────────────────────────────────

    /// <summary>
    /// What refraction leaves of a piece at <paramref name="at"/>, as a gain. With the temperature
    /// falling with height the sound speed falls too, a ray curves upward with a radius of
    /// 1/a, a = lapse / (2 T), and the lowest ray from a source h up reaches the ground no further than
    /// x = sqrt(2 h / a), plus the same for the listener's own height (Fleagle 1949; a 4 km source,
    /// about 26 km). A wind blowing toward the listener bends sound down and pushes that edge out;
    /// blowing away, it pulls it in. In a storm the air near the ground is often cooled by the rain,
    /// which weakens or inverts the lapse and carries thunder further; the standard lapse is used.
    /// </summary>
    public static float ShadowGain(Vector3 at, Vector3 listener, float groundY, Air air, float c)
    {
        float h = MathF.Max(0f, at.Y - groundY);
        var flat = new Vector2(listener.X - at.X, listener.Z - at.Z);
        float x = flat.Length();
        if (x < 1f || h < 1f) return 1f;
        float tK = air.TemperatureC + 273.15f;
        float toward = (air.Wind.X * flat.X + air.Wind.Z * flat.Y) / x;
        float a = LapseRate / (2f * tK) - toward / WindShearDepthMetres / c;
        if (a <= 0f) return 1f;
        // The limiting ray grazes the ground between the two: each side's height buys its own reach.
        float edge = MathF.Sqrt(2f * h / a) + MathF.Sqrt(2f * MathF.Max(0f, listener.Y - groundY) / a);
        if (x <= edge) return 1f;
        float into = Math.Clamp((x / edge - 1f) / ShadowRampShare, 0f, 1f);
        return MathF.Pow(10f, -ShadowMaxDb * into / 20f);
    }

    /// <summary>The distance past which a source <paramref name="height"/> up is in the shadow, still air.</summary>
    public static float ShadowEdgeMetres(float height, float temperatureC)
        => MathF.Sqrt(2f * height / (LapseRate / (2f * (temperatureC + 273.15f))));

    // ── The element's wave ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The N-wave's length after <paramref name="distance"/> metres, seconds: weak-shock theory. The
    /// shock runs ahead at c(1 + beta p / 2 rho c^2) and the tail lags as much, so the wave lengthens by
    /// dT/dr = beta p / (rho c^3); its energy, p^2 T times the area it has spread over, is kept. Spread as a
    /// cylinder to <see cref="CylinderToSphereMetres"/> and as a sphere beyond, that integrates to
    /// T^3/2 = T0^3/2 + (3/2) K ln(r / r_c), K = beta p_c r_c T0^1/2 / (rho c^3).
    /// </summary>
    public static float LengthenedSeconds(float t0, float amplitudeAt2m, float distance, float c, float rho = 1.2f)
    {
        float rc = CylinderToSphereMetres;
        if (distance <= rc) return t0;
        float pc = amplitudeAt2m * MathF.Sqrt(SourceMetres / rc);
        float k = Beta * pc * rc * MathF.Sqrt(t0) / (rho * c * c * c);
        float t32 = MathF.Pow(t0, 1.5f) + 1.5f * k * MathF.Log(distance / rc);
        return MathF.Pow(t32, 2f / 3f);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), float> _lineResponses = new();

    /// <summary><see cref="LineResponse"/>, kept: it depends only on the wave's length (to 0.1 %) and
    /// the speed of sound (to 0.1 m/s), and every strike asks for the same few dozen.</summary>
    public static float LineResponseCached(float t, float c)
        => _lineResponses.GetOrAdd(((int)MathF.Round(MathF.Log(t) * 1000f), (int)MathF.Round(c * 10f)), _ => LineResponse(t, c));

    /// <summary>
    /// The peak pressure 2 m from a straight infinite line of unit strength, every element sending a
    /// unit N-wave of length <paramref name="t"/> with 1/r spreading: what turns the source's 650 Pa at
    /// 2 m into a strength per metre. Integrated numerically.
    /// </summary>
    public static float LineResponse(float t, float c)
    {
        float r = SourceMetres;
        float zMax = MathF.Sqrt(MathF.Pow(r + c * t * 1.05f, 2f) - r * r);
        const int nz = 2000, nt = 240;
        float dz = zMax / nz;
        float best = 0f;
        for (int it = 0; it <= nt; it++)
        {
            float tt = r / c + t * it / nt;
            double sum = 0;
            for (int iz = 0; iz < nz; iz++)
            {
                float z = (iz + 0.5f) * dz;
                float rr = MathF.Sqrt(r * r + z * z);
                float u = tt - rr / c;
                if (u < 0f || u > t) continue;
                sum += (1f - 2f * u / t) / rr;
            }
            best = MathF.Max(best, (float)(2.0 * sum * dz));
        }
        return MathF.Max(1e-6f, best);
    }

    /// <summary>The kernels, made as they are asked for: for each energy class and each distance, the
    /// element's unit N-wave, lengthened, with the air's absorption over that distance, and its running
    /// integral.</summary>
    private sealed class KernelBank
    {
        public sealed class Kernel { public float[] Wave = Array.Empty<float>(); public float[] Integral = Array.Empty<float>(); public int Pre; }

        /// <summary>One energy per metre: its wave's length and its amplitude 2 m out, and the strength
        /// per metre of channel that gives that amplitude.</summary>
        public sealed class Class
        {
            public int Index; public float T0; public float A2; public float Strength;
            public readonly System.Collections.Concurrent.ConcurrentDictionary<int, Kernel> Bins = new();
        }

        private readonly int _fs; private readonly float _c; private readonly Air _air;
        // Read from several cores at once (Render's Parallel.For): made at most a few times over, kept once.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Class> _classes = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, double[]> _alpha = new();
        /// <summary>Room before the arrival for the absorption's smoothing, which is symmetric: 5 ms.</summary>
        public int Pre => Math.Max(32, _fs / 200);
        public int MaxLength { get; }
        /// <summary>Distance bins per decade: neighbours differ by a third, which at 8 kHz and 100 m
        /// is 3 dB of air.</summary>
        private const int PerDecade = 8;
        /// <summary>Energy classes per doubling: neighbours' waves differ in length by 9 %.</summary>
        private const int PerDoubling = 4;

        public KernelBank(int fs, float c, Air air, float largestEnergy)
        {
            _fs = fs; _c = c; _air = air;
            float t0 = LightningPhysics.NWaveSeconds(largestEnergy, c);
            MaxLength = LengthFor(LengthenedSeconds(t0, AmplitudeAt2m(largestEnergy, c), 60000f, c));
        }

        public Class ClassFor(float energyPerMetre)
        {
            int index = (int)MathF.Round(MathF.Log2(MathF.Max(1f, energyPerMetre)) * PerDoubling);
            if (_classes.TryGetValue(index, out var cls)) return cls;
            float e = MathF.Pow(2f, index / (float)PerDoubling);
            float t0 = LightningPhysics.NWaveSeconds(e, _c);
            float a2 = AmplitudeAt2m(e, _c);
            return _classes.GetOrAdd(index, new Class { Index = index, T0 = t0, A2 = a2, Strength = a2 / LineResponseCached(t0, _c) });
        }

        public Kernel For(Class cls, float distance)
        {
            int bin = (int)MathF.Round(MathF.Log10(MathF.Max(1f, distance)) * PerDecade);
            if (cls.Bins.TryGetValue(bin, out var k)) return k;
            return cls.Bins.GetOrAdd(bin, Make(cls, MathF.Pow(10f, bin / (float)PerDecade)));
        }

        private int LengthFor(float t) => NextPow2((int)((1.3f * t + 0.012f) * _fs) + Pre);

        /// <summary>The air's absorption, dB per metre, at each bin of a transform this long.</summary>
        private double[] Alpha(int n)
        {
            if (_alpha.TryGetValue(n, out var a)) return a;
            a = new double[n / 2 + 1];
            for (int i = 1; i <= n / 2; i++)
                a[i] = AudioPhysics.AirAttenuationDbPerMetre((float)(i * (double)_fs / n), _air.TemperatureC, _air.Humidity, _air.PressureMb);
            return _alpha.GetOrAdd(n, a);
        }

        private Kernel Make(Class cls, float d)
        {
            int pre = Pre;
            float t = LengthenedSeconds(cls.T0, cls.A2, d, _c);
            int n = LengthFor(t);
            var alpha = Alpha(n);
            // Energy kept as it lengthens: the peak falls as sqrt(T0 / T).
            float peak = MathF.Sqrt(cls.T0 / t);
            var spec = new System.Numerics.Complex[n];
            for (int i = 0; i <= n / 2; i++)
            {
                double f = i * (double)_fs / n;
                var s = NWaveSpectrum(f, t) * peak;
                double keep = Math.Pow(10.0, -alpha[i] * d / 20.0);
                double shift = -2.0 * Math.PI * f * pre / _fs;
                spec[i] = s * keep * System.Numerics.Complex.FromPolarCoordinates(1.0, shift) * _fs;
                if (i > 0 && i < n / 2) spec[n - i] = System.Numerics.Complex.Conjugate(spec[i]);
            }
            // Inverse by conjugation round the forward transform.
            for (int i = 0; i < n; i++) spec[i] = System.Numerics.Complex.Conjugate(spec[i]);
            Spectrum.Fft(spec);
            var wave = new float[n];
            for (int i = 0; i < n; i++) wave[i] = (float)(spec[i].Real / n);
            // Fade the far end, which holds nothing but the transform's wrap.
            int fade = n / 8;
            for (int i = 0; i < fade; i++) wave[n - 1 - i] *= i / (float)fade;
            // The wave carries no net push (an N-wave's spectrum is zero at 0 Hz), so its running
            // integral must end at zero: what the fade and rounding leave is taken out evenly, or each
            // piece's two deposits would leave a step where the earlier one runs out.
            double mean = 0;
            for (int i = 0; i < n; i++) mean += wave[i];
            mean /= n;
            for (int i = 0; i < n; i++) wave[i] -= (float)mean;
            var integral = new float[n];
            double acc = 0;
            for (int i = 0; i < n; i++) { acc += wave[i] / (double)_fs; integral[i] = (float)acc; }
            // Cut where both have died away: most of the transform is the room left for the longest
            // wave, and every piece of channel adds the whole kernel.
            float wMax = 0f, iMax = 0f;
            for (int i = 0; i < n; i++) { wMax = MathF.Max(wMax, MathF.Abs(wave[i])); iMax = MathF.Max(iMax, MathF.Abs(integral[i])); }
            int end = n;
            while (end > pre + 1 && MathF.Abs(wave[end - 1]) < 1e-4f * wMax && MathF.Abs(integral[end - 1]) < 1e-4f * iMax) end--;
            if (end < n) { Array.Resize(ref wave, end); Array.Resize(ref integral, end); }
            return new Kernel { Wave = wave, Integral = integral, Pre = pre };
        }
    }

    /// <summary>
    /// The spectrum of a unit N-wave, +1 falling linearly to -1 over <paramref name="t"/> seconds, at
    /// <paramref name="f"/> Hz: closed form, so the shock's jumps are not aliased.
    /// </summary>
    public static System.Numerics.Complex NWaveSpectrum(double f, double t)
    {
        if (f <= 0.0) return System.Numerics.Complex.Zero;
        double w = 2.0 * Math.PI * f;
        var j = System.Numerics.Complex.ImaginaryOne;
        var e = System.Numerics.Complex.Exp(-j * w * t);
        var i0 = (1.0 - e) / (j * w);
        var i1 = (e * (1.0 + j * w * t) - 1.0) / (w * w);
        return i0 - 2.0 / t * i1;
    }

    // ── Rate and grouping ────────────────────────────────────────────────────────────────────

    /// <summary>The lowest of 48, 24 and 12 kHz whose top octave the air has taken at least 60 dB off
    /// by the time the nearest bit of channel is heard.</summary>
    public static int ChooseRate(float nearest, Air air)
    {
        int rate = 48000;
        foreach (int r in new[] { 24000, 12000 })
        {
            float loss = AudioPhysics.AirAttenuationDbPerMetre(0.4f * r, air.TemperatureC, air.Humidity, air.PressureMb) * nearest;
            if (loss >= 60f) rate = r; else break;
        }
        return rate;
    }

    /// <summary>Directions grouped by weighted k-means on the sphere, then any two groups closer than
    /// <paramref name="mergeDegrees"/> joined.</summary>
    private static (List<Vector3> Centres, int[] Member) Cluster(Vector3[] dirs, float[] w, int k, float mergeDegrees)
    {
        int n = dirs.Length;
        var member = new int[n];
        var centres = new List<Vector3>();
        int first = 0;
        for (int i = 1; i < n; i++) if (w[i] > w[first]) first = i;
        centres.Add(dirs[first]);
        while (centres.Count < k)
        {
            int far = -1; float worst = 1f;
            for (int i = 0; i < n; i++)
            {
                float best = -1f;
                foreach (var cdir in centres) best = MathF.Max(best, Vector3.Dot(dirs[i], cdir));
                if (best < worst) { worst = best; far = i; }
            }
            if (far < 0 || worst > MathF.Cos(mergeDegrees * MathF.PI / 180f)) break;
            centres.Add(dirs[far]);
        }
        for (int iter = 0; iter < 8; iter++)
        {
            Assign(dirs, centres, member);
            var sums = new Vector3[centres.Count];
            for (int i = 0; i < n; i++) sums[member[i]] += dirs[i] * MathF.Max(w[i], 1e-30f);
            for (int g = 0; g < centres.Count; g++)
                if (sums[g].LengthSquared() > 0f) centres[g] = Vector3.Normalize(sums[g]);
        }
        // Join groups that ended up close together.
        float cosMerge = MathF.Cos(mergeDegrees * MathF.PI / 180f);
        for (bool merged = true; merged && centres.Count > 1;)
        {
            merged = false;
            for (int a = 0; a < centres.Count && !merged; a++)
                for (int b = a + 1; b < centres.Count && !merged; b++)
                    if (Vector3.Dot(centres[a], centres[b]) > cosMerge)
                    {
                        centres[a] = Vector3.Normalize(centres[a] + centres[b]);
                        centres.RemoveAt(b);
                        merged = true;
                    }
        }
        Assign(dirs, centres, member);
        var weighted = new Vector3[centres.Count];
        for (int i = 0; i < n; i++) weighted[member[i]] += dirs[i] * MathF.Max(w[i], 1e-30f);
        for (int g = 0; g < centres.Count; g++)
            if (weighted[g].LengthSquared() > 0f) centres[g] = Vector3.Normalize(weighted[g]);
        return (centres, member);
    }

    private static void Assign(Vector3[] dirs, List<Vector3> centres, int[] member)
    {
        for (int i = 0; i < dirs.Length; i++)
        {
            int best = 0; float bd = -2f;
            for (int g = 0; g < centres.Count; g++)
            {
                float d = Vector3.Dot(dirs[i], centres[g]);
                if (d > bd) { bd = d; best = g; }
            }
            member[i] = best;
        }
    }

    private static int NextPow2(int v) { int p = 256; while (p < v) p <<= 1; return p; }
}
