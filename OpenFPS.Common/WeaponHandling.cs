using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>What a handling sound is: a reload of so many rounds, from empty or not, a dry fire, a bolt
/// cycled, the selector moved or the action worked.</summary>
public readonly record struct HandlingSpec(string WeaponId, bool IsReload, int Rounds, bool FromEmpty, bool IsCycle = false,
                                          bool IsSelector = false, bool IsAction = false);

/// <summary>
/// The noises of working a gun by hand: reloads, dry fire, a bolt cycled, the selector, the action.
///
/// Each is a routine, the steps a pair of hands goes through in order, each step a contact between two
/// pieces of metal or a slide of one along another. The routine is the one source of a reload's length:
/// the server finishes a reload when it ends, so what a player hears and when the gun is ready agree.
///
/// Every contact is made the way the approved door knock and car door are made: a burst of noise in
/// every octave band at the band's own level, falling at its own rate. No resonators: small steel
/// parts ring, but for a few milliseconds, and a resonator long enough to hear is an instrument. The
/// band shapes and falls are fitted to the drinkingwindgames handling recordings (inbox/weapons,
/// measured 2026-10-03 with <c>--reload-spec</c>): the clicks and catches have their energy at 2-8
/// kHz and fall 20 dB in about 6 ms; a magazine body sliding free peaks at 500 Hz-1 kHz; a bolt going
/// home is broad from 500 Hz up and falls 20 dB in 12-35 ms; a pump's fore-end hitting its stop rings
/// the receiver for 50-75 ms. The recordings are the spec only; nothing of them is played.
///
/// Levels are dB SPL at a metre, peak: a bolt slamming home about 82, a magazine seating 76, a
/// release button 64, the hand sliding a shell into a tube in the fifties. The loudest contact in a
/// routine is the level the world sound is sent at, and the render peaks at one.
/// </summary>
public static class WeaponHandling
{
    public const string ReloadPrefix = "reload:";
    public const string DryFirePrefix = "dryfire:";

    /// <summary>"reload:akm:30:e": the weapon, the rounds going in, and whether it was empty (so the
    /// bolt has to be sent home as well).</summary>
    public static string ReloadKey(WeaponDefinition weapon, int rounds, bool fromEmpty)
        => $"{ReloadPrefix}{weapon.Id}:{Math.Max(0, rounds)}:{(fromEmpty ? "e" : "p")}";

    public static string DryFireKey(WeaponDefinition weapon) => DryFirePrefix + weapon.Id;

    public const string CyclePrefix = "cycle:";

    /// <summary>"cycle:m700": a bolt worked by hand after a shot, to put the next round in the chamber.</summary>
    public static string CycleKey(WeaponDefinition weapon) => CyclePrefix + weapon.Id;

    /// <summary>Whether a gun is worked by hand between shots, and so makes its action's sound after
    /// every one rather than only on a reload.</summary>
    public static bool CyclesByHand(WeaponDefinition weapon) => weapon.Action == WeaponAction.BoltAction;

    /// <summary>How long after the shot the hand reaches the bolt: the recoil settles first.</summary>
    public const float CycleAfterShotSeconds = 0.35f;

    public const string SelectorPrefix = "selector:";

    /// <summary>"selector:akm": the fire selector or safety lever moved one detent.</summary>
    public static string SelectorKey(WeaponDefinition weapon) => SelectorPrefix + weapon.Id;

    public const string ActionPrefix = "action:";

    /// <summary>"action:akm": the action worked by hand once, as a gun is checked when it is handed over:
    /// the charging handle, slide or bolt drawn back and let go, a pump racked, a cylinder opened and shut.</summary>
    public static string ActionKey(WeaponDefinition weapon) => ActionPrefix + weapon.Id;

