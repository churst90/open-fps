using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// A firefight in a concrete street, heard from a fixed point in the middle of it.
///
/// The scene is deliberately the hardest one for an audio engine and the most useful one for a player:
/// a straight street twenty-four metres wide with six-storey concrete blocks down both sides and two
/// cross streets cutting through. Everything a shot can do is in it — a near miss, a long shot, a
/// subsonic round that makes no crack at all, a shooter above the listener on a roof, and one firing
/// from behind a corner so the direct path is blocked and only the reflected one arrives.
///
/// What this is actually testing, in the order it matters:
///
///   1. That the crack arrives before the report, from a DIFFERENT DIRECTION, with a gap that is a
///      readable measure of range. See <see cref="Ballistics"/> and <see cref="ShotResolver"/>.
///   2. That the street has sides. A concrete canyon has a real reverberation time and open ground
///      does not, and the difference has to be audible or the geometry may as well not be there.
///   3. That a weapon is identifiable by ear — an AKM, an AR-15, a Glock, a .45 and a shotgun should
///      be five distinguishable sounds, not one sound at five volumes.
///
/// Headless (<c>--battle</c>) checks all three as numbers. <c>--battle-live</c> plays it.
/// </summary>
public static class BattleSpike
{
    // ── The street ──────────────────────────────────────────────────────────────────────────────
    private const float StreetHalfWidth = 12f;    // 24 m kerb to kerb
    private const float BlockDepth = 34f;         // how far along the street one building runs
    private const float BlockWidth = 30f;         // how far back from the street it goes
    private const float BlockHeight = 26f;        // six storeys or so
    private const float SideStreetGap = 14f;

    /// <summary>The listener, standing in the middle of the road, facing up it.</summary>
    private static readonly Vector3 Ear = new(0f, 1.7f, 0f);

    /// <summary><paramref name="MissBy"/> is how far the round goes past the listener, and where. Nobody
    /// shoots through anybody's head: a real round passes by, and how far by is what sets the width of
    /// the crack — a metre away is a sharp tear, twenty metres is a flat snap. Aiming every shot exactly
    /// at the ear made every miss distance 0.0 m, which is the one value the crack model never sees.</summary>
    private sealed record Shooter(string WeaponId, Vector3 Position, string Where, int Shots, float At,
                                  Vector3 MissBy);

    /// <summary>
    /// The order of events. Chosen so that each shot demonstrates one thing and the contrast with the
    /// shot before it is the point — the Glock immediately after the AKM, the .45 immediately after
    /// something supersonic, the rooftop AR-15 after something at street level.
    /// </summary>
    private static readonly Shooter[] Fight =
    {
        new("glock",   new Vector3(-4f,  1.4f,  -9f), "behind you and to the left, ten metres",
            3,  0.0f, new Vector3(0.7f, 0.2f, 0f)),
        new("akm",     new Vector3( 2f,  1.5f,  48f), "up the street, fifty metres",
            6,  2.6f, new Vector3(-1.2f, 0.4f, 0f)),
        new("pistol",  new Vector3(-6f,  1.4f,  70f), "seventy metres — a .45, and it does not crack",
            3,  5.6f, new Vector3(1.8f, 0.3f, 0f)),
        new("ar15",    new Vector3(21f, 27f,   150f), "a rooftop, a hundred and fifty metres",
            4,  8.2f, new Vector3(-2.4f, 0.9f, 0f)),
        // In the mouth of the first cross street, thirty-five metres out: inside buckshot's range, and
        // with a building corner between it and the listener so the direct path is broken. At z=96 this
        // shooter was standing INSIDE a building, and at 65 m its pellets stopped before they arrived.
        new("shotgun", new Vector3(24f,  1.5f,  25f), "round the corner, down the first cross street",
            2, 11.4f, new Vector3(0f, 0.5f, 0f)),
        new("akm",     new Vector3(-3f,  1.5f, 178f), "far up the street, most of two hundred metres",
            5, 14.0f, new Vector3(3.1f, 0.6f, 0f)),
    };

    public static int Run(bool live)
    {
        AcousticRegistry.Initialize();
        float c = AudioPhysics.SpeedOfSound;
        var boxes = BuildStreet();

        Console.WriteLine($"  Concrete Row: {boxes.Count} surfaces — a {StreetHalfWidth * 2:F0} m street " +
                          $"with {BlockHeight:F0} m blocks down both sides.");
        Console.WriteLine($"  Listener at the centre line, ear height {Ear.Y:F1} m, facing up the street.\n");

        BuildSurfaces(boxes);
        Console.WriteLine($"  {_surfaces.Length} reflecting faces.\n");

        bool ok = true;
        ok &= CheckShootersAreOutside(boxes);
        ok &= ReportReflections(c);
        ok &= ReportTimings(c);
        ok &= ReportReverberation(boxes, out float canyonRt60, out float openRt60);

        if (live)
        {
            int rc = Play(boxes, canyonRt60, c);
            if (rc != 0) ok = false;
        }
        else
        {
            Console.WriteLine("\n  (--battle-live plays it. Headphones: the crack and the report come from " +
                              "different directions and the street answers each shot.)");
        }

        Console.WriteLine(ok
            ? "\nRESULT: PASS — crack leads report by a readable gap, and the canyon reverberates where open ground does not."
            : "\nRESULT: FAIL — see the unmet conditions above.");
        return ok ? 0 : 1;
    }

    /// <summary>Where a shot actually goes: past the listener by <c>MissBy</c>, and on until it hits
    /// something or runs out of range. One function so the table and the playback cannot disagree.</summary>
    private static void Aim(WeaponDefinition w, Vector3 from, Vector3 missBy,
                           out Vector3 direction, out Vector3 impact)
    {
        Vector3 target = Ear + missBy;
        direction = Vector3.Normalize(target - from);
        float reach = MathF.Min(w.MaxRange, Vector3.Distance(from, target) + 22f);
        impact = from + direction * reach;
    }

    /// <summary>
    /// That nobody is standing inside a building.
    ///
    /// Worth a check rather than an eye: two of the six were, and neither said so. A shooter embedded
    /// in solid concrete is not an obviously wrong number — it is a plausible-looking coordinate that
    /// produces a fully occluded, barely audible source, which in a test whose whole output is a sound
    /// reads as "the occlusion is working well" rather than as a bug in the scene.
    /// </summary>
    private static bool CheckShootersAreOutside(List<SteamAudioScene.Box> boxes)
    {
        bool ok = true;
        foreach (var s in Fight)
        {
            foreach (var b in boxes)
            {
                Vector3 half = b.Size * 0.5f;
                Vector3 d = s.Position - b.Center;
                if (MathF.Abs(d.X) < half.X && MathF.Abs(d.Y) < half.Y && MathF.Abs(d.Z) < half.Z)
                {
                    Console.WriteLine($"  [FAIL] the {s.WeaponId} at {s.Position} is inside a building " +
                                      $"centred on {b.Center}");
                    ok = false;
                }
            }
        }
        if (ok) Console.WriteLine("  [PASS] every shooter is standing in the open\n");
        return ok;
    }

