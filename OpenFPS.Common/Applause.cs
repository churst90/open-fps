using System.Globalization;

namespace OpenFPS.Common;

/// <summary>How many people, how worked up they are, and for how long.</summary>
public readonly record struct CrowdApplause(int Clappers, float Intensity, float Seconds);

/// <summary>
/// A crowd clapping, from the mechanism: a clap is two hands meeting, a short broadband contact shaped
/// by the pocket of air they trap, and a crowd is many clappers each keeping their own tempo.
///
/// Density is a parameter (polite clapping and an ovation are one sound at two rates, and claps blur
/// into a roar on their own at around twenty arrivals a second), it never repeats, and the size of the
/// crowd is audible: independent sources add in power, so doubling a crowd adds three decibels. Past a
/// few hundred clappers the sum is noise, so a bounded number are rendered and the rest are carried by
/// that square root.
/// </summary>
public static class Applause
{
    /// <summary>Claps per second per person when they are barely bothering.</summary>
    public const float PoliteRate = 1.8f;

    /// <summary>...and when they are on their feet. People cannot clap much faster than this.</summary>
    public const float OvationRate = 4.6f;

    /// <summary>How many clappers are rendered before the square root takes over: the sum of many
    /// independent sources is noise long before this.</summary>
    public const int MaxRendered = 320;

    /// <summary>One clap at one metre, dB SPL. A pair of hands measures from the mid eighties to about
    /// a hundred; this is the middle, which a full stand heard across a racetrack wanted.</summary>
    public const float SingleClapDb = 92f;

    /// <summary>How much of the crack rides on top of the body. The edge is what says "hands"; at zero
    /// the whole thing rustles, and at 2.0 there was too much above 4 kHz.</summary>
    public const float CrackLevel = 1.2f;

    // Fitted 2026-09-19 against sixty-seven real claps (approved/applause/Slow Clapping  HQ Sound
    // Effects.mp3, cut by tools/split_footsteps.py, compared with --applause compare=DIR): the peak at
    // 1-2 kHz, a flesh plateau from 125 to 500 Hz about 8 dB down, almost nothing above 4 kHz, and 20 dB
    // down 5 ms after the peak. A shape guessed by ear is not the measured one: re-fit, never nudge.

    /// <summary>The note of the pocket of air an average adult's palms trap, Hz. A big pair of hands
    /// lands about a fifth below, a child's a fifth above.</summary>
    public const float CavityHzAdult = 1400f;

    /// <summary>How far above its own note the pocket lets the contact burst through, as a multiple of
    /// the cavity frequency: the recording falls 12 dB in the octave above the peak (2.8 left the burst
    /// flat to 4 kHz).</summary>
    public const float CavityCeiling = 1.0f;

    /// <summary>Where the flesh thumps, Hz, for an adult: the centre of the 125-500 Hz plateau.</summary>
    public const float ThumpHzAdult = 240f;

    /// <summary>How long the flesh keeps moving, seconds. Two soft palms stop in a few milliseconds;
    /// at 4-12 ms every clap outlasted the real ones twice over.</summary>
    public const float ThumpTauMin = 0.0015f, ThumpTauMax = 0.003f;

    /// <summary>Above this the crack is filtered off, Hz. A real clap has almost nothing above 4 kHz;
    /// what lives up there is paper.</summary>
    public const float CrackCeilingHz = 2500f;

    /// <summary>
    /// Below this nothing radiates, Hz: two hands are a small source, and the recording's spectrum drops
    /// off a cliff under 60 Hz. A two-pole high-pass on the whole clap; 160 because the recording's
    /// cliff is steeper than two poles, and a corner an octave above it lands 30-60 Hz within 3 dB
    /// (60-125 Hz reads 2 dB light, the cheaper error).
    /// </summary>
    public const float RadiationFloorHz = 160f;

