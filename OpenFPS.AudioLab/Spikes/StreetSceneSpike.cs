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

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// A minute on a street, with everything the transient channel now carries.
///
/// Four acts, and each one is here because it is the sound of a MECHANISM rather than the sound of a
/// recording. Nothing in this scene was recorded; everything is worked out from what the things are
/// made of and what happened to them, which is the claim the whole engine rests on and the only way
/// to judge it is to stand in the street and listen.
///
///   ONE. A wooden door and a steel one, opened and shut. Same doorway, same swing, and they are
///   plainly different objects. The latch comes BEFORE the thud, because the bolt rides the strike
///   plate as the handle is worked and the leaf has not met anything yet.
///
///   TWO. A window shot out, from thirty metres. The shot, the pane letting go up at the window,
///   then nothing, then the glass arriving at the FOOT of the wall a second and a half later. The gap
///   is sqrt(2h/g) and it is a direct readout of which floor it was — information a sighted game
///   would never think to provide, and this one gets for free by not faking the physics.
///
///   THREE. A truck comes past and stands on it. The engine is synthesised live from its torque
///   curve and gearbox; the tyres let go because the cornering demand exceeds the grip, not because
///   anything played a squeal.
///
///   FOUR. It hits something. The same impact calculation a door, a ball and a dropped crate use.
///
/// Headless it checks the arithmetic and writes each layer out to audition. `--street-live` plays it.
/// </summary>
public static class StreetSceneSpike
{
    /// <summary>Where the listener stands: on the pavement, the road in front of them.</summary>
    private static readonly Vector3 Ear = new(0f, 1.7f, 0f);

    public static int Run(bool live)
    {
        AcousticRegistry.Initialize();
        Console.WriteLine("  A street. Nothing here was recorded.\n");

        bool ok = true;
        ok &= Doors(out var doorLayers);
        ok &= Window(out var glassLayers);
        ok &= Crash(out var crashLayers);

        string outDir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "SCENE");
        Directory.CreateDirectory(outDir);
        foreach (var (name, pcm) in doorLayers.Concat(glassLayers).Concat(crashLayers))
            if (pcm.Length > 0) File.WriteAllBytes(Path.Combine(outDir, name + ".wav"), WeaponSynth.ToWav16(pcm));
        Console.WriteLine($"\n  wrote {doorLayers.Count + glassLayers.Count + crashLayers.Count} layer(s) to {outDir}");

        if (live) return Play();

