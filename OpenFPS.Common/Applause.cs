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

    /// <summary>
    /// One clap at one metre, dB SPL.
    ///
    /// A single pair of hands measures anywhere from the mid eighties to about a hundred depending on
    /// how hard somebody means it; this sits in the middle of that, and three decibels up from where it
    /// was, which was judged a touch quiet against a full stand heard across a racetrack.
    /// </summary>
    public const float SingleClapDb = 92f;

    /// <summary>How much of the crack rides on top of the body. The edge is what says "hands" — at
    /// zero the whole thing rustles. Fitted (below): at 2.0 there was too much above 4 kHz.</summary>
    public const float CrackLevel = 1.2f;

    // ── Fitted against a recording, 2026-09-19 ───────────────────────────────────────────────────
    //
    // Everything below was first settled by ear and then MEASURED against sixty-seven real claps
    // (`inbox/Slow Clapping  HQ Sound Effects.mp3`, cut up by `tools/split_footsteps.py`, compared
    // with `--applause compare=DIR`). The ear had put the cavity at 800 Hz "because hands are bigger
    // and softer than they sound"; the recording puts the peak of a clap squarely at 1-2 kHz, with the
    // flesh under it a broad plateau from 125 to 500 Hz about eight decibels down, almost nothing
    // above 4 kHz, and the whole thing twenty decibels down FIVE milliseconds after its peak. The
    // model had been eleven decibels heavy at 250-500 Hz, nine light at 1-2 kHz, and twice too slow —
    // a duller, boxier, longer clap than any pair of hands makes. Same lesson as the footsteps: a
    // shape guessed by ear and a shape measured are not the same shape. Re-fit, never nudge.

    /// <summary>The note of the pocket of air an average adult's palms trap, Hz. A big pair of hands
    /// lands about a fifth below, a child's a fifth above.</summary>
    public const float CavityHzAdult = 1400f;

    /// <summary>How far above its own note the pocket lets the contact burst through, as a multiple
    /// of the cavity frequency. At the note itself: the recording falls twelve decibels in the octave
    /// above the peak, and 2.8 (the old value) had the burst flat to four kilohertz.</summary>
    public const float CavityCeiling = 1.0f;

    /// <summary>Where the flesh thumps, Hz, for an adult: the centre of the 125-500 Hz plateau.</summary>
    public const float ThumpHzAdult = 240f;

    /// <summary>How long the flesh keeps moving, seconds. Two soft palms stop in a few milliseconds;
    /// the 4-12 ms this used to be is what made every clap outlast the real ones by a factor of two.</summary>
    public const float ThumpTauMin = 0.0015f, ThumpTauMax = 0.003f;

    /// <summary>Above this the crack is filtered off, Hz. A real clap has almost nothing above 4 kHz;
    /// what lives up there is paper.</summary>
    public const float CrackCeilingHz = 2500f;

    /// <summary>
    /// Below this nothing radiates, Hz — two hands are a small source and a small source cannot push
    /// low frequencies, so the spectrum drops off a cliff under sixty hertz in the recording. Applied
    /// as a two-pole high-pass to the whole clap, because the crack noise had never been high-passed
    /// at all and the thump resonator passed DC, and between them the 30-60 Hz band was ten decibels
    /// too loud. A hundred and sixty because the recording's cliff is steeper than two poles, and a
    /// corner an octave above it is what lands 30-60 Hz within three decibels; 60-125 Hz reads two
    /// decibels light for it, which is the cheaper of the two errors.
    /// </summary>
    public const float RadiationFloorHz = 160f;

    /// <summary>Where the contact burst's own high-pass sits, as a fraction of the cavity note. The
    /// burst below the pocket's note is the flesh's job, and letting the burst carry it too made the
    /// plateau under the peak six decibels too flat.</summary>
    public const float BurstFloorRatio = 0.4f;

    /// <summary>
    /// How hard the contact drives the thump: the low, slow part that is two palms of flesh meeting.
    ///
    /// It is the half of a clap that survives distance. Air takes the four-to-twelve-kilohertz half of
    /// a clap away over a couple of hundred metres, so a clap made ONLY of edge — which is what this
    /// was — arrives across a stadium as a crinkle, and a clap with a thump under it arrives as a clap.
    /// Fitted (above) at 0.12 from 1.1: the recording's plateau under the peak is eight decibels down,
    /// and at 1.1 the thump alone put it level with the peak. The thump also radiates its velocity now,
    /// which is a bandpass, so a given level buys much less rumble than it did.
    /// </summary>
    public const float ThumpLevel = 0.12f;

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

    /// <summary>How closely people sit, per square metre. A seated crowd is about half a square metre
    /// each; standing on a terrace is tighter, and nothing in this engine has a terrace yet.</summary>
    public const float PeoplePerSquareMetre = 2.0f;

    /// <summary>
    /// How far across a crowd of this many people is — the radius of the patch they occupy.
    ///
    /// This is the reference distance for the inverse law, and it is not one metre. Sound falls off
    /// with distance because the same energy spreads over a bigger sphere, and inside a crowd's own
    /// footprint there is no such spreading: stepping a metre towards one clapper steps you a metre
    /// away from another. Four hundred people at half a square metre each fill two hundred square
    /// metres, which is a patch about eight metres across, so eight metres is where the falling-off
    /// starts.
    /// </summary>
    public static float SpreadRadiusMetres(int people)
        => MathF.Sqrt(Math.Max(1, people) / PeoplePerSquareMetre / MathF.PI);

    /// <summary>
    /// The nearest crowd this engine will bother telling apart from this one.
    ///
    /// A rendered crowd is a BUFFER, cached under its key, and two crowds whose numbers differ by a
    /// person are the same sound — but nothing quantised the key, so every reaction on the speedway
    /// was a fresh 3.8-second render of three hundred people and a fresh sound registered with the
    /// mixer, ninety times a minute, for ever. The same reasoning is already written above
    /// WorldAudioPlayer.IdFor and it had simply never reached the escape hatch.
    ///
    /// The steps are coarse on purpose and none of them is audible: a tenth of the head count is under
    /// half a decibel, and a crowd does not clap to a schedule accurate to a quarter second.
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
    private readonly record struct Clapper(float CavityHz, float DistanceGain, float Loudness,
                                          float Cupping, float ThumpHz);

    private static Clapper NewClapper(Random rng, float intensity)
    {
        // HAND SIZE, and everything about the clap follows from it.
        //
        // A crowd is not one kind of person. Children, women and men have palms that differ by most
        // of a factor of two across their span, and that single number moves the sound in two ways at
        // once — which is why drawing a frequency and a loudness independently, as this did at first,
        // came out thin. Big hands trap a bigger pocket of air, and a bigger cavity resonates LOWER;
        // big hands also present more AREA, and area is what radiates, so they are louder as well.
        // Deeper and louder together, which is what a listener means by meaty.
        //
        // 0.7 is a small child, 1.0 an average adult, 1.35 a large man. Skewed toward adults,
        // because a grandstand mostly is.
        float size = 0.7f + (float)Math.Pow(rng.NextDouble(), 0.7) * 0.65f;

        // A cavity's note goes inversely with its linear size. About 1.4 kHz for an average adult
        // (CavityHzAdult); a big pair of hands lands nearer a kilohertz, a child's nearer two. The ear
        // had settled this at 800 over three passes — "hands are bigger and softer than they sound" —
        // and the first estimate of 2,200 had read as thin. Both were wrong for the same reason: with
        // no flesh under it the only way to make the clap sound heavy was to drag the cavity down.
        // Measured against a recording the peak of a clap is at 1-2 kHz, and the weight is the thump.
        float cavityHz = CavityHzAdult / size * (0.85f + 0.3f * (float)rng.NextDouble());
        cavityHz *= 1f + 0.25f * intensity;          // harder clapping is brighter as well as louder

        // ...and how far away they are. People fill an AREA, so the number of them at a given
        // distance grows with it while the level falls as 1/r — which leaves a few near ones much
        // louder than the wash behind them. Without that spread every clap is the same size and the
        // sum is a texture rather than a room. Area-weighted, hence the square root.
        float near = 1.5f;
        float distance = near + MathF.Sqrt((float)rng.NextDouble()) * 28f;

        // Loudness goes with the radiating AREA, so with the square of the size — and on top of that
        // people simply do not clap equally hard.
        float loudness = size * size * MathF.Pow(10f, (float)(rng.NextDouble() * 10.0 - 5.0) / 20f);

        // HOW THEY CLAP, which is the other half of what a clap sounds like and was not modelled at
        // all. Palm to palm with cupped hands traps the air and the pocket RINGS — that is the pock a
        // listener calls a clap. Flat palms, or fingers into a palm, let it out sideways and leave
        // almost nothing but the contact. A crowd is a mixture, and the mixture is the texture.
        float cupping = 0.25f + 0.75f * (float)rng.NextDouble();

        // ...and the thump under it: the flesh of two palms deforming and rebounding. It is slow,
        // it is low, and it is most of what survives a hundred metres of air — which is why a stand
        // full of people sounded like a crinkling bag from across the track. Scales with the hand,
        // like everything else here.
        float thumpHz = ThumpHzAdult * (0.8f + 0.5f * (float)rng.NextDouble()) / size;

        return new Clapper(cavityHz, near / distance, loudness, cupping, thumpHz);
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
        // Longer and wider than it was. At a quarter of a millisecond through a 9 kHz low-pass the
        // crack was all edge and no weight — thin, and the listener said so. A real clap's edge has
        // BODY under it, because the surfaces meeting are hands rather than sticks, and the bigger
        // the hands the lower that edge reaches. Tied to the cavity, so a big pair of palms cracks
        // deeper as well as louder.
        float crackTau = 0.0005f + (float)rng.NextDouble() * 0.0009f;      // 0.5-1.4 ms
        float bodyTau = 0.001f + (float)rng.NextDouble() * 0.0015f;       // 1-2.5 ms
        // The flesh is slower than the edge, and not by as much as it was: a real clap is twenty
        // decibels down five milliseconds after its peak, and the thump is the last thing left.
        float thumpTau = ThumpTauMin + (float)rng.NextDouble() * (ThumpTauMax - ThumpTauMin);

        // Long enough for the slowest thing in it — the flesh — to have died away on its own: at
        // eight time constants it is seventy decibels down. Five was fine when the thump ran for
        // twelve milliseconds and left a truncation nobody could hear; at three it cut the clap off
        // at minus forty, mid-ring.
        int len = Math.Min((int)(thumpTau * 8f * sampleRate), into.Length - at);
        if (len <= 2) return;

        // The body is band-limited by the air pocket. The crack is barely filtered at all, because a
        // step has energy everywhere and filtering it is what took the edge off.
        // A WIDE band, not a narrow one. The pocket of air between two palms is a poor resonator —
        // soft, leaky walls and an opening most of its own size — so it tilts a broad spectrum rather
        // than picking a note out of it. Two and a half octaves wide here, and the narrower it was
        // made the more the whole crowd sounded like one thing rather than like many.
        float bodyLp = Alpha(who.CavityHz * CavityCeiling, sampleRate);
        float crackLp = Alpha(CrackCeilingHz, sampleRate);
        // The crack's own high-pass follows the hand: big palms crack down into the low hundreds,
        // small ones do not. It is the same size that set the cavity.
        float hpA = Alpha(who.CavityHz * BurstFloorRatio, sampleRate);

        // THE POCKET OF AIR RINGS, and leaving it out is what made a crowd sound like a bag being
        // crushed. The first version of this had a sharp resonator and a listener called it pouring
        // water, which is correct — a drip IS a brief narrow resonance — so it was replaced by a plain
        // tilt, and a tilt has no note in it at all. Both were wrong in the same way: the question is
        // not whether the cavity resonates but HOW HARD IT IS DAMPED. Two soft leaky palms give a Q of
        // about three: a pock, audible as a pitch, over in a few milliseconds. Cupped hands trap more
        // of it and ring more; flat hands let it out sideways and barely ring at all.
        float q = 1.5f + 4f * who.Cupping;
        float w = 2f * MathF.PI * Math.Clamp(who.CavityHz, 60f, sampleRate * 0.45f) / sampleRate;
        float r = MathF.Exp(-w / (2f * q));
        float cav1 = 2f * r * MathF.Cos(w), cav2 = -r * r;
        // A two-pole resonator has enormous gain at its own note — the closer it is to ringing for
        // ever, the louder. Normalised so the ringing changes the SHAPE of the clap and not its level.
        float cavNorm = 1f - r;
        float cavA = 0f, cavB = 0f;

        // ...and under all of it, the flesh. Two palms meeting is a soft heavy impact before it is
        // anything else, and that is a low damped thud rather than a click. Same resonator, a much
        // lower note and a much longer decay — it is the part that carries across a stadium.
        float tw = 2f * MathF.PI * Math.Clamp(who.ThumpHz, 40f, sampleRate * 0.45f) / sampleRate;
        float tr = MathF.Exp(-1f / (thumpTau * sampleRate));
        float th1 = 2f * tr * MathF.Cos(tw), th2 = -tr * tr;
        float thNorm = 1f - tr;
        float thA = 0f, thB = 0f;
        // What radiates from a mass on a spring is its VELOCITY, so the thump is the resonator's
        // output differenced — a bandpass with a zero at DC. As an all-pole filter it had passed
        // everything below its own note straight through, at a gain that went UP as its decay was
        // shortened, and the 30-60 Hz band was ten decibels too loud for exactly that reason. The
        // difference is scaled back up by the resonant frequency so the note keeps its level.
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

            // The broadband burst that excites everything, still band-limited by the pocket.
            lp += bodyLp * (n1 * bodyAmp - lp);
            hp += hpA * (lp - hp);
            float drive = lp - hp;

            // Through the cavity, which rings for a few milliseconds at its own note.
            float cav = drive * cavNorm + cav1 * cavA + cav2 * cavB;
            cavB = cavA; cavA = cav;

            // The thump is excited by the contact itself — a fraction of a millisecond of it — and
            // then rings on its own long after the rest has gone.
            float thump = n3 * thumpAmp * thNorm + th1 * thA + th2 * thB;
            float thumpOut = (thump - thA) * thVel;
            thB = thA; thA = thump;

            // The crack: the same hands, so the same floor under it and a two-pole ceiling over it.
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