    /// <summary>Where the contact burst's own high-pass sits, as a fraction of the cavity note. Below
    /// the note is the flesh's job; the burst carrying it too made the plateau 6 dB too flat.</summary>
    public const float BurstFloorRatio = 0.4f;

    /// <summary>
    /// How hard the contact drives the thump, two palms of flesh meeting: the half of a clap that
    /// survives distance. Air takes the 4-12 kHz half away over a couple of hundred metres, so a clap of
    /// edge alone arrives across a stadium as a crinkle. 0.12 puts the recording's plateau 8 dB under
    /// the peak.
    /// </summary>
    public const float ThumpLevel = 0.12f;

    /// <summary>What the rendered buffer is scaled to, as RMS. Well under full scale, because the
    /// coincidences in a dense crowd need somewhere to go.</summary>
    public const float TargetRms = 0.16f;

    /// <summary>How loud a crowd of this size is at one metre, dB SPL: ten log ten of the count, because
    /// independent sources add in power.</summary>
    public static float LevelDb(int clappers, float intensity)
        => SingleClapDb + 10f * MathF.Log10(Math.Max(1, clappers)) + 6f * Math.Clamp(intensity, 0f, 1f);

    /// <summary>How closely people sit, per square metre: about half a square metre each, seated.</summary>
    public const float PeoplePerSquareMetre = 2.0f;

    /// <summary>
    /// The radius of the patch a crowd of this many people occupies: the reference distance for the
    /// inverse law, because inside a crowd's footprint there is no spreading (a metre towards one
    /// clapper is a metre from another).
    /// </summary>
    public static float SpreadRadiusMetres(int people)
        => MathF.Sqrt(Math.Max(1, people) / PeoplePerSquareMetre / MathF.PI);

    /// <summary>
    /// The nearest crowd worth telling apart from this one. A rendered crowd is a buffer cached under its
    /// key; unquantised, every reaction on the speedway was a fresh 3.8 s render and a fresh sound in the
    /// mixer, ninety times a minute. No step is audible: a tenth of the head count is under half a
    /// decibel, and a crowd keeps no schedule to a quarter second.
    /// </summary>
    public static CrowdApplause Quantise(CrowdApplause spec)
    {
        int clappers = Math.Max(1, spec.Clappers);
        int step = Math.Max(1, clappers / 10);
        clappers = Math.Max(1, (clappers + step / 2) / step * step);

        float intensity = MathF.Round(Math.Clamp(spec.Intensity, 0f, 1f) * 20f) / 20f;
        float seconds = MathF.Round(Math.Clamp(spec.Seconds, 0.2f, 12f) * 4f) / 4f;
        return new CrowdApplause(clappers, intensity, MathF.Max(0.25f, seconds));
    }

    /// <summary>
    /// Renders the applause, mono and normalised. The envelope swells and falls because a crowd does
    /// not start together, and that ragged edge is most of what makes it sound like people.
    /// </summary>
    public static float[] Render(CrowdApplause spec, int sampleRate, int seed)
    {
        float seconds = Math.Clamp(spec.Seconds, 0.2f, 12f);
        float intensity = Math.Clamp(spec.Intensity, 0f, 1f);
        int clappers = Math.Max(1, spec.Clappers);
        int n = (int)(seconds * sampleRate);
        var buffer = new float[n];
        var rng = new Random(seed);

        int rendered = Math.Min(clappers, MaxRendered);
        // Everybody past the budget, in amplitude: incoherent sources sum in power.
        float crowdGain = MathF.Sqrt(clappers / (float)rendered);

        float rate = PoliteRate + (OvationRate - PoliteRate) * intensity;

        // An ovation starts almost together.
        float riseSeconds = MathHelperLerp(0.9f, 0.25f, intensity);
        float fallSeconds = MathHelperLerp(2.2f, 1.1f, intensity);

        // Each clapper keeps a seat, a pair of hands and a tempo of their own for the whole burst. One
        // Poisson process for the whole crowd has no rhythm and sounded like static; a few hundred
        // quasi-periodic trains are a texture you can pick people out of.
        for (int p = 0; p < rendered; p++)
        {
            float period = 1f / (rate * (0.75f + 0.5f * (float)rng.NextDouble()));  // their own tempo
            double t = rng.NextDouble() * period;                                    // ...and their own phase
            var person = NewClapper(rng, intensity);

            while (t < seconds)
            {
                float env = Envelope((float)t, seconds, riseSeconds, fallSeconds);
                int at = (int)(t * sampleRate);
                if (env > 0.001f && at < n) AddClap(buffer, at, sampleRate, rng, person, env * crowdGain);

                t += period * (0.85 + 0.3 * rng.NextDouble());
            }
        }

        Normalise(buffer);
        return buffer;
    }