    // ── 1. The timings ──────────────────────────────────────────────────────────────────────────

    private static bool ReportTimings(float c)
    {
        Console.WriteLine("  What the listener hears from each shooter:\n");
        Console.WriteLine("   weapon    range   miss    crack     report      gap   reads back as");
        Console.WriteLine("   ───────────────────────────────────────────────────────────────────");
        bool ok = true;

        foreach (var s in Fight)
        {
            var w = WeaponRegistry.Get(s.WeaponId);
            if (w == null) { Console.WriteLine($"   unknown weapon '{s.WeaponId}'"); return false; }

            Aim(w, s.Position, s.MissBy, out Vector3 dir, out Vector3 impact);
            var heard = ShotResolver.Audition(w, s.Position, dir, impact, "Concrete", Ear, c);

            float range = Vector3.Distance(s.Position, Ear);
            string crack = heard.HasCrack ? $"{heard.CrackDelaySeconds * 1000,6:F1} ms" : "     —   ";
            string gap = heard.HasCrack ? $"{heard.CrackToReportSeconds * 1000,6:F1} ms" : "    —   ";
            string back = heard.HasCrack
                ? $"{Ballistics.DistanceFromCrackToReport(heard.CrackToReportSeconds, w.MuzzleVelocity, c),6:F0} m"
                : "  (no crack)";

            Console.WriteLine($"   {w.Id,-8} {range,5:F0} m {heard.MissDistance,5:F1} m {crack} " +
                              $"{heard.ReportDelaySeconds * 1000,7:F1} ms {gap}  {back}");

            if (heard.HasCrack)
            {
                if (heard.CrackDelaySeconds >= heard.ReportDelaySeconds)
                {
                    Console.WriteLine($"      the crack does not lead the report for the {w.Id}");
                    ok = false;
                }

                // What the gap SHOULD read back as, given that the round did not go straight through
                // the listener's head.
                //
                // The textbook cue is gap = d·(1/c − 1/v), and inverting it is how a player turns a
                // gap into a range. But the crack was made where the round passed, `miss` metres away,
                // and that shock still had to cross those metres at the speed of sound. That hop eats
                // into the gap, so the inversion under-reads by exactly hop/(1/c − 1/v) — a fixed
                // number of metres for a given miss distance and cartridge, independent of range.
                //
                // Checking against THAT rather than against the raw range is the difference between
                // testing the model and testing an approximation of it. It also says something useful:
                // the same 0.5 m miss costs a shotgun 3 m of apparent range and a Glock 7 m, because
                // the deficit scales with how close to the speed of sound the round is.
                float hopMs = heard.MissDistance / c * 1000f;
                float gapMs = heard.CrackToReportSeconds * 1000f;
                float recovered = Ballistics.DistanceFromCrackToReport(
                    heard.CrackToReportSeconds, w.MuzzleVelocity, c);

                float perMetre = 1f / c - 1f / w.MuzzleVelocity;
                float deficit = perMetre > 1e-9f ? (heard.MissDistance / c) / perMetre : 0f;
                float expected = MathF.Max(0f, range - deficit);

                if (MathF.Abs(recovered - expected) > MathF.Max(1.5f, range * 0.03f))
                {
                    Console.WriteLine($"      reads back as {recovered:F1} m; the model says it should " +
                                      $"read {expected:F1} m ({range:F0} m less a {deficit:F1} m deficit " +
                                      $"from the {heard.MissDistance:F1} m miss)");
                    ok = false;
                }
                else if (deficit > range * 0.1f)
                {
                    Console.WriteLine($"      (reads as {recovered:F0} m rather than {range:F0} m: the " +
                                      $"round passed {heard.MissDistance:F1} m wide, and for a Mach " +
                                      $"{w.MuzzleVelocity / c:F2} round that costs {deficit:F0} m of " +
                                      $"apparent range. The gap is a RIFLE cue.)");
                }
            }
        }

        Console.WriteLine();
        var pistol = WeaponRegistry.ServicePistol;
        var akm = WeaponRegistry.Akm;
        ok &= Check($"the .45 is subsonic and makes no crack ({pistol.MuzzleVelocity:F0} m/s vs {c:F0})",
                    !pistol.IsSupersonic(AudioPhysics.SpeedOfSound));
        ok &= Check($"the AKM is supersonic and does (Mach {akm.MuzzleVelocity / c:F2}, " +
                    $"cone {Ballistics.MachConeAngle(akm.MuzzleVelocity, c) * 180 / MathF.PI:F0}°)",
                    akm.IsSupersonic(c));
        ok &= Check("a faster round drags a tighter cone (AR-15 tighter than AKM)",
                    Ballistics.MachConeAngle(WeaponRegistry.Ar15.MuzzleVelocity, c) <
                    Ballistics.MachConeAngle(akm.MuzzleVelocity, c));
        return ok;
    }

    /// <summary>What each shooter's shot bounces off, and when it gets back.</summary>
    private static bool ReportReflections(float c)
    {
        Console.WriteLine("  Which buildings answer each shot, and how late:\n");
        Console.WriteLine("    shooter    direct      1st reflection        2nd            3rd");
        bool any = true;
        Span<Reflection> refl = stackalloc Reflection[MaxReflectionsPerShot * 2];

        foreach (var sh in Fight)
        {
            float direct = Vector3.Distance(sh.Position, Ear);
            int n = Arrivals(sh.Position, c, refl);
            if (n == 0) any = false;

            string cells = "";
            for (int i = 0; i < 3; i++)
                cells += i < n
                    ? $"  {refl[i].DelaySeconds * 1000,5:F0} ms {refl[i].Gain * 100,3:F0}%"
                    : "            —";
            Console.WriteLine($"    {sh.WeaponId,-9} {direct,5:F0} m {cells}");
        }
        Console.WriteLine();

        // Somewhere in the fight there must be a bounce off something genuinely distant — a facade
        // far enough away that its answer is a separate event rather than a colouration. That is the
        // sound of a street having sides, and it is exactly what a reverb decay cannot produce.
        float furthest = 0f;
        int fewest = int.MaxValue;
        foreach (var sh in Fight)
        {
            int fn = Arrivals(sh.Position, c, refl);
            fewest = Math.Min(fewest, fn);
            for (int i = 0; i < fn; i++) furthest = MathF.Max(furthest, refl[i].DelaySeconds);
        }
        Console.WriteLine($"    the least any shot gets is {fewest} discrete arrivals");
        Console.WriteLine($"    the latest building answer in the whole fight is {furthest * 1000:F0} ms " +
                          $"behind its shot ({furthest * AudioPhysics.SpeedOfSound:F0} m of extra path)\n");

        bool ok = Check("EVERY shot has at least one building answering it", any && fewest > 0);
        ok &= Check("something in the street answers more than 100 ms late", furthest > 0.10f);
        return ok;
    }

    // ── 2. The street ───────────────────────────────────────────────────────────────────────────