    public static bool TryParseKey(string? key, out HandlingSpec spec)
    {
        spec = default;
        if (string.IsNullOrEmpty(key)) return false;
        if (key.StartsWith(SelectorPrefix, StringComparison.Ordinal))
        {
            string id = key[SelectorPrefix.Length..];
            if (!WeaponRegistry.TryGet(id, out _)) return false;
            spec = new HandlingSpec(id, IsReload: false, 0, false, IsSelector: true);
            return true;
        }
        if (key.StartsWith(ActionPrefix, StringComparison.Ordinal))
        {
            string id = key[ActionPrefix.Length..];
            if (!WeaponRegistry.TryGet(id, out _)) return false;
            spec = new HandlingSpec(id, IsReload: false, 0, false, IsAction: true);
            return true;
        }
        if (key.StartsWith(CyclePrefix, StringComparison.Ordinal))
        {
            string id = key[CyclePrefix.Length..];
            if (!WeaponRegistry.TryGet(id, out _)) return false;
            spec = new HandlingSpec(id, IsReload: false, 0, false, IsCycle: true);
            return true;
        }
        if (key.StartsWith(DryFirePrefix, StringComparison.Ordinal))
        {
            string id = key[DryFirePrefix.Length..];
            if (!WeaponRegistry.TryGet(id, out _)) return false;
            spec = new HandlingSpec(id, IsReload: false, 0, false);
            return true;
        }
        if (!key.StartsWith(ReloadPrefix, StringComparison.Ordinal)) return false;
        var parts = key[ReloadPrefix.Length..].Split(':');
        if (parts.Length != 3 || !WeaponRegistry.TryGet(parts[0], out _)) return false;
        if (!int.TryParse(parts[1], out int rounds) || rounds < 0 || rounds > 200) return false;
        if (parts[2] is not ("e" or "p")) return false;
        spec = new HandlingSpec(parts[0], IsReload: true, rounds, parts[2] == "e");
        return true;
    }

    /// <summary>How long a reload of this many rounds takes, seconds.</summary>
    public static float ReloadSeconds(WeaponDefinition weapon, int rounds, bool fromEmpty)
        => Routine(weapon, isReload: true, rounds, fromEmpty).Done;

    /// <summary>How long the routine a key names takes, seconds.</summary>
    public static float Seconds(HandlingSpec spec)
        => WeaponRegistry.TryGet(spec.WeaponId, out var w) ? Routine(w, spec).Done : 0f;

    /// <summary>The loudest contact in the routine, dB SPL at a metre: the level to send it at.</summary>
    public static float LevelDb(HandlingSpec spec)
    {
        if (!WeaponRegistry.TryGet(spec.WeaponId, out var w)) return 60f;
        float loudest = 0f;
        foreach (var c in Routine(w, spec).Contacts) loudest = MathF.Max(loudest, c.Db);
        return loudest;
    }

    // ── The parts of a gun meeting each other ─────────────────────────────────────────────────

    private enum Part { Click, MagOut, Seat, Slam, Slide, ShellClick, Clack, Brass, Hammer }