    /// <summary>One person: where they are sitting and what their hands sound like, drawn once and kept.</summary>
    private readonly record struct Clapper(float CavityHz, float DistanceGain, float Loudness,
                                          float Cupping, float ThumpHz);

    private static Clapper NewClapper(Random rng, float intensity)
    {
        // Hand size moves the sound two ways at once: a bigger pocket of air rings lower, and a bigger
        // area radiates more, so big hands are deeper and louder together (drawn apart, it came out
        // thin). 0.7 a small child, 1.0 an average adult, 1.35 a large man; skewed toward adults.
        float size = 0.7f + (float)Math.Pow(rng.NextDouble(), 0.7) * 0.65f;

        // A cavity's note goes inversely with its size. By ear it drifted to 800 Hz and 2,200 read thin;
        // measured, the peak is at 1-2 kHz and the weight is the thump, not a lower cavity.
        float cavityHz = CavityHzAdult / size * (0.85f + 0.3f * (float)rng.NextDouble());
        cavityHz *= 1f + 0.25f * intensity;          // harder clapping is brighter as well as louder

        // People fill an area, so a few near ones stand out of the wash behind: area-weighted, hence
        // the square root.
        float near = 1.5f;
        float distance = near + MathF.Sqrt((float)rng.NextDouble()) * 28f;

        // Loudness goes with the radiating area, the square of the size, and people clap unequally.
        float loudness = size * size * MathF.Pow(10f, (float)(rng.NextDouble() * 10.0 - 5.0) / 20f);

        // Cupped hands trap the air and the pocket rings (the pock); flat palms let it out sideways and
        // leave little but the contact. The mixture is the texture.
        float cupping = 0.25f + 0.75f * (float)rng.NextDouble();

        // The thump: the flesh of two palms deforming and rebounding, slow and low, most of what
        // survives a hundred metres of air.
        float thumpHz = ThumpHzAdult * (0.8f + 0.5f * (float)rng.NextDouble()) / size;

        return new Clapper(cavityHz, near / distance, loudness, cupping, thumpHz);
    }