    private static bool ReportReverberation(List<SteamAudioScene.Box> boxes,
                                            out float canyonRt60Ms, out float openRt60Ms)
    {
        canyonRt60Ms = openRt60Ms = 0f;
        var ctxS = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS)
        {
            Console.WriteLine("  (Steam Audio unavailable — cannot measure the street.)");
            return false;
        }
        try
        {
            canyonRt60Ms = MeasureRt60(ctx, boxes) * 1000f;
            // The same listener on bare ground: one floor plane and nothing else standing up.
            openRt60Ms = MeasureRt60(ctx, new List<SteamAudioScene.Box> { Ground() }) * 1000f;

            Console.WriteLine($"  Reverberation, ray-traced from the geometry:");
            Console.WriteLine($"    in the street canyon : RT60 {canyonRt60Ms,6:F0} ms");
            Console.WriteLine($"    on open ground       : RT60 {openRt60Ms,6:F0} ms");

            float span = AcousticConstants.OutdoorFullWetDecayMs - AcousticConstants.OutdoorDryDecayMs;
            float canyonOpen = Math.Clamp((canyonRt60Ms - AcousticConstants.OutdoorDryDecayMs) / span, 0f, 1f);
            float openOpen = Math.Clamp((openRt60Ms - AcousticConstants.OutdoorDryDecayMs) / span, 0f, 1f);
            Console.WriteLine($"    outdoor reverb bus   : {canyonOpen * 100,5:F0}% open in the street, " +
                              $"{openOpen * 100:F0}% on open ground\n");

            Console.WriteLine("  What distance does to the tone of a shot:\n");
        Console.WriteLine("    range      low     mid    high   (dB, atmospheric + urban excess)");
        foreach (var sh in Fight)
        {
            float d = Vector3.Distance(sh.Position, Ear);
            var b = AudioPhysics.AtmosphericBands(d, Humidity, TemperatureC, PressureMillibars, 1f, false);
            static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-4f, g));
            Console.WriteLine($"    {d,5:F0} m  {Db(b.Low),6:F1}  {Db(b.Mid),6:F1}  {Db(b.High),6:F1}   ({sh.WeaponId})");
        }
        Console.WriteLine();

        bool ok = Check("the canyon reverberates for longer than open ground does",
                            canyonRt60Ms > openRt60Ms * 1.5f);
            ok &= Check("open ground stays dry — the outdoor bus does not open on it",
                        openRt60Ms < AcousticConstants.OutdoorDryDecayMs);
            ok &= Check("the street opens the outdoor reverb bus", canyonOpen > 0.15f);
            return ok;
        }
        finally { Phonon.iplContextRelease(ref ctx); }
    }

    /// <summary>Mid-band RT60 at the listener, in seconds, from Steam Audio's reflection simulation.</summary>
    private static float MeasureRt60(IntPtr ctx, List<SteamAudioScene.Box> boxes)
    {
        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        if (!scene.IsBuilt) return 0f;
        using var sim = new SteamAudioSimulator(ctx, maxSources: 2, enableReflections: true, enableDirect: false);
        if (!sim.IsValid) return 0f;
        sim.SetScene(scene);
        IntPtr src = sim.AcquireSource();
        if (src == IntPtr.Zero) return 0f;
        sim.SetSourceInputs(src, Ear + new Vector3(0f, 0f, 20f));
        sim.SetListener(Ear);

        // The reflection simulation is stochastic and occasionally finds nothing at all. Take the
        // MEDIAN of several runs, the same way the provider does with the live stream — one empty run
        // reporting "no reverberation here" would otherwise read as an open field in the middle of a
        // concrete street.
        const int Runs = 9;
        var readings = new float[Runs];
        for (int i = 0; i < Runs; i++)
        {
            sim.Run();
            readings[i] = sim.GetReverb(src).Rt60Mid;
        }
        Array.Sort(readings);
        return readings[Runs / 2];
    }

    // ── The geometry ────────────────────────────────────────────────────────────────────────────

    private static SteamAudioScene.Box Ground() => new(
        new Vector3(0f, -0.25f, 80f), new Vector3(400f, 0.5f, 400f), Quaternion.Identity, "Concrete");

    /// <summary>The street, its faces, its map and its probes, for other demos that want to stand in it.</summary>
    public static List<SteamAudioScene.Box> StreetBoxes() => BuildStreet();
    public static ReflectingSurface[] StreetSurfaces(List<SteamAudioScene.Box> boxes) { BuildSurfaces(boxes); return _surfaces; }
    public static AcousticMap StreetMap() => OutdoorMap();
    public static int Probes(Vector3 listener, List<SteamAudioScene.Box> boxes, BoundaryProbe[] into) => BoundaryProbes(listener, boxes, into);

    private static List<SteamAudioScene.Box> BuildStreet()
    {
        var boxes = new List<SteamAudioScene.Box> { Ground() };
        var q = Quaternion.Identity;

        // Blocks down both sides. The gaps matter as much as the blocks — a first-order reflection
        // needs a FACE at the point where the path bounces, so an opening in the right place removes
        // a building's answer entirely, and that is the sound of a side street.
        //
        // They are one-sided on purpose. With the cross streets cutting through both rows, the gap sat
        // exactly where a shot from the middle of the road would have bounced, and three of the six
        // shooters got no reflection from anything at all. Real blocks are not symmetrical either.
        float z = -50f;
        int i = 0;
        while (z < 240f)
        {
            // Which row, if either, is open here: +1 opens the right, -1 the left, 0 neither.
            int gapSide = i switch { 2 => +1, 5 => -1, _ => 0 };
            float cz = z + BlockDepth * 0.5f;
            float cx = StreetHalfWidth + BlockWidth * 0.5f;

            if (gapSide != +1)
                boxes.Add(new SteamAudioScene.Box(
                    new Vector3(cx, BlockHeight * 0.5f, cz),
                    new Vector3(BlockWidth, BlockHeight, BlockDepth), q, "Concrete"));
            if (gapSide != -1)
                boxes.Add(new SteamAudioScene.Box(
                    new Vector3(-cx, BlockHeight * 0.5f, cz),
                    new Vector3(BlockWidth, BlockHeight, BlockDepth), q, "Concrete"));

            z += gapSide == 0 ? BlockDepth : SideStreetGap;
            i++;
        }
        return boxes;
    }

    // ── 3. Playing it ───────────────────────────────────────────────────────────────────────────

    private static int Play(List<SteamAudioScene.Box> boxes, float canyonRt60Ms, float c)
    {
        var assets = FindAssetRoot();
        if (assets == null)
        {
            Console.WriteLine("  Cannot find OpenFPS.Client/ASSETS/SOUNDS — run the ingest first:");
            Console.WriteLine("      tools/ingest_audio.py --only weapons");
            return 1;
        }

        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }
        try
        {
            provider.SetAcousticMap(OutdoorMap());
            provider.SetSimulatedReverbDecay(canyonRt60Ms);
            provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            for (int i = 0; i < 30; i++) { provider.Update(); Thread.Sleep(8); }

            // Before anything else: is the street's reverb bus actually receiving audio? "It sounded
            // dry" is a judgement, and the bus RMS is not — it is either zero or it is not. This is
            // how the outdoor send bug was caught: the bus was built, unmuted, and correctly timed at
            // a two-second decay, and measured EXACTLY ZERO, because every sound outdoors was excluded
            // from every reverb bus by a guard that read `regionId != -1`.
            MeasureOutdoorReverb(provider, assets);

            StartAmbience(provider, assets);
            RumbleFloor(provider);
            DemonstrateProximity(provider, assets, c);

            Console.WriteLine("\n  LIVE. HEADPHONES. Concrete Row, about eighteen seconds.\n");

            int voice = -20000;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            foreach (var s in Fight)
            {
                var w = WeaponRegistry.Get(s.WeaponId)!;
                WaitUntil(provider, clock, s.At);
                Console.WriteLine($"    {s.WeaponId,-8} — {s.Where}");

                var state = WeaponMechanics.Fresh(w);
                Span<WeaponEvent> events = stackalloc WeaponEvent[WeaponMechanics.MaxEventsPerCall];

                for (int shot = 0; shot < s.Shots; shot++)
                {
                    float now = (float)clock.Elapsed.TotalSeconds;
                    int n = shot == 0
                        ? WeaponMechanics.PullTrigger(ref state, w, now, events)
                        : FireAgain(ref state, w, now, events);

                    for (int e = 0; e < n; e++)
                        Emit(provider, assets, ref voice, w, s.Position, s.MissBy, events[e], c, shot);

                    // A pump gun has to be worked between shots, and that is a sound in itself.
                    if (w.Action == ActionType.Pump)
                    {
                        WaitUntil(provider, clock, (float)clock.Elapsed.TotalSeconds + w.PumpSeconds * 0.6f);
                        int pn = WeaponMechanics.Pump(ref state, w, (float)clock.Elapsed.TotalSeconds, events);
                        for (int e = 0; e < pn; e++)
                            Emit(provider, assets, ref voice, w, s.Position, s.MissBy, events[e], c);
                    }

                    WaitUntil(provider, clock,
                              (float)clock.Elapsed.TotalSeconds + w.IntervalFor(state.Mode(w)));
                }
                WeaponMechanics.ReleaseTrigger(ref state);
            }

            // Let the last report finish crossing the street and coming back.
            WaitUntil(provider, clock, (float)clock.Elapsed.TotalSeconds + 3.5f);
            Console.WriteLine($"\n  outdoor reverb bus settled at {provider.OutdoorReverbWetDb:F1} dB " +
                              $"(-80 would mean open air)");
            return 0;
        }
        finally { provider.Dispose(); }
    }

    /// <summary>
    /// Fires one shot and watches the outdoor reverb bus, reporting the energy actually on it.
    ///
    /// A number rather than an impression. The bus can be open, correctly timed and completely silent,
    /// and from the listening chair that is indistinguishable from a reverb that is merely too quiet.
    /// </summary>
    private static void MeasureOutdoorReverb(FmodAudioProvider provider, string assets)
    {
        var w = WeaponRegistry.Akm;
        string blast = BlastAsset(assets, w);
        if (blast.Length == 0) return;

        int probe = -40000;
        double sum = 0; int n = 0; float peak = 0f, inputPeak = 0f;
        float db = Loudness.MuzzleBlastDb(w);
        var (pg, pr) = Loudness.Place(db);
        Fire(provider, ref probe, blast, new Vector3(0f, 1.5f, 30f),
             pg, Loudness.AudibleRange(db), 0f, reference: pr);

        // Sample across a second and a half: the shot, and the street answering it.
        for (int i = 0; i < 300; i++)
        {
            provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            provider.Update();
            if (provider.TryMeterReverbBus(AcousticConstants.GlobalRegionId,
                                           out float inPeak, out float outPeak))
            {
                sum += outPeak;
                peak = MathF.Max(peak, outPeak);
                inputPeak = MathF.Max(inputPeak, inPeak);
                n++;
            }
            Thread.Sleep(5);
        }

        float mean = n > 0 ? (float)(sum / n) : 0f;
        Console.WriteLine($"\n  Outdoor reverb bus during one shot:");
        Console.WriteLine($"    into the reverb  peak {inputPeak:F6}   (is the shot being SENT to it?)");
        Console.WriteLine($"    out of it        peak {peak:F6}, mean {mean:F6}   (is it answering?)");
        Console.WriteLine($"    decay {provider.SimulatedReverbDecayMs:F0} ms, wet {provider.OutdoorReverbWetDb:F1} dB");
        Console.WriteLine(inputPeak <= 1e-6f
            ? "  -> nothing is being SENT. The street cannot answer a shot it never receives."
            : peak > 1e-6f
                ? "  -> the street is receiving the shot and answering it."
                : "  -> the shot arrives but nothing comes back: the reverb itself is muted or bypassed.");
    }

    /// <summary>
    /// The same shot heard from the middle of the road and from two metres in front of a building.
    ///
    /// This is the "if I am standing in front of a skyscraper, would I not sense it?" question, and
    /// the answer has two halves that behave completely differently.
    ///
    /// Beyond about five metres a surface answers as an ARRIVAL: the return comes back at 2d/c —
    /// seventy milliseconds at twelve metres — which is late enough to be heard as its own event, and
    /// that is what <see cref="ImageSource"/> renders.
    ///
    /// Closer than that it is not an event at all. At two metres the return is under twelve
    /// milliseconds, inside the window where the ear fuses it with the direct sound, and what you get
    /// is a change of timbre and an unmistakable sense of a solid thing right there. That is
    /// <see cref="BoundaryModel"/>'s job, it has been in the engine since the near-field work, and
    /// this demo was never feeding it — which is a large part of why Concrete Row felt like an open
    /// field with sounds in it rather than a street with sides.
    ///
    /// So: the same shot, twice, from two listening positions. If the second does not feel closer to
    /// something solid than the first, the boundary model needs its level looked at rather than its
    /// geometry.
    /// </summary>
    private static void DemonstrateProximity(FmodAudioProvider provider, string assets, float c)
    {
        var w = WeaponRegistry.Akm;
        string blast = BlastAsset(assets, w);
        if (blast.Length == 0) return;
        float db = Loudness.MuzzleBlastDb(w);
        var (gain, reference) = Loudness.Place(db);

        // Mid-road, and then hard against the left-hand block, whose face is at x = -StreetHalfWidth.
        var open = new Vector3(0f, 1.7f, 0f);
        var hugging = new Vector3(-StreetHalfWidth + 2f, 1.7f, 0f);

        Console.WriteLine("\n  Before the fight — the same shot from two places you could stand:\n");
        int voice = -50000;

        foreach (var (where, listener) in new[]
        {
            ("in the middle of the road, twelve metres from either building", open),
            ("two metres in front of the left-hand block", hugging),
        })
        {
            Console.WriteLine($"    {where}");
            provider.UpdateListener(listener, Quaternion.Identity, Vector3.Zero,
                                    AcousticConstants.GlobalRegionId);

            int np = BoundaryProbes(listener, _boxes, _probeBuffer);
            provider.UpdateBoundaries(_probeBuffer.AsSpan(0, np));
            // Per direction, not the minimum: the minimum is always the GROUND (the ear is 1.7 m
            // above it), which is true everywhere and therefore says nothing about where you are
            // standing. What distinguishes these two positions is the LEFT probe.
            string[] names = { "right", "left", "above", "below", "ahead", "behind" };
            var parts = new System.Text.StringBuilder("      ");
            for (int i = 0; i < np && i < names.Length; i++)
            {
                float d = _probeBuffer[i].Distance;
                parts.Append(d >= BoundaryModel.MaxDistance
                    ? $"{names[i]}: —   "
                    : $"{names[i]}: {d:F1} m ({2f * d / c * 1000f:F0} ms)   ");
            }
            Console.WriteLine(parts.ToString());

            var at = new Vector3(0f, 1.5f, 40f);
            var bands = AudioPhysics.AtmosphericBands(Vector3.Distance(at, listener), Humidity,
                                                      TemperatureC, PressureMillibars, 1f, false);
            Fire(provider, ref voice, blast, at, gain, Loudness.AudibleRange(db), 0f, bands, reference);

            var until = DateTime.UtcNow.AddMilliseconds(2200);
            while (DateTime.UtcNow < until)
            {
                provider.UpdateListener(listener, Quaternion.Identity, Vector3.Zero,
                                        AcousticConstants.GlobalRegionId);
                provider.UpdateBoundaries(_probeBuffer.AsSpan(0, np));
                provider.Update();
                Thread.Sleep(4);
            }
        }

        // Put the listener back where the fight expects it.
        provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
    }

    /// <summary>
    /// The street's own noise — built out of the world rather than laid over it.
    ///
    /// A recorded ambience bed is a photograph of somewhere else. It does not reflect off OUR
    /// buildings, does not get occluded when you step behind one, does not come from anywhere, and
    /// does not change as you move. Play one under a firefight and the two do not share a world:
    /// the gunshots are being filtered, delayed and reflected by the geometry and the bed is not, so
    /// it sits on top like a soundtrack. That is what "separated from the gunshots" is.
    ///
    /// So the environment is made of the same kind of thing as everything else: EMITTERS, placed in
    /// the world, going through the identical pipeline. Step behind a block and the traffic occludes.
    /// Walk up the street and the balance between the sources changes. Each one reflects off the same
    /// facades the shots do, because it is the same code doing it.
    ///
    /// The second, quieter thing this buys is navigational. A player who learns that the main road is
    /// north-east and the extractor fan is on the near corner can orient by them — which in a game
    /// played by ear is not decoration, it is the map. A head-locked bed can never do that, because it
    /// turns with you.
    ///
    /// What is deliberately NOT here: a wash of "city ambience". Anything far enough away to be
    /// undifferentiated belongs in a low diffuse floor (see <see cref="RumbleFloor"/>), not in a
    /// ninety-second loop with birds on it.
    /// </summary>
    private static void StartAmbience(FmodAudioProvider provider, string assets)
    {
        string Abs(string rel)
        {
            foreach (var ext in new[] { ".ogg", ".wav", ".mp3" })
            {
                string path = Path.Combine(assets, rel.Replace('/', Path.DirectorySeparatorChar) + ext);
                if (File.Exists(path)) return path;
            }
            return "";
        }

        int voice = -70000;
        int placed = 0;

        // Each of these is a THING, somewhere. The recordings are being used as the sound of one
        // source rather than as the sound of a whole field, which is what they are actually good for.
        foreach (var (rel, where, pos, db, minDist) in new[]
        {
            // A main road away to the north-east, past the end of the blocks. Low, broad, continuous.
            ("AMBIENCE/stereo/urban_ambiance_distance", "main road, north-east",
             new Vector3(150f, 2f, 240f), 74f, 60f),
            // Traffic the other way, so the street has two ends rather than one.
            ("AMBIENCE/stereo/suburban_distance_ambiance", "traffic, south",
             new Vector3(-40f, 2f, -160f), 70f, 50f),
            // Something small and close and specific: plant on the roof of the second block up.
            ("AMBIENCE/stereo/neighborhood_ambiance", "rooftop plant on the near block",
             new Vector3(27f, 26f, 40f), 58f, 8f),
        })
        {
            string path = Abs(rel);
            if (path.Length == 0) continue;
            var (gain, reference) = Loudness.Place(db);

            void Place(Vector3 at, float vol, float delayMs, (float, float, float)? eq)
            {
                var e = new SpatialEmitter
                {
                    EntityId = voice--,
                    SoundId = path,
                    Type = EmitterType.WorldLocked,
                    Mode = PlaybackMode.LoopOne,
                    Position = at,
                    Volume = vol,
                    Range = Loudness.AudibleRange(db),
                    MinDistance = MathF.Max(reference, minDist),
                    Pitch = 1.0f,
                    DelayMs = delayMs,
                    TargetRegionId = AcousticConstants.GlobalRegionId,
                    EnableReverb = true,
                };
                if (eq is { } q) { e.EqLow = q.Item1; e.EqMid = q.Item2; e.EqHigh = q.Item3; }
                provider.PlaySpatialSound(e);
            }

            Place(pos, gain, 0f, null);

            // The same reflections a gunshot from here would get — and this is the whole point of
            // building ambience out of emitters rather than beds.
            //
            // A delayed copy of an IMPULSE is heard as a separate arrival: an echo. A delayed copy of
            // a SUSTAINED sound is not heard as a second event at all — it sums with the original and
            // the result is a comb filter, notches every 1/d Hz, which the ear reads as colour and as
            // the size of the place. A dog barking from a sixth-floor window does not echo off the
            // block opposite; it acquires that block's colouration. Same geometry, same code, and the
            // difference is entirely in whether the source is a bang or a drone.
            //
            // Fewer than a gunshot gets: these run forever, so each one costs a permanent voice.
            Span<Reflection> refl = stackalloc Reflection[AmbienceReflections];
            int rn = ImageSource.FirstOrder(_surfaces, pos, Ear, AudioPhysics.SpeedOfSound, refl);
            for (int i = 0; i < rn; i++)
            {
                var bands = AudioPhysics.AtmosphericBands(refl[i].PathLength, Humidity, TemperatureC,
                                                          PressureMillibars, 1f, false);
                Place(refl[i].ApparentPosition, gain * refl[i].Gain * ReflectionLevel,
                      refl[i].DelaySeconds * 1000f,
                      (bands.Low, bands.Mid * 0.85f, bands.High * 0.6f));
            }

            Console.WriteLine($"  ambience: {where} ({Vector3.Distance(pos, Ear):F0} m) — " +
                              $"{rn} building reflection(s), which for a continuous source is comb " +
                              $"colouration rather than echo");
            placed++;
        }

        if (placed == 0)
            Console.WriteLine("  ambience: no sources placed — the street will be silent between shots.");
    }

    /// <summary>
    /// The floor: everything too far away to be any one thing.
    ///
    /// A city is never silent, and what fills the gaps is not identifiable sources — it is the SUM of
    /// thousands of them, kilometres out, arriving with no direction left and with everything above
    /// about a kilohertz long since absorbed by the air. That is a rumble, not a hiss, which is why
    /// plain white noise is the wrong instinct: white noise is flat, and this should be steeply tilted
    /// towards the bottom.
    ///
    /// Synthesized rather than recorded, for three reasons that all matter here. It never loops, so it
    /// cannot be caught repeating in a quiet moment. It costs nothing to make it respond — indoors it
    /// should drop and darken, and a recording cannot be asked to do that. And it has no birds in it.
    /// </summary>
    private static void RumbleFloor(FmodAudioProvider provider)
    {
        // Placed in the world rather than on the listener so it still has a weak direction and so the
        // engine's own attenuation applies; far enough out that walking does not change it much.
        provider.PlaySpatialSound(new SpatialEmitter
        {
            EntityId = -79000,
            SoundId = "",
            Type = EmitterType.WorldLocked,
            Mode = PlaybackMode.LoopOne,
            Position = new Vector3(0f, 30f, 600f),
            Volume = Loudness.GainFor(RumbleFloorDb),
            Range = 4000f,
            MinDistance = 600f,
            Pitch = 1.0f,
            TargetRegionId = AcousticConstants.GlobalRegionId,
            EnableReverb = false,
            IsSynth = true,
            SynthWave = SynthWaveType.Noise,
            // Steeply low-passed: this is distance, not hiss. Everything above a few hundred hertz has
            // been taken by kilometres of air long before it reaches the street.
            SynthFilterCutoff = 0.035f,
            SynthFilterResonance = 0.0f,
            // A very slow wander, so it breathes instead of sitting perfectly still. Anything faster
            // reads as a modulation effect rather than as a city.
            SynthLfoRate = 0.03f,
            SynthLfoDepth = 0.25f,
        });
        Console.WriteLine($"  ambience: synthesized distance rumble at {RumbleFloorDb:F0} dB — " +
                          $"never loops, and it is a rumble rather than a hiss");
    }

    /// <summary>How many facades answer each continuous ambience source. Lower than a gunshot's
    /// budget because these never stop, so each one holds a voice for the whole session.</summary>
    private const int AmbienceReflections = 3;

    /// <summary>Source level of the diffuse floor. It should be noticed only when it stops.</summary>
    private const float RumbleFloorDb = 52f;

    /// <summary>A subsequent shot: automatic weapons keep the trigger down, the rest cycle it.</summary>    /// <summary>A subsequent shot: automatic weapons keep the trigger down, the rest cycle it.</summary>
    private static int FireAgain(ref WeaponState state, WeaponDefinition w, float now,
                                 Span<WeaponEvent> events)
    {
        if (state.Mode(w) == FireMode.Auto) return WeaponMechanics.Hold(ref state, w, now, events);
        WeaponMechanics.ReleaseTrigger(ref state);
        return WeaponMechanics.PullTrigger(ref state, w, now, events);
    }

    /// <summary>
    /// Turns one heard action into voices.
    ///
    /// A shot is the interesting case and it becomes THREE voices, not one, because the three sounds
    /// happen in different places and reach the listener at different times: the crack is rendered at
    /// the point on the round's path closest to the listener and arrives first, the report is rendered
    /// at the muzzle and arrives second, and the impact is rendered wherever the round stopped. The
    /// delays are FMOD sample-accurate <c>setDelay</c>, not frame-quantized, which matters when the
    /// gap being conveyed is a few milliseconds.
    /// </summary>
    private static void Emit(FmodAudioProvider provider, string assets, ref int voice,
                             WeaponDefinition w, Vector3 at, Vector3 missBy, WeaponEvent ev, float c,
                             int shotSeed = 0)
    {
        if (ev.Action != WeaponAction.Fire)
        {
            string folder = WeaponMechanics.SoundFolderFor(w, ev.Action);
            string? file = PickFrom(assets, folder);
            if (file != null)
            {
                // A bolt closing is about eighty decibels and a gunshot is a hundred and sixty. Half
                // the difference is not a detail — hearing someone's magazine go in is how you know
                // they are CLOSE, and that only means anything if the sound cannot carry.
                float db = ev.Action == WeaponAction.Casing ? Loudness.CasingDb : Loudness.WeaponHandlingDb;
                var (hg, hr) = Loudness.Place(db);
                Fire(provider, ref voice, file, at, hg, Loudness.AudibleRange(db),
                     ev.DelaySeconds * 1000f, reference: hr);
            }
            return;
        }

        Aim(w, at, missBy, out Vector3 dir, out Vector3 impact);
        var heard = ShotResolver.Audition(w, at, dir, impact, "Concrete", Ear, c);

        // The report, at the muzzle.
        //
        // Three things now decide how it sounds, and none of them did before. It is a COMPOSITE —
        // the recorded transient with the synthesized body and decay underneath, because thirty
        // milliseconds of limited transient on its own is a tick. Its level comes from how loud a
        // muzzle blast actually is rather than from the file being normalised like everything else.
        // And the air has taken some of its top end on the way over, which is what makes a shot two
        // streets away read as big-and-distant instead of small-and-near.
        string blast = BlastAsset(assets, w);
        if (blast.Length > 0)
        {
            float db = Loudness.MuzzleBlastDb(w);
            float distance = Vector3.Distance(at, Ear);
            var bands = AudioPhysics.AtmosphericBands(distance, Humidity, TemperatureC,
                                                      PressureMillibars, 1f, listenerIndoors: false);
            var (gain, reference) = Loudness.Place(db);
            Fire(provider, ref voice, blast, at, gain, Loudness.AudibleRange(db),
                 heard.ReportDelaySeconds * 1000f, bands, reference);

            // And the buildings answering it.
            //
            // Each of these is one bounce off one identified facade, rendered AT THE MIRRORED SOURCE
            // so it arrives from the direction the reflected wavefront is actually travelling, and
            // delayed by however much further it had to go. This is what a reverb decay cannot do:
            // a decay time has no direction, so every surface in the world blurs into one wash and
            // the street stops having sides.
            Span<Reflection> reflections = stackalloc Reflection[MaxReflectionsPerShot * 2];
            int rn = Arrivals(at, c, reflections);
            for (int i = 0; i < rn; i++)
            {
                var r = reflections[i];
                // The reflected path is longer, so the air has taken more of its top end — and the
                // concrete took a little more on the way past. Both matter: a reflection that is
                // merely a quieter copy of the direct sound reads as an echo, not as a building.
                var rb = AudioPhysics.AtmosphericBands(r.PathLength, Humidity, TemperatureC,
                                                       PressureMillibars, 1f, listenerIndoors: false);
                Fire(provider, ref voice, blast, r.ApparentPosition,
                     gain * r.Gain * ReflectionLevel, Loudness.AudibleRange(db),
                     (heard.ReportDelaySeconds + r.DelaySeconds) * 1000f,
                     (rb.Low, rb.Mid * 0.85f, rb.High * 0.6f), reference);
            }
        }

        // The crack, beside the listener, arriving first. Rendered from the closest-approach point so
        // it comes from the side the round went past, not from the shooter.
        if (heard.HasCrack)
        {
            string crackFile = CrackAsset(w, heard.MissDistance, shotSeed);
            if (crackFile.Length > 0)
            {
                var (cg, cr) = Loudness.Place(Loudness.SupersonicCrackDb);
                Fire(provider, ref voice, crackFile, heard.CrackOrigin, cg,
                     Loudness.AudibleRange(Loudness.SupersonicCrackDb),
                     heard.CrackDelaySeconds * 1000f, reference: cr);
            }
        }
    }

    /// <summary>
    /// The crack is synthesized rather than sampled, and has to be: it is an N-wave lasting a fraction
    /// of a millisecond whose width depends on how far the round passed, and no recording in the drop
    /// is of a round going past a microphone. Rendered once per miss distance and cached on disk.
    /// </summary>
    private static string CrackAsset(WeaponDefinition w, float missDistance, int shotSeed)
    {
        var profile = WeaponProfile.From(w);
        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "WEAPONS", "_CRACK");
        Directory.CreateDirectory(dir);
        int bucket = Math.Clamp((int)MathF.Round(missDistance), 1, 25);
        // Varied per shot. Cached by miss distance alone, every round of a burst played the SAME
        // rendered crack, and an identical bright transient repeated four times in three hundred
        // milliseconds stops sounding like gunfire and starts sounding like an insect zapper. Real
        // shock waves differ shot to shot; four variants is enough that the repeat is not a pattern.
        int variant = ((shotSeed % 4) + 4) % 4;
        string path = Path.Combine(dir, $"{profile.Name}_{bucket:D2}_{variant}.wav");
        if (!File.Exists(path))
        {
            var pcm = WeaponSynth.SupersonicCrack(profile, bucket, seed: 2 + variant * 17);
            if (pcm.Length == 0)
            {
                Console.WriteLine($"      (the {w.Id} was scheduled a crack its profile renders as " +
                                  $"silence — {w.MuzzleVelocity:F0} m/s against the profile's)");
                return "";
            }
            File.WriteAllBytes(path, WeaponSynth.ToWav16(pcm));
        }
        return path;
    }

    private static void Fire(FmodAudioProvider provider, ref int voice, string soundId, Vector3 at,
                             float volume, float range, float delayMs,
                             (float Low, float Mid, float High)? bands = null,
                             float reference = 2.0f)
    {
        var eq = bands ?? (1f, 1f, 1f);
        provider.PlaySpatialSound(new SpatialEmitter
        {
            EqLow = eq.Low,
            EqMid = eq.Mid,
            EqHigh = eq.High,
            EntityId = voice--,
            SoundId = soundId,
            Type = EmitterType.WorldLocked,
            Mode = PlaybackMode.Single,
            Position = at,
            Volume = volume,
            Range = range,
            MinDistance = reference,
            Pitch = 1.0f,
            DelayMs = MathF.Max(0f, delayMs),
            TargetRegionId = AcousticConstants.GlobalRegionId,
            EnableReverb = true,
            IsEvent = true,
        });
    }

    private static void WaitUntil(FmodAudioProvider provider, System.Diagnostics.Stopwatch clock, float at)
    {
        // A field rather than a stackalloc: BoundaryProbe carries a material NAME, which makes it a
        // managed type. Reused across frames so the per-frame path still allocates nothing.
        while (clock.Elapsed.TotalSeconds < at)
        {
            if (_boxes.Count > 0)
            {
                int n = BoundaryProbes(Ear, _boxes, _probeBuffer);
                provider.UpdateBoundaries(_probeBuffer.AsSpan(0, n));
            }
            provider.Update();
            Thread.Sleep(3);
        }
    }

    /// <summary>The street's geometry, kept so the boundary probes can be re-cast each frame.</summary>
    private static List<SteamAudioScene.Box> _boxes = new();

    private static readonly BoundaryProbe[] _probeBuffer =
        new BoundaryProbe[BoundaryModel.ProbeDirections.Length];

    // ── Assets ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The asset tree, found by walking up from the binary. AudioLab deliberately does not copy it —
    /// it is 117 MB and this is a test harness — and the provider takes an absolute path when one is
    /// given, so locating the real tree is both cheaper and more honest than shipping a second copy
    /// that can drift from the one the game reads.
    /// </summary>
    private static string? FindAssetRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>How many building faces get their own voice per shot. Each is a real arrival; past a
    /// handful the rest belong in the reverb tail, which is what the tail is for.</summary>
    private const int MaxReflectionsPerShot = 6;

    /// <summary>
    /// How loud the reflections sit under the direct sound.
    ///
    /// The image-source gain already accounts for distance and for what the surface absorbed, so this
    /// is the one judgement left: how much of a real facade's return is specular enough to arrive as a
    /// distinct event rather than smearing into the tail. Half. A reflection you notice AS a
    /// reflection is already too loud — what it should do is tell you the street has a side, without
    /// announcing itself.
    /// </summary>
    private const float ReflectionLevel = 0.5f;

    /// <summary>How loud the environment bed sits. It should be noticed only when it stops.</summary>
    private const float AmbienceLevel = 0.5f;

    // The air the shots are crossing. Cold and dry absorbs most; this is an ordinary temperate day.
    private const float TemperatureC = 14f;
    private const float Humidity = 0.55f;
    private const float PressureMillibars = 1013.25f;

    /// <summary>
    /// The weapon's muzzle blast: its recorded transient with the synthesized body put back under it,
    /// rendered once and cached.
    ///
    /// Rendered in C# rather than baked by the ingest because the synthesized half comes from the
    /// weapon's profile, and a profile that is tuned by ear needs the blast to re-render when it
    /// changes — which a Python asset step could not do without owning a copy of the synthesizer.
    /// </summary>
    private static string BlastAsset(string assets, WeaponDefinition w)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "WEAPONS", "_BLAST");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{w.Id}.wav");
        if (File.Exists(path)) return path;

        var profile = WeaponProfile.From(w);

        float[] recorded = Array.Empty<float>();
        string? take = TakeFrom(assets, w.FiringFolder, w.FiringTakeIndex);
        if (take != null) recorded = WeaponSynth.ReadWav16Mono(File.ReadAllBytes(take));
        if (recorded.Length == 0)
            Console.WriteLine($"      ({w.Id}: no recorded transient — the blast is entirely synthesized)");

        var pcm = WeaponSynth.CompositeBlast(profile, recorded, recordedBlend: w.RecordedBlend);
        if (pcm.Length == 0) return "";
        File.WriteAllBytes(path, WeaponSynth.ToWav16(pcm));
        return path;
    }

    /// <summary>A specific take from a folder, by index. Deterministic, unlike PickFrom: two weapons
    /// sharing a firing folder must not end up on the same recording, and a weapon must not change
    /// character between sessions.</summary>
    private static string? TakeFrom(string assetRoot, string folder, int index)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        string dir = Path.Combine(assetRoot, folder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, "*.wav");
        Array.Sort(files, StringComparer.Ordinal);
        return files.Length == 0 ? null : files[((index % files.Length) + files.Length) % files.Length];
    }

    /// <summary>Every face of every building, as reflecting surfaces. Built once.</summary>
    private static ReflectingSurface[] _surfaces = Array.Empty<ReflectingSurface>();

    /// <summary>Every discrete arrival for one shot: the facades that answer it directly, and the
    /// wall-to-wall flutter behind them.</summary>
    private static int Arrivals(Vector3 from, float c, Span<Reflection> into)
    {
        Span<Reflection> first = stackalloc Reflection[MaxReflectionsPerShot];
        Span<Reflection> second = stackalloc Reflection[MaxReflectionsPerShot];
        int f = ImageSource.FirstOrder(_surfaces, from, Ear, c, first);
        int sec = ImageSource.SecondOrder(_surfaces, from, Ear, c, second);

        int n = 0;
        for (int i = 0; i < f && n < into.Length; i++) into[n++] = first[i];
        for (int i = 0; i < sec && n < into.Length; i++) into[n++] = second[i];
        return n;
    }

    private static void BuildSurfaces(List<SteamAudioScene.Box> boxes)
    {
        _boxes = boxes;
        var all = new List<ReflectingSurface>(boxes.Count * 6);
        Span<ReflectingSurface> six = stackalloc ReflectingSurface[6];
        int id = 1;
        foreach (var b in boxes)
        {
            // 0.25, not the 0.02 a lab measures for a smooth concrete slab.
            //
            // A building front is not a slab. It is windows, reveals, sills, downpipes, signage and
            // texture, and all of that SCATTERS — it sends energy off in directions other than the
            // specular one, where it becomes part of the diffuse tail instead of a distinct arrival.
            // Modelled as polished concrete, every facade answered at 98% and the street came back at
            // you like a hall of mirrors: louder than life and, because the reflections were nearly as
            // loud as the direct sound, exaggerated rather than convincing.
            int n = ImageSource.FacesOfBox(b.Center, b.Size, 0.25f, id, six);
            for (int i = 0; i < n; i++) all.Add(six[i]);
            id += 8;
        }
        _surfaces = all.ToArray();
    }

    /// <summary>
    /// What is immediately around the listener's head, as six head-relative probes.
    ///
    /// This answers a different question from the reflections, and the difference is the whole point.
    /// A reflection is an ARRIVAL — a distinct event you could point at. A surface a metre or two away
    /// produces no such thing: its return comes back in five or ten milliseconds, far inside the
    /// window where the ear fuses it with the direct sound, and what you get instead is a change in
    /// timbre and a strong, immediate sense that something solid is RIGHT THERE. That is how a person
    /// with their eyes shut knows they are standing in front of a wall before touching it, and it is
    /// what makes a space feel occupied rather than sterile.
    ///
    /// <see cref="BoundaryModel"/> renders it — six probes, each a delayed, damped, lateralized tap at
    /// 2d/c. It has been in the engine since the near-field work and this demo never fed it, so every
    /// surface in Concrete Row was silent until it was far enough away to echo.
    /// </summary>
    private static int BoundaryProbes(Vector3 listener, List<SteamAudioScene.Box> boxes,
                                      BoundaryProbe[] into)
    {
        var dirs = BoundaryModel.ProbeDirections;
        int n = Math.Min(dirs.Length, into.Length);
        for (int i = 0; i < n; i++)
        {
            float nearest = float.MaxValue;
            foreach (var b in boxes)
            {
                // Distance from the listener to this box along the probe direction, treating the box
                // as a slab: the standard ray/AABB slab test, minus the parts we do not need.
                Vector3 half = b.Size * 0.5f;
                Vector3 lo = b.Center - half, hi = b.Center + half;
                Vector3 d = dirs[i];
                float tmin = 0f, tmax = float.MaxValue;
                bool hit = true;
                for (int a = 0; a < 3 && hit; a++)
                {
                    float o = a == 0 ? listener.X : a == 1 ? listener.Y : listener.Z;
                    float dd = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
                    float l = a == 0 ? lo.X : a == 1 ? lo.Y : lo.Z;
                    float h = a == 0 ? hi.X : a == 1 ? hi.Y : hi.Z;
                    if (MathF.Abs(dd) < 1e-6f) { if (o < l || o > h) hit = false; }
                    else
                    {
                        float t1 = (l - o) / dd, t2 = (h - o) / dd;
                        if (t1 > t2) (t1, t2) = (t2, t1);
                        tmin = MathF.Max(tmin, t1);
                        tmax = MathF.Min(tmax, t2);
                        if (tmin > tmax) hit = false;
                    }
                }
                if (hit && tmin >= 0f && tmin < nearest) nearest = tmin;
            }
            into[i] = new BoundaryProbe(dirs[i],
                                        nearest == float.MaxValue ? BoundaryModel.MaxDistance : nearest,
                                        "Concrete");
        }
        return n;
    }

    private static readonly Random _pick = new(7);

    /// <summary>One file from a folder, the way AudioBank serves a category: at random, so a magazine
    /// going in twice does not sound like the same recording played twice.</summary>
    private static string? PickFrom(string assetRoot, string folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        string dir = Path.Combine(assetRoot, folder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, "*.wav");
        return files.Length == 0 ? null : files[_pick.Next(files.Length)];
    }

    /// <summary>A map with nothing in it but the outdoors, so the provider builds the global bus that
    /// the simulated RT60 then drives.</summary>
    private static AcousticMap OutdoorMap()
    {
        var map = new AcousticMap(new Vector3(400f, 60f, 400f), new Vector3(-200f, 0f, -100f),
                                  AcousticConstants.DefaultVoxelResolution);
        map.GlobalEnvironmentId = AcousticConstants.GlobalRegionId;
        map.Regions[AcousticConstants.GlobalRegionId] = new RegionComponent
        {
            FriendlyName = "Concrete Row",
            IsIndoor = false,
            Environment = AcousticEnvironmentType.LargeOpen,
            RoomSize = new Vector3(StreetHalfWidth * 2f, BlockHeight, 280f),
            ReverbTimeScale = 1.0f,
            Materials = new[] { 18, 18, 18, 18, 18, 18 },   // concrete on every face
        };
        map.RegionPositions[AcousticConstants.GlobalRegionId] = Vector3.Zero;
        return map;
    }

    private static bool Check(string what, bool held)
    {
        Console.WriteLine($"  [{(held ? "PASS" : "FAIL")}] {what}");
        return held;
    }
}
