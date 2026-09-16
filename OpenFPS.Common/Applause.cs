using System;
using System.Globalization;

namespace OpenFPS.Common;

/// <summary>How many people, how worked up they are, and for how long.</summary>
public readonly record struct CrowdApplause(int Clappers, float Intensity, float Seconds);

/// <summary>
/// A crowd clapping, from the mechanism — because a clap is an impact and a crowd is a lot of them.
///
/// This is the one sound in a stadium that needs no recording at all, and it is worth saying why,
/// because the reasoning is the same one that decides everything else in this engine. A clap is two
/// flat surfaces meeting: a broadband contact burst, five to ten milliseconds long, shaped by the
/// pocket of air the hands trap between them. That is an impact, and impacts are already modelled.
/// A CROWD clapping is many of those arriving independently — a Poisson process, exactly like rain
/// on a panel or the shower of fragments after a pane goes.
///
/// What a recording cannot give you, and this can:
///
///   DENSITY IS A PARAMETER. Scattered polite clapping and a full ovation are not two sounds, they
///   are one sound at two arrival rates, and everything in between exists. The moment where
///   individual claps stop being individual and become a roar is not a crossfade between two
///   samples, it happens on its own at around twenty arrivals a second.
///
///   IT NEVER REPEATS. A loop of applause is audibly a loop within a few seconds, and a crowd that
///   loops is the clearest possible signal that a place is not real.
///
///   THE SIZE OF THE CROWD IS AUDIBLE, and correctly. Independent sources sum incoherently, so the
///   level grows as the SQUARE ROOT of the number of them: doubling a crowd adds three decibels, not
///   six. That is also what makes this affordable — past a few hundred simultaneous clappers the sum
///   is statistically indistinguishable from noise, so a bounded number of them are rendered and the
///   rest are accounted for by that square root. The budget is the physics, not a shortcut past it.
/// </summary>
public static class Applause
{
    /// <summary>Claps per second per person when they are barely bothering.</summary>
    public const float PoliteRate = 1.8f;

    /// <summary>...and when they are on their feet. People cannot clap much faster than this.</summary>
    public const float OvationRate = 4.6f;

    /// <summary>
    /// How many clappers are actually rendered before the square root takes over.
    ///
    /// A real budget rather than a fudge: the sum of many independent sources is noise long before
    /// this, so rendering more of them adds cost and no information.
    /// </summary>
    public const int MaxRendered = 320;

    /// <summary>One clap at one metre, dB SPL. A single pair of hands is about this.</summary>
    public const float SingleClapDb = 89f;

    /// <summary>How much of the crack rides on top of the body. The edge is what says "hands" — at
    /// zero the whole thing rustles.</summary>
    public const float CrackLevel = 0.85f;

    /// <summary>What the rendered buffer is scaled to, as RMS. Well under full scale, because the
    /// coincidences in a dense crowd need somewhere to go.</summary>
    public const float TargetRms = 0.16f;

    /// <summary>
    /// How loud a crowd of this size is at one metre, dB SPL.
    ///
    /// Ten log ten of the count, not twenty: independent sources add in POWER, because their phases
    /// are unrelated. It is why a stadium is loud and not deafening, and why the difference between
    /// five hundred people and a thousand is three decibels and not a doubling.
    /// </summary>
    public static float LevelDb(int clappers, float intensity)
        => SingleClapDb + 10f * MathF.Log10(Math.Max(1, clappers)) + 6f * Math.Clamp(intensity, 0f, 1f);

    /// <summary>
    /// Renders the applause. Mono, normalised, at whatever rate the caller renders in.
    ///
    /// The envelope is a swell and a fall rather than a switch, because a crowd does not start
    /// together: the first people to react are a fraction of a second ahead of the rest, and the
    /// ragged edge of that is most of what makes it sound like people rather than like a sample
    /// being triggered.
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
        // Everybody past the budget is accounted for in amplitude, because incoherent sources sum in
        // power. This is the same square root that makes LevelDb a ten-log and not a twenty-log.
        float crowdGain = MathF.Sqrt(clappers / (float)rendered);

        float rate = PoliteRate + (OvationRate - PoliteRate) * intensity;

        // The swell: how long the crowd takes to get going, and how long to give up. Both shorten as
        // it gets more excited — an ovation starts almost together.
        float riseSeconds = MathHelperLerp(0.9f, 0.25f, intensity);
        float fallSeconds = MathHelperLerp(2.2f, 1.1f, intensity);

        // EACH PERSON IS A PERSON, and this is the difference between applause and static.
        //
        // The first version drew the arrival times from one Poisson process for the whole crowd. A
        // Poisson process is memoryless — it has no rhythm by construction, and a dense stream of
        // identical memoryless clicks is the definition of static. That is what it sounded like.
        //
        // Real applause is not memoryless, because a person clapping has a TEMPO. They keep roughly
        // to it, they drift off it, and no two of them share one. The sum of a few hundred slightly
        // different quasi-periodic trains is a texture with structure in it — you can pick individual
        // people out of the near edge of a crowd, and that is exactly what you cannot do with noise.
        //
        // So each rendered clapper gets an identity and keeps it for the whole burst: where they are
        // sitting, what their hands sound like, how fast they clap, and where in their own cycle they
        // happen to be. Everything else falls out of that.
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