    /// <summary>Octave centres the shapes are written at, Hz: those the spec was measured at.</summary>
    internal static readonly float[] Centres = { 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

    /// <summary>Each part's octave bands against its loudest, dB, and how long it takes to fall 20 dB,
    /// ms, as measured (the means of the takes that show each cleanly).</summary>
    private static (float[] Bands, float FallMs) Shape(Part part) => part switch
    {
        // Selector, release button, catch: AKM selector and AR catch takes.
        Part.Click => (new[] { -45f, -40f, -31f, -19f, -13f, -4f, -5f, 0f, -11f }, 6f),
        // A magazine body leaving the well: AKM metal, AR and Glock ejections.
        Part.MagOut => (new[] { -35f, -30f, -20f, -9f, 0f, -4f, -10f, -12f, -28f }, 15f),
        // The magazine's body meeting the well as it seats: AKM, AR, Glock insertions.
        Part.Seat => (new[] { -28f, -24f, -16f, -6f, -1f, 0f, -3f, -4f, -16f }, 10f),
        // A bolt or slide going home under its spring: AKM charging, AR bolt release, Glock slide.
        Part.Slam => (new[] { -32f, -30f, -24f, -15f, -7f, -3f, -2f, 0f, -11f }, 20f),
        // Metal sliding on metal: a charging handle drawn, a pump's fore-end travelling.
        Part.Slide => (new[] { -44f, -40f, -31f, -22f, -10f, -4f, -1f, 0f, -12f }, 0f),
        // A shell pushed past the shell stop: the shotgun loading takes.
        Part.ShellClick => (new[] { -42f, -44f, -42f, -32f, -18f, -6f, 0f, -5f, -20f }, 4f),
        // A pump's fore-end at its stop, ringing the receiver: the pump takes.
        Part.Clack => (new[] { -40f, -35f, -27f, -22f, -12f, -5f, 0f, -2f, -14f }, 60f),
        // An empty case falling out of a cylinder against the frame and the others.
        Part.Brass => (new[] { -50f, -50f, -45f, -35f, -25f, -12f, -4f, 0f, -6f }, 30f),
        // A hammer or striker falling on an empty chamber: AKM, AR and Glock trigger takes.
        _ => (new[] { -34f, -28f, -23f, -19f, -12f, -7f, -4f, 0f, -8f }, 15f),
    };

    private readonly record struct Contact(float At, Part Part, float Db, float Length = 0f);

    private readonly record struct Plan(List<Contact> Contacts, float Done);

    /// <summary>
    /// The routine: every contact in order with its time and level, and when the hands are done.
    /// Times come from the takes' own spacing where a take shows it (a seat and its catch 5 ms apart,
    /// a pump's two stops 0.1 s apart) and from how long the hands take between (fetching a magazine
    /// from a pouch, about three quarters of a second).
    /// </summary>
    private static Plan Routine(WeaponDefinition w, HandlingSpec spec)
        => spec.IsSelector ? SelectorMoved(w)
         : spec.IsAction ? ActionWorked(w)
         : Routine(w, spec.IsReload, spec.Rounds, spec.FromEmpty, spec.IsCycle);

    /// <summary>
    /// A selector or safety lever moved one detent. The AKM's is a long stamped-steel lever that slaps
    /// into its detent with a clack everybody near hears (the reason the takes have one); the AR's a
    /// small lever turning on a spring-loaded detent, a crisp click; a 1911's thumb safety, a pump's
    /// cross-bolt and a Model 700's slide are smaller and quieter still.
    /// </summary>
    private static Plan SelectorMoved(WeaponDefinition w)
    {
        var c = new List<Contact>();
        switch (w.Action)
        {
            case WeaponAction.Kalashnikov:
                c.Add(new(0.000f, Part.Slide, 50f, 0.03f));
                c.Add(new(0.030f, Part.Click, 72f));
                c.Add(new(0.032f, Part.Seat, 64f));
                return new Plan(c, 0.12f);
            case WeaponAction.Stoner:
                c.Add(new(0.000f, Part.Click, 64f));
                return new Plan(c, 0.08f);
            case WeaponAction.Pump:
                // A cross-bolt pushed through the trigger guard: a click and the bolt meeting its stop.
                c.Add(new(0.000f, Part.Click, 62f));
                c.Add(new(0.004f, Part.Seat, 56f));
                return new Plan(c, 0.08f);
            default:
                c.Add(new(0.000f, Part.Click, w.Action == WeaponAction.BoltAction ? 58f : 62f));
                return new Plan(c, 0.08f);
        }
    }

    /// <summary>
    /// The action worked once by hand, as a gun is checked when it changes hands: drawn back to show
    /// the chamber and let go home. The empty-chamber half of each reload's routine, on its own.
    /// </summary>
    private static Plan ActionWorked(WeaponDefinition w)
    {
        var c = new List<Contact>();
        switch (w.Action)
        {
            case WeaponAction.Kalashnikov:
                c.Add(new(0.00f, Part.Click, 60f));
                c.Add(new(0.02f, Part.Slide, 58f, 0.16f));
                c.Add(new(0.18f, Part.Clack, 70f));
                c.Add(new(0.25f, Part.Slam, 82f));
                c.Add(new(0.258f, Part.Click, 70f));
                return new Plan(c, 0.6f);
            case WeaponAction.Stoner:
                // The charging handle drawn to the rear and let go.
                c.Add(new(0.00f, Part.Click, 60f));
                c.Add(new(0.02f, Part.Slide, 56f, 0.12f));
                c.Add(new(0.14f, Part.Clack, 66f));
                c.Add(new(0.22f, Part.Slam, 80f));
                c.Add(new(0.228f, Part.Click, 68f));
                return new Plan(c, 0.55f);
            case WeaponAction.Pistol:
            {
                float frame = w.PolymerFrame ? -3f : 0f;
                c.Add(new(0.00f, Part.Slide, 54f, 0.08f));
                c.Add(new(0.08f, Part.Clack, 64f + frame));
                c.Add(new(0.14f, Part.Slam, 78f + frame));
                c.Add(new(0.147f, Part.Click, 66f));
                return new Plan(c, 0.45f);
            }
            case WeaponAction.Revolver:
                // The cylinder swung out to its stop, looked into, and swung shut on its latch.
                c.Add(new(0.00f, Part.Click, 62f));
                c.Add(new(0.06f, Part.Slide, 52f, 0.08f));
                c.Add(new(0.14f, Part.Clack, 66f));
                c.Add(new(0.60f, Part.Seat, 72f));
                c.Add(new(0.605f, Part.Click, 70f));
                return new Plan(c, 0.85f);
            case WeaponAction.Pump:
                c.Add(new(0.00f, Part.Slide, 58f, 0.10f));
                c.Add(new(0.10f, Part.Clack, 78f));
                c.Add(new(0.18f, Part.Slide, 58f, 0.10f));
                c.Add(new(0.28f, Part.Clack, 80f));
                c.Add(new(0.285f, Part.Click, 70f));
                return new Plan(c, 0.6f);
            default:
                // A bolt lifted, drawn, run forward and turned down: the cycle without a shot before it.
                return Routine(w, isReload: false, 0, false, isCycle: true);
        }
    }

    private static Plan Routine(WeaponDefinition w, bool isReload, int rounds, bool fromEmpty, bool isCycle = false)
    {
        var c = new List<Contact>();
        if (isCycle)
        {
            // A bolt worked after a shot: the handle lifted (cocking the striker, a click), the bolt
            // drawn back to its stop and the case flicked out, then run forward stripping the next
            // round off the magazine into the chamber, and the handle turned down to lock.
            c.Add(new(0.00f, Part.Click, 64f));
            c.Add(new(0.04f, Part.Slide, 58f, 0.12f));
            c.Add(new(0.16f, Part.Clack, 70f));
            c.Add(new(0.34f, Part.Slide, 56f, 0.11f));
            c.Add(new(0.45f, Part.Seat, 70f));
            c.Add(new(0.52f, Part.Click, 68f));
            return new Plan(c, 0.7f);
        }
        if (!isReload)
        {
            switch (w.Action)
            {
                case WeaponAction.BoltAction:
                    // A striker falling on an empty chamber inside a closed bolt: muffled by the bolt.
                    c.Add(new(0.005f, Part.Hammer, 66f));
                    return new Plan(c, 0.15f);
                case WeaponAction.Revolver:
                    // Double action: the pull turns the cylinder a chamber before the hammer falls.
                    c.Add(new(0.005f, Part.Click, 58f));
                    c.Add(new(0.065f, Part.Hammer, 70f));
                    return new Plan(c, 0.2f);
                case WeaponAction.Pistol:
                    // A striker in a plastic frame is quieter than a 1911's hammer on its steel.
                    c.Add(new(0.005f, Part.Hammer, w.PolymerFrame ? 64f : 68f));
                    return new Plan(c, 0.15f);
                case WeaponAction.Stoner:
                    c.Add(new(0.005f, Part.Hammer, 68f));
                    return new Plan(c, 0.15f);
                default:
                    c.Add(new(0.005f, Part.Hammer, 72f));
                    return new Plan(c, 0.15f);
            }
        }

        float done;
        switch (w.Action)
        {
            case WeaponAction.Kalashnikov:
                // The paddle, the magazine rocked forward and out of the well.
                c.Add(new(0.00f, Part.Click, 66f));
                c.Add(new(0.04f, Part.Slide, 56f, 0.10f));
                c.Add(new(0.14f, Part.MagOut, 70f));
                // A fresh one from the pouch: its front lug hooked in, rocked back, seated, caught.
                c.Add(new(1.05f, Part.Click, 64f));
                c.Add(new(1.07f, Part.Slide, 55f, 0.12f));
                c.Add(new(1.19f, Part.Seat, 76f));
                c.Add(new(1.195f, Part.Click, 72f));
                if (fromEmpty)
                {
                    // Over the top to the charging handle: drawn back against the spring, the
                    // carrier meeting the rear trunnion, let go, and the bolt slamming home.
                    c.Add(new(1.70f, Part.Click, 60f));
                    c.Add(new(1.72f, Part.Slide, 58f, 0.16f));
                    c.Add(new(1.88f, Part.Clack, 70f));
                    c.Add(new(1.95f, Part.Slam, 82f));
                    c.Add(new(1.958f, Part.Click, 70f));
                    done = 2.4f;
                }
                else done = 1.6f;
                break;

            case WeaponAction.Stoner:
                // The button, and the magazine dropping free.
                c.Add(new(0.00f, Part.Click, 64f));
                c.Add(new(0.03f, Part.MagOut, 68f));
                // Straight in and seated with a push.
                c.Add(new(0.95f, Part.Slide, 54f, 0.08f));
                c.Add(new(1.03f, Part.Seat, 76f));
                c.Add(new(1.036f, Part.Click, 72f));
                if (fromEmpty)
                {
                    // The bolt was held back on the catch: a slap on the catch and it goes home.
                    c.Add(new(1.40f, Part.Click, 66f));
                    c.Add(new(1.41f, Part.Slam, 82f));
                    c.Add(new(1.418f, Part.Click, 68f));
                    done = 1.8f;
                }
                else done = 1.3f;
                break;

            case WeaponAction.Pistol:
            {
                // Plastic meeting plastic is softer than steel on steel: a Glock's magazine and frame
                // against a 1911's.
                float frame = w.PolymerFrame ? -3f : 0f;
                c.Add(new(0.00f, Part.Click, 62f));
                c.Add(new(0.02f, Part.MagOut, 64f + frame));
                c.Add(new(0.75f, Part.Slide, 52f, 0.06f));
                c.Add(new(0.81f, Part.Seat, 74f + frame));
                c.Add(new(0.815f, Part.Click, 70f));
                if (fromEmpty)
                {
                    // The slide was locked back: the slide stop pressed, and the slide runs home.
                    c.Add(new(1.10f, Part.Click, 62f));
                    c.Add(new(1.115f, Part.Slam, 80f + frame));
                    c.Add(new(1.122f, Part.Click, 66f));
                    done = 1.45f;
                }
                else done = 1.05f;
                break;
            }

            case WeaponAction.Revolver:
                // The latch, the crane swinging out to its stop.
                c.Add(new(0.00f, Part.Click, 62f));
                c.Add(new(0.06f, Part.Slide, 52f, 0.08f));
                c.Add(new(0.14f, Part.Clack, 66f));
                // The ejector rod pushed, and the six cases falling out against the frame.
                c.Add(new(0.40f, Part.Slide, 54f, 0.10f));
                for (int i = 0; i < 6; i++) c.Add(new(0.50f + 0.055f * i, Part.Brass, 60f - 0.7f * i));
                // The speedloader: rounds sliding into the chambers, dropping home, the knob turned.
                c.Add(new(1.50f, Part.Slide, 52f, 0.08f));
                c.Add(new(1.58f, Part.Seat, 68f));
                c.Add(new(1.75f, Part.Click, 60f));
                // The cylinder closed and the latch catching it.
                c.Add(new(2.05f, Part.Seat, 72f));
                c.Add(new(2.055f, Part.Click, 70f));
                done = 2.4f;
                break;

            default:   // Pump
            {
                // A hand to the shells, then each one thumbed past the shell stop into the tube.
                int n = Math.Max(1, rounds);
                float t = 0.30f;
                for (int i = 0; i < n; i++)
                {
                    c.Add(new(t, Part.Slide, 52f, 0.07f));
                    c.Add(new(t + 0.07f, Part.ShellClick, 68f));
                    c.Add(new(t + 0.10f, Part.Click, 56f));
                    t += 0.55f;
                }
                if (fromEmpty)
                {
                    // Racked to chamber one: back to the stop, forward to the stop, the bolt locks.
                    t += 0.05f;
                    c.Add(new(t, Part.Slide, 58f, 0.10f));
                    c.Add(new(t + 0.10f, Part.Clack, 78f));
                    c.Add(new(t + 0.18f, Part.Slide, 58f, 0.10f));
                    c.Add(new(t + 0.28f, Part.Clack, 80f));
                    c.Add(new(t + 0.285f, Part.Click, 70f));
                    done = t + 0.45f;
                }
                else done = t - 0.55f + 0.30f;   // the last shell, and the hand back to the grip
                break;
            }

            case WeaponAction.BoltAction:
            {
                // The bolt lifted and drawn back to open the action.
                c.Add(new(0.00f, Part.Click, 62f));
                c.Add(new(0.04f, Part.Slide, 56f, 0.12f));
                c.Add(new(0.16f, Part.Clack, 68f));
                // A hand to the cartridges, then each one pressed down past the feed lips into the
                // box: a short slide of brass on steel and the round snapping under the lips.
                int n = Math.Max(1, rounds);
                float t = 0.85f;
                for (int i = 0; i < n; i++)
                {
                    c.Add(new(t, Part.Slide, 50f, 0.05f));
                    c.Add(new(t + 0.06f, Part.ShellClick, 64f));
                    t += 0.6f;
                }
                // The bolt run forward over the top round, chambering it, and turned down to lock.
                t += 0.05f;
                c.Add(new(t, Part.Slide, 56f, 0.11f));
                c.Add(new(t + 0.11f, Part.Seat, 72f));
                c.Add(new(t + 0.18f, Part.Click, 68f));
                done = t + 0.4f;
                break;
            }
        }
        return new Plan(c, done);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The routine as sound: dry, mono, normalised to a peak of one, a little longer than the routine
    /// so the last contact rings out. A seed varies each contact's bands by a decibel or two and the
    /// whole routine's pace by up to four per cent faster, never slower, so the sound cannot outlast
    /// the reload it belongs to.
    /// </summary>
    public static float[] Render(HandlingSpec spec, int sampleRate, int seed)
    {
        if (!WeaponRegistry.TryGet(spec.WeaponId, out var w)) return new float[16];
        var plan = Routine(w, spec);
        var rng = new Random(seed);
        float pace = 1f - 0.04f * (float)rng.NextDouble();
        float sr = sampleRate;
        int n = (int)((plan.Done * pace + 0.25f) * sr);
        var bands = BandNoise(n, sr, rng);
        var outp = new float[n];

        foreach (var c in plan.Contacts)
        {
            var (shape, fallMs) = Shape(c.Part);
            float at = c.At * pace + 0.005f;
            float level = c.Db + ((float)rng.NextDouble() - 0.5f) * 2f;
            for (int b = 0; b < Centres.Length; b++)
            {
                if (Centres[b] >= 0.45f * sr) continue;
                float gain = MathF.Pow(10f, (level - 80f + shape[b] + ((float)rng.NextDouble() - 0.5f) * 3f) / 20f);
                if (c.Part == Part.Slide) AddSlide(outp, bands[b], sr, at, c.Length * pace, gain, rng.Next());
                else
                {
                    // Falling 20 dB in fallMs is a T60 of three times it; the low bands of a struck
                    // part hang on a little longer than the top. Only a little: at a quarter power
                    // of frequency the renders measured 5-8 dB too much below 250 Hz against the
                    // takes, at a tenth they sit within about 4.
                    float t60 = 3f * fallMs / 1000f * MathF.Min(2f, MathF.Pow(2000f / Centres[b], 0.1f));
                    AddHit(outp, bands[b], sr, at, t60, gain);
                }
            }
        }

        float peak = 1e-9f;
        foreach (float v in outp) peak = MathF.Max(peak, MathF.Abs(v));
        for (int i = 0; i < n; i++) outp[i] /= peak;
        return outp;
    }

    /// <summary>A struck part on one band: up in half a millisecond, then down by its T60.</summary>
    private static void AddHit(float[] outp, float[] band, float sr, float at, float t60, float gain)
    {
        int a = (int)(at * sr);
        float attack = 0.0005f * sr, k = 6.9f / (t60 * sr);
        int end = Math.Min(outp.Length, a + (int)(t60 * sr * 1.3f));
        for (int i = Math.Max(0, a); i < end; i++)
        {
            float t = i - a;
            outp[i] += band[i] * gain * MathF.Min(1f, t / attack) * MathF.Exp(-k * t);
        }
    }

    /// <summary>
    /// Metal drawn along metal for <paramref name="seconds"/>: noise that rises and falls with the
    /// stroke, roughened by stick and slip, a new grip every five milliseconds or so.
    /// </summary>
    private static void AddSlide(float[] outp, float[] band, float sr, float at, float seconds, float gain, int seed)
    {
        var rng = new Random(seed);
        int a = (int)(at * sr), len = Math.Max(1, (int)(seconds * sr));
        int grain = Math.Max(1, (int)(0.005f * sr));
        float from = 0.5f, to = 0.5f + 0.5f * (float)rng.NextDouble();
        for (int i = 0; i < len && a + i < outp.Length; i++)
        {
            if (i % grain == 0) { from = to; to = 0.4f + 0.6f * (float)rng.NextDouble(); }
            float rough = from + (to - from) * (i % grain) / grain;
            float x = i / (float)len;
            float stroke = MathF.Sin(MathF.PI * MathF.Min(1f, x * 1.15f));   // up fast, ends on the stop
            if (a + i >= 0) outp[a + i] += band[a + i] * gain * stroke * stroke * rough;
        }
    }

    /// <summary>One white noise split into the octaves, each an octave-wide band-pass run twice
    /// (the filter the spec was measured with), so the shapes are in the units they were fitted in.</summary>
    private static float[][] BandNoise(int n, float sr, Random rng)
    {
        int pre = (int)(0.02f * sr);
        var white = new float[pre + n];
        for (int i = 0; i < white.Length; i++) white[i] = ((float)rng.NextDouble() * 2f - 1f) * 1.7320508f;
        var bands = new float[Centres.Length][];
        for (int b = 0; b < Centres.Length; b++)
        {
            bands[b] = new float[n];
            if (Centres[b] >= 0.45f * sr) continue;
            var f1 = new OctaveBandPass(Centres[b], sr);
            var f2 = new OctaveBandPass(Centres[b], sr);
            double sum = 1e-20;
            for (int i = 0; i < white.Length; i++)
            {
                float y = f2.Run(f1.Run(white[i]));
                if (i >= pre) { bands[b][i - pre] = y; sum += y * y; }
            }
            // Each band to unit RMS. White noise has energy in proportion to bandwidth, so an octave
            // at 8 kHz carries 21 dB more than one at 63 Hz; the shapes were measured as energy per
            // octave, so the bands must start level for a shape to mean what it says.
            float k = (float)(1.0 / Math.Sqrt(sum / n));
            for (int i = 0; i < n; i++) bands[b][i] *= k;
        }
        return bands;
    }

    /// <summary>An RBJ band-pass, an octave wide, unity at its centre.</summary>
    private struct OctaveBandPass
    {
        private readonly float _b0, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;
        public OctaveBandPass(float f, float sr)
        {
            double w = 2 * Math.PI * f / sr, alpha = Math.Sin(w) / (2 * 1.41), a0 = 1 + alpha;
            _b0 = (float)(alpha / a0); _b2 = (float)(-alpha / a0);
            _a1 = (float)(-2 * Math.Cos(w) / a0); _a2 = (float)((1 - alpha) / a0);
            _x1 = _x2 = _y1 = _y2 = 0f;
        }
        public float Run(float x)
        {
            float y = _b0 * x + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