    /// <summary>
    /// One clap: a crack, not a ring. Two broad surfaces meet and stop, and what radiates is a broadband
    /// burst of two or three milliseconds, coloured (not sustained) by the pocket of air between the
    /// palms. A noise burst through a sharp resonator is a water drip, and a crowd of them was heard as
    /// pouring water.
    /// </summary>
    private static void AddClap(float[] into, int at, int sampleRate, Random rng, Clapper who, float gain)
    {
        float level = gain * who.DistanceGain * who.Loudness;

        // Two parts. The crack is the near-step of palms meeting, kept sharp and broadband (without it,
        // hundreds of soft bursts a second were heard as leaves blowing); the body, as the trapped air
        // escapes and the hands rebound, carries the colour. A quarter-millisecond crack through 9 kHz
        // was all edge and no weight.
        float crackTau = 0.0005f + (float)rng.NextDouble() * 0.0009f;      // 0.5-1.4 ms
        float bodyTau = 0.001f + (float)rng.NextDouble() * 0.0015f;       // 1-2.5 ms
        // A real clap is 20 dB down 5 ms after its peak, and the thump is the last thing left.
        float thumpTau = ThumpTauMin + (float)rng.NextDouble() * (ThumpTauMax - ThumpTauMin);

        // Eight time constants of the flesh, 70 dB down: three cut the clap off at -40 dB, mid-ring.
        int len = Math.Min((int)(thumpTau * 8f * sampleRate), into.Length - at);
        if (len <= 2) return;

        // The body is band-limited by the pocket, a poor resonator with leaky walls, so a wide band (the
        // narrower, the more the crowd sounded like one thing). The crack is barely filtered: filtering
        // a step is what takes the edge off. Its high-pass follows the hand, as the cavity does.
        float bodyLp = Alpha(who.CavityHz * CavityCeiling, sampleRate);
        float crackLp = Alpha(CrackCeilingHz, sampleRate);
        float hpA = Alpha(who.CavityHz * BurstFloorRatio, sampleRate);

        // The pocket does ring, damped hard: two soft leaky palms give a Q of about three, a pock over
        // in a few milliseconds. A sharp resonator was a drip and a plain tilt a crushed bag; cupped
        // hands ring more, flat ones barely.
        float q = 1.5f + 4f * who.Cupping;
        float w = 2f * MathF.PI * Math.Clamp(who.CavityHz, 60f, sampleRate * 0.45f) / sampleRate;
        float r = MathF.Exp(-w / (2f * q));
        float cav1 = 2f * r * MathF.Cos(w), cav2 = -r * r;
        // Normalised so the ringing changes the clap's shape and not its level.
        float cavNorm = 1f - r;
        float cavA = 0f, cavB = 0f;

        // The flesh: the same resonator, a much lower note and a longer decay.
        float tw = 2f * MathF.PI * Math.Clamp(who.ThumpHz, 40f, sampleRate * 0.45f) / sampleRate;
        float tr = MathF.Exp(-1f / (thumpTau * sampleRate));
        float th1 = 2f * tr * MathF.Cos(tw), th2 = -tr * tr;
        float thNorm = 1f - tr;
        float thA = 0f, thB = 0f;
        // A mass on a spring radiates its velocity, so the output is differenced (a zero at DC; all-pole,
        // the 30-60 Hz band was 10 dB too loud) and scaled back up so the note keeps its level.
        float thVel = 1f / (2f * MathF.Sin(tw / 2f));

        float lp = 0f, hp = 0f, clp = 0f, clp2 = 0f, chp = 0f;
        // The radiation floor: two poles, so it is a cliff and not a slope.
        float floorA = Alpha(RadiationFloorHz, sampleRate);
        float f1 = 0f, f2 = 0f;
        float bodyDecay = MathF.Exp(-1f / (bodyTau * sampleRate));
        float crackDecay = MathF.Exp(-1f / (crackTau * sampleRate));
        float thumpDrive = MathF.Exp(-1f / (0.0008f * sampleRate));
        float bodyAmp = 1f, crackAmp = CrackLevel, thumpAmp = ThumpLevel;

        for (int i = 0; i < len; i++)
        {
            float n1 = (float)(rng.NextDouble() * 2 - 1);
            float n2 = (float)(rng.NextDouble() * 2 - 1);
            float n3 = (float)(rng.NextDouble() * 2 - 1);

            lp += bodyLp * (n1 * bodyAmp - lp);
            hp += hpA * (lp - hp);
            float drive = lp - hp;

            float cav = drive * cavNorm + cav1 * cavA + cav2 * cavB;
            cavB = cavA; cavA = cav;

            // The thump is driven for a fraction of a millisecond and rings on after the rest.
            float thump = n3 * thumpAmp * thNorm + th1 * thA + th2 * thB;
            float thumpOut = (thump - thA) * thVel;
            thB = thA; thA = thump;

            clp += crackLp * (n2 * crackAmp - clp);
            clp2 += crackLp * (clp - clp2);
            chp += hpA * (clp2 - chp);
            float crack = clp2 - chp;

            bodyAmp *= bodyDecay;
            crackAmp *= crackDecay;
            thumpAmp *= thumpDrive;

            float y = drive * (1f - who.Cupping * 0.25f)
                    + cav * (1.3f * who.Cupping)
                    + thumpOut * 0.30f
                    + crack;
            // Two first-order high-passes in series: twelve decibels an octave under the floor.
            f1 += floorA * (y - f1);
            float h1 = y - f1;
            f2 += floorA * (h1 - f2);
            into[at + i] += (h1 - f2) * level;
        }
    }