        Console.WriteLine(ok
            ? "\nRESULT: PASS — run again with --street-live to hear it."
            : "\nRESULT: FAIL — see the unmet conditions above.");
        return ok ? 0 : 1;
    }

    // ── One: the doors ──────────────────────────────────────────────────────────────────────────

    private static bool Doors(out List<(string Name, float[] Pcm)> layers)
    {
        layers = new List<(string, float[])>();
        Console.WriteLine("  ONE — a wooden door and a steel one, same doorway.");

        bool ok = true;
        foreach (var (material, label, thickness, mass) in new[]
                 {
                     ("Wood", "wooden", 0.045f, 22f),
                     ("Metal", "steel", 0.05f, 65f),
                 })
        {
            var props = AcousticRegistry.GetProperties(material);
            float edge = DoorAcoustics.EdgeSpeed(0.9f, MathF.PI / 2f, 0.9f);
            var shutting = DoorAcoustics.Closing(props, Vector3.Zero, Vector3.Zero,
                                                 0.9f, 2.1f, thickness, mass, edge,
                                                 hasSeal: material == "Metal");

            float hz = DoorAcoustics.PanelHz(props, 0.9f, 2.1f, thickness);
            float ring = DoorAcoustics.RingSeconds(props, hz);
            var latch = shutting.Single(s => s.Kind == DoorSoundKind.Latch);
            var impact = shutting.Single(s => s.Kind == DoorSoundKind.Impact);

            Console.WriteLine($"    {label,-7} panel {hz,5:F0} Hz, rings {ring,5:F2} s, "
                            + $"impact {impact.LevelDb,5:F0} dB, latch {(latch.DelaySeconds - impact.DelaySeconds) * 1000f,4:F0} ms early"
                            + (shutting.Any(s => s.Kind == DoorSoundKind.Seal) ? ", sealed" : ""));

            // The latch has to come first, or it reads as a recording played backwards.
            if (latch.DelaySeconds <= impact.DelaySeconds) ok = false;

            foreach (var sound in shutting)
                layers.Add(($"door_{label}_{sound.Kind}".ToLowerInvariant(),
                            TransientSynth.Render(sound.ToTransient(), seed: 3)));
        }
        return ok;
    }

    // ── Two: the window ─────────────────────────────────────────────────────────────────────────

    private static bool Window(out List<(string Name, float[] Pcm)> layers)
    {
        layers = new List<(string, float[])>();
        Console.WriteLine("\n  TWO — a window on the second floor, shot out from thirty metres.");

        const float height = 12f;
        var pane = new GlassPane(new Vector3(0f, height, 30f), new Vector2(1.2f, 1.6f), -Vector3.UnitZ,
                                 GlassType.Tempered, HeightAboveGround: height);
        if (!WeaponRegistry.TryGet("akm", out var weapon)) return false;

        Span<GlassEvent> buffer = stackalloc GlassEvent[64];
        int count = GlassBreak.Resolve(pane, pane.Centre, weapon, seed: 9, buffer);
        var events = new List<GlassEvent>(count);
        for (int i = 0; i < count; i++) events.Add(buffer[i]);

        var sounds = GlassSound.From(events, pane.Type, pane.Size);
        float breakAt = sounds.Min(s => s.DelaySeconds);
        float firstLanding = sounds.Where(s => s.Character == SoundCharacter.Knock)
                                   .DefaultIfEmpty().Min(s => s.DelaySeconds);
        float ideal = GlassBreak.FallSeconds(height);

        Console.WriteLine($"    {count} glass event(s). Break at {breakAt * 1000:F0} ms, "
                        + $"first piece down at {firstLanding:F2} s (free fall from {height:F0} m is {ideal:F2} s).");
        Console.WriteLine($"    That gap read back as a height: {GlassBreak.HeightFromFallDelay(firstLanding - breakAt):F1} m.");

        layers.Add(("shot_akm", WeaponSynth.MuzzleBlast(WeaponProfile.From(weapon), seed: 9)));
        int n = 0;
        foreach (var sound in sounds.Take(12))
            layers.Add(($"glass_{n++}_{sound.Character}".ToLowerInvariant(),
                        TransientSynth.Render(sound, seed: 9 + n)));

        // It has to be heard twice, from two different heights, or the whole point of modelling it is
        // thrown away.
        return firstLanding - breakAt > 1.0f
            && sounds.Any(s => s.Position.Y < pane.Centre.Y - 4f);
    }

    // ── Four: the crash ─────────────────────────────────────────────────────────────────────────

    private static bool Crash(out List<(string Name, float[] Pcm)> layers)
    {
        layers = new List<(string, float[])>();
        Console.WriteLine("\n  FOUR — the truck finds a wall at eleven metres a second.");

        var truck = VehicleProfile.ByName("diesel_truck");
        var sounds = ImpactAcoustics.Between(AcousticRegistry.GetProperties("Metal"),
                                             AcousticRegistry.GetProperties("Concrete"),
                                             new Vector3(0f, 1f, 26f), closingSpeed: 11f,
                                             hitterMassKg: truck.MassKg, struckMassKg: truck.MassKg * 50f,
                                             struckWidth: 4f, struckHeight: 3f, struckThickness: 0.4f);

        float joules = PanelAcoustics.ImpactJoules(truck.MassKg, truck.MassKg * 50f, 11f);
        Console.WriteLine($"    {joules / 1000f:F0} kJ arriving — {sounds[0].LevelDb:F0} dB at a metre, "
                        + $"{(sounds.Any(s => s.Character == SoundCharacter.Ring) ? "and the wall rings" : "and nothing rings")}.");

        int n = 0;
        foreach (var sound in sounds)
            layers.Add(($"crash_{n++}_{sound.Character}".ToLowerInvariant(), TransientSynth.Render(sound, seed: 5)));
        return sounds.Count > 0;
    }

    // ── Live ────────────────────────────────────────────────────────────────────────────────────

    private static int Play()
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }

        try
        {
            provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            Console.WriteLine("\n  LIVE. HEADPHONES.\n");

            Console.WriteLine("  ONE — a wooden door, then a steel one. Two metres to your left.");
            PlayDoor("Wood", 0.045f, 22f, new Vector3(-2f, 1.1f, 0f), provider, 1);
            Wait(provider, 1800);
            PlayDoor("Metal", 0.05f, 65f, new Vector3(-2f, 1.1f, 0f), provider, 2);
            Wait(provider, 2200);

            Console.WriteLine("  TWO — the shot, the pane, then the glass coming down. Count the gap.");
            PlayWindow(provider);
            Wait(provider, 1500);

            Console.WriteLine("  THREE — a truck comes past, stands on it, and lets the tyres go.");
            PlayTruck(provider);

            Console.WriteLine("  FOUR — and finds the wall.");
            PlayCrash(provider);
            Wait(provider, 2500);
            return 0;
        }
        finally { provider.Dispose(); }
    }

    private static void PlayDoor(string material, float thickness, float mass, Vector3 at,
                                 FmodAudioProvider provider, int seed)
    {
        var props = AcousticRegistry.GetProperties(material);
        float edge = DoorAcoustics.EdgeSpeed(0.9f, MathF.PI / 2f, 0.9f);

        var opening = DoorAcoustics.Opening(props, at, at + new Vector3(-0.9f, 0f, 0f),
                                            0.9f, 2.1f, thickness, 0.9f, hingeDryness: 0.35f,
                                            hasSeal: material == "Metal");
        Schedule(provider, opening.Select(s => s.ToTransient()), seed);
        Wait(provider, 1400);
        var closing = DoorAcoustics.Closing(props, at, at, 0.9f, 2.1f, thickness, mass, edge,
                                            hasSeal: material == "Metal");
        Schedule(provider, closing.Select(s => s.ToTransient()), seed);
    }

    private static void PlayWindow(FmodAudioProvider provider)
    {
        const float height = 12f;
        var pane = new GlassPane(new Vector3(0f, height, 30f), new Vector2(1.2f, 1.6f), -Vector3.UnitZ,
                                 GlassType.Tempered, height);
        WeaponRegistry.TryGet("akm", out var weapon);

        // The muzzle blast is at YOUR shoulder — you are the one shooting.
        var shot = new TransientSound
        {
            Character = SoundCharacter.Knock, Position = Ear + new Vector3(0.3f, 0f, 0.4f),
            LevelDb = Loudness.MuzzleBlastDb(weapon), SynthKey = "weapon:" + weapon.Id, DecaySeconds = 0.6f,
        };
        Schedule(provider, new[] { shot }, seed: 9);

        // The round takes a moment to get there. Thirty metres at seven hundred metres a second.
        Wait(provider, 30f / weapon.MuzzleVelocity * 1000f + 40f);

        Span<GlassEvent> buffer = stackalloc GlassEvent[64];
        int count = GlassBreak.Resolve(pane, pane.Centre, weapon, 9, buffer);
        var events = new List<GlassEvent>(count);
        for (int i = 0; i < count; i++) events.Add(buffer[i]);
        Schedule(provider, GlassSound.From(events, pane.Type, pane.Size), seed: 9);

        // Long enough for the glass to finish arriving.
        Wait(provider, 3200);
    }

    private static void PlayCrash(FmodAudioProvider provider)
    {
        var truck = VehicleProfile.ByName("diesel_truck");
        Schedule(provider, ImpactAcoustics.Between(AcousticRegistry.GetProperties("Metal"),
                                                   AcousticRegistry.GetProperties("Concrete"),
                                                   new Vector3(0f, 1f, 26f), 11f,
                                                   truck.MassKg, truck.MassKg * 50f, 4f, 3f, 0.4f), seed: 5);
    }

    /// <summary>
    /// A truck approaching, passing and accelerating away, with the tyres let go on the way out.
    ///
    /// The engine is not a recording being pitched about: it is synthesised from this vehicle's own
    /// torque curve and gearbox, following the speed it is told, so the gear changes and the load are
    /// the engine's doing rather than a mix decision. The squeal is the same — the client works out
    /// how much of the tyres' grip is being asked for and sings when the answer is "more than they
    /// have".
    /// </summary>
    private static void PlayTruck(FmodAudioProvider provider)
    {
        const string preset = "diesel_truck";
        const int Id = -77001;
        var truck = VehicleProfile.ByName(preset);
        var (gain, reference) = Loudness.Place(truck.SourceLevelDb);

        SpatialEmitter Engine(Vector3 at, Vector3 velocity, float speed, float slip) => new()
        {
            EntityId = Id,
            SoundId = "engine:" + preset,
            IsSynth = true,
            EngineKey = preset,
            EngineSpeed = speed,
            EngineRunning = true,
            TyreSlip = slip,
            Type = EmitterType.WorldLocked,
            Mode = PlaybackMode.LoopOne,
            Position = at,
            Velocity = velocity,
            Volume = gain,
            Range = Loudness.AudibleRange(truck.SourceLevelDb),
            MinDistance = MathF.Max(reference, 3f),
            Pitch = 1f,
            TargetRegionId = AcousticConstants.GlobalRegionId,
            EnableReverb = true,
        };

        // Down the road from sixty metres out, past you at eight metres, and away.
        var from = new Vector3(-60f, 0.6f, 8f);
        provider.PlaySpatialSound(Engine(from, Vector3.Zero, 0f, 0f));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        float x = -60f, speed = 9f, slip = 0f, reported = -10f;

        while (x < 70f)
        {
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)(now - last); last = now;

            // Coasting in, then hard on it once past — which is where the tyres give up. The demand
            // is what makes the squeal, not a decision to play one.
            if (x < 0f)
            {
                speed = MathF.Max(6f, speed - 1.5f * dt);
                slip = 0f;
            }
            else
            {
                // Hard enough on it that the tyres cannot deliver what is being asked. The number
                // handed over IS the demand — a fraction of available grip, where 0.78 is where a
                // tyre starts to sing and 1.0 is all it has. Subtracting anything from it, as this
                // did at first, put it below the onset and produced a truck that accelerated in
                // perfect silence.
                const float launch = 7.5f;
                speed = MathF.Min(26f, speed + launch * dt);
                slip = TyreFriction.Demand(launch, 0f, truck.Tyres.PeakGripG);
            }

            x += speed * dt;
            var at = new Vector3(x, 0.6f, 8f);
            provider.UpdateSpatialAttributes(Engine(at, new Vector3(speed, 0f, 0f), speed, slip));

            if (now - reported >= 1.5)
            {
                reported = (float)now;
                Console.WriteLine($"    {Vector3.Distance(at, Ear),5:F0} m   {speed * 3.6f,5:F0} km/h"
                                + (slip > 0.05f ? $"   tyres at {slip * 100f:F0}% over" : ""));
            }

            provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            provider.Update();
            Thread.Sleep(4);
        }
        provider.FadeOutEngine(Id);
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders each sound, registers it under its own id, and plays it at its own moment.
    ///
    /// Exactly what the client does with a <c>WorldAudioEvent</c>, minus the wire — including that
    /// the parts of one event do not all happen at once. Registering under an id rather than playing
    /// a buffer is what puts these through the ordinary spatial path.
    /// </summary>
    private static void Schedule(FmodAudioProvider provider, IEnumerable<TransientSound> sounds, int seed)
    {
        var queue = sounds.OrderBy(s => s.DelaySeconds).ToList();
        if (queue.Count == 0) return;

        int voice = -50000 - Math.Abs(seed * 97);
        float elapsed = 0f;
        foreach (var sound in queue)
        {
            float wait = MathF.Max(0f, sound.DelaySeconds - elapsed);
            if (wait > 0f) { Wait(provider, wait * 1000f); elapsed += wait; }

            var placed = Loudness.Place(sound.LevelDb);
            string id = $"scene:{sound.Character}:{(int)sound.Hz}:{(int)sound.LevelDb}:{seed}";
            var pcm = !string.IsNullOrEmpty(sound.SynthKey) && WeaponRegistry.TryGet(sound.SynthKey["weapon:".Length..], out var w)
                ? WeaponSynth.MuzzleBlast(WeaponProfile.From(w), seed)
                : TransientSynth.Render(sound, seed);

            if (!provider.RegisterSynthesisedSound(id, TransientSynth.ToPcm16(pcm), TransientSynth.SampleRate)) continue;
            provider.PlaySpatialSound(new SpatialEmitter
            {
                EntityId = voice--,
                SoundId = id,
                Type = EmitterType.WorldLocked,
                Mode = PlaybackMode.Single,
                Position = sound.Position,
                Volume = placed.Gain,
                Range = MathF.Min(400f, Loudness.AudibleRange(sound.LevelDb)),
                MinDistance = placed.ReferenceDistance,
                Pitch = 1f,
                TargetRegionId = AcousticConstants.GlobalRegionId,
                EnableReverb = true,
                IsEvent = true,
            });
        }
    }

    private static void Wait(FmodAudioProvider provider, float ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { provider.Update(); Thread.Sleep(4); }
    }
}