                // They keep to their tempo, and they do not keep to it exactly.
                t += period * (0.85 + 0.3 * rng.NextDouble());
            }
        }

        Normalise(buffer);
        return buffer;
    }

    /// <summary>
    /// One pair of hands.
    ///
    /// A short broadband contact burst through the resonance of the air pocket the palms trap. That
    /// pocket is what makes a clap a clap rather than a click, and its size is why no two people
    /// sound alike: cupped hands trap more air and ring lower and hollower, flat palms trap almost
    /// none and crack. A real crowd spans that whole range at once, which is why the spread here is
    /// wide and drawn per clap rather than being one number.
    /// </summary>
    /// <summary>One person: where they are sitting and what their hands sound like. Drawn once and
    /// kept, because a person does not move seats or change hands between claps.</summary>
    private readonly record struct Clapper(float CavityHz, float DistanceGain, float Loudness);

    private static Clapper NewClapper(Random rng, float intensity)
    {
        // 900 Hz is a deeply cupped hand, 3300 a flat-palmed crack, and a crowd spans the range.
        float cavityHz = (900f + (float)rng.NextDouble() * 2400f) * (1f + 0.3f * intensity);

        // How far away they are. People fill an AREA, so the number of them at a given distance grows
        // with that distance while the level falls as 1/r — which leaves a few near ones much louder
        // than the wash behind them. That spread is most of what makes a crowd sound like people
        // rather than like a texture: without it every clap is the same size. Area-weighted, hence
        // the square root.
        float near = 1.5f;
        float distance = near + MathF.Sqrt((float)rng.NextDouble()) * 28f;

        // ...and people do not clap equally hard either, on top of where they are sitting.
        float loudness = MathF.Pow(10f, (float)(rng.NextDouble() * 10.0 - 5.0) / 20f);

        return new Clapper(cavityHz, near / distance, loudness);
    }

    /// <summary>
    /// One clap: a CRACK, not a ring — and getting that wrong is audible immediately.
    ///
    /// The first version put a short noise burst through a sharp resonator, which is a perfectly good
    /// model of something: a water droplet. A drip IS a brief narrowband resonance, so a thousand of
    /// them a second is a thousand drips a second, and a listener said so in four words.
    ///
    /// A clap is the opposite kind of event. Two broad flat surfaces meet and stop, and what radiates
    /// is a very short BROADBAND burst — two or three milliseconds, with real energy from a couple of
    /// hundred hertz to several kilohertz. The pocket of air the palms trap colours that burst, but it
    /// colours it rather than sustaining it, so the cavity is a tilt across a wide band and not a note.
    /// </summary>
    private static void AddClap(float[] into, int at, int sampleRate, Random rng, Clapper who, float gain)
    {
        float level = gain * who.DistanceGain * who.Loudness;

        // TWO PARTS, because a clap has an edge and the edge is the whole of what identifies it.
        //
        // One decaying broadband burst is not enough, and the failure is specific: hundreds of soft
        // bursts a second average into a rustle, and a listener called it leaves blowing. What is
        // missing is the ATTACK. Palms meeting is very nearly a step change in pressure — the contact
        // is complete in well under a millisecond — and that near-discontinuity is the crack. The
        // couple of milliseconds after it, as the trapped air escapes and the hands rebound, is the
        // body. Separating them lets the crack stay sharp and broadband while the body carries the
        // colour, instead of one filter having to do both and softening the edge to do it.
        float crackTau = 0.00025f + (float)rng.NextDouble() * 0.00035f;   // 0.25-0.6 ms
        float bodyTau = 0.0015f + (float)rng.NextDouble() * 0.0025f;      // 1.5-4 ms

        int len = Math.Min((int)(bodyTau * 6f * sampleRate), into.Length - at);
        if (len <= 2) return;

        // The body is band-limited by the air pocket. The crack is barely filtered at all, because a
        // step has energy everywhere and filtering it is what took the edge off.
        float bodyLp = Alpha(who.CavityHz * 1.7f, sampleRate);
        float crackLp = Alpha(9000f, sampleRate);
        float hpA = Alpha(320f, sampleRate);

        float lp = 0f, hp = 0f, clp = 0f;
        float bodyDecay = MathF.Exp(-1f / (bodyTau * sampleRate));
        float crackDecay = MathF.Exp(-1f / (crackTau * sampleRate));
        float bodyAmp = 1f, crackAmp = CrackLevel;

        for (int i = 0; i < len; i++)
        {
            float n1 = (float)(rng.NextDouble() * 2 - 1);
            float n2 = (float)(rng.NextDouble() * 2 - 1);

            lp += bodyLp * (n1 * bodyAmp - lp);
            hp += hpA * (lp - hp);
            clp += crackLp * (n2 * crackAmp - clp);

            bodyAmp *= bodyDecay;
            crackAmp *= crackDecay;

            into[at + i] += ((lp - hp) + clp) * level;
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
    /// Normalised to its ENERGY, not to its loudest sample.
    ///
    /// Peak normalisation is wrong for a dense texture and it was making the crowd quiet. Hundreds of
    /// claps a second overlap, and every so often enough of them land together to make a peak far
    /// above the average; scaling that peak to full scale then drags everything else down with it, so
    /// the busier the crowd the quieter it renders — which is backwards. Scaling by RMS keeps the
    /// loudness where the level says it should be, and the rare coincidences are caught by the
    /// limiter at the end rather than being allowed to set the gain for the whole buffer.
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

    /// <summary>The key a <see cref="TransientSound"/> carries to ask for this. Same escape hatch a
    /// gunshot uses, and for the same reason: four characters and seven numbers cannot describe a
    /// thousand people, and a model that can already exists.</summary>
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