    private static float Alpha(float hz, int sampleRate)
        => 1f - MathF.Exp(-2f * MathF.PI * MathF.Min(hz, sampleRate * 0.45f) / sampleRate);

    private static float Envelope(float t, float total, float rise, float fall)
    {
        float up = rise <= 0f ? 1f : Math.Clamp(t / rise, 0f, 1f);
        float remaining = total - t;
        float down = fall <= 0f ? 1f : Math.Clamp(remaining / fall, 0f, 1f);
        // Squared on the way in, so the swell accelerates the way a crowd joining in does.
        return up * up * down;
    }

    /// <summary>
    /// Normalised to its energy, not its loudest sample: in a dense texture a rare coincidence of claps
    /// would set the gain, and the busier the crowd the quieter it rendered. The coincidences are
    /// rounded off instead.
    /// </summary>
    private static void Normalise(float[] buffer)
    {
        if (buffer.Length == 0) return;

        double sum = 0;
        foreach (float v in buffer) sum += (double)v * v;
        float rms = (float)Math.Sqrt(sum / buffer.Length);
        if (rms <= 1e-9f) return;

        float k = TargetRms / rms;
        for (int i = 0; i < buffer.Length; i++)
        {
            float y = buffer[i] * k;
            // Only the coincidences, and rounded rather than clipped: a clipped crowd is a buzz.
            buffer[i] = y > 0.9f || y < -0.9f ? MathF.Tanh(y * 1.111f) * 0.9f : y;
        }
    }

    private static float MathHelperLerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    // ── Naming, so it can travel as a SynthKey ───────────────────────────────────────────────────

    /// <summary>The key for one person clapping once: the player's own hands (T, on foot).</summary>
    public const string ClapKey = "clap";

    /// <summary>
    /// One clap by one person, at arm's length: a clapper drawn as the crowd draws them, but sitting
    /// nowhere — no seat-distance loss — and a single pair of hands meeting once. Normalised; the
    /// level is <see cref="SingleClapDb"/>, placed by whoever emits it.
    /// </summary>
    public static float[] RenderClap(int sampleRate, int seed)
    {
        var rng = new Random(seed);
        var who = NewClapper(rng, 0.7f) with { DistanceGain = 1f };
        var buffer = new float[(int)(0.25f * sampleRate)];
        AddClap(buffer, 0, sampleRate, rng, who, 1f);
        Normalise(buffer);
        return buffer;
    }

    /// <summary>The key a <see cref="TransientSound"/> carries to ask for this.</summary>
    public static string Key(CrowdApplause spec)
        => string.Format(CultureInfo.InvariantCulture, "applause:{0}:{1:0.##}:{2:0.##}",
                         Math.Max(1, spec.Clappers), Math.Clamp(spec.Intensity, 0f, 1f),
                         Math.Clamp(spec.Seconds, 0.2f, 12f));

    public static bool TryParseKey(string key, out CrowdApplause spec)
    {
        spec = default;
        if (string.IsNullOrEmpty(key) || !key.StartsWith("applause:", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = key["applause:".Length..].Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int clappers)) return false;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float intensity)) return false;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds)) return false;

        spec = new CrowdApplause(clappers, intensity, seconds);
        return true;
    }
}
