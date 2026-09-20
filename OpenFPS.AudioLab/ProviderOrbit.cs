using System;
using System.Numerics;
using Thread = System.Threading.Thread;
using System.IO;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Drives the REAL FmodAudioProvider end-to-end: plays a synth-noise spatial emitter and orbits
/// its world position around the listener. This exercises the full integrated path — FMOD channel
/// + occlusion/EQ DSPs + the Steam Audio binaural DSP wired into the provider — not a parallel
/// harness. interactive=true runs until Q; false runs `seconds` headless for a crash/init check.
/// </summary>
public static class ProviderOrbit
{
    public static int Run(bool interactive, double seconds = 3.0)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }
        provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);

        var emitter = new SpatialEmitter
        {
            EntityId = 1,
            Type = EmitterType.WorldLocked,
            IsSynth = true,
            SynthWave = SynthWaveType.Noise,
            SynthFrequency = 200f,
            SynthFilterCutoff = 1.0f,
            SynthFilterResonance = 0.0f,
            Volume = 0.6f,
            Position = new Vector3(0, 0, 3),
            Range = 100f,
            MinDistance = 3f
        };
        provider.PlaySpatialSound(emitter);

        bool running = true;
        if (interactive)
        {
            Console.WriteLine("LIVE orbit through FmodAudioProvider + Steam Audio. HEADPHONES. Q to quit.");
            Console.WriteLine("0-8s horizontal (front->right->back->left), 8-16s vertical (front->up->back->down), looping.");
            var kt = new Thread(() => { while (running) if (Console.ReadKey(true).Key == ConsoleKey.Q) running = false; }) { IsBackground = true };
            kt.Start();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (running)
        {
            double t = sw.Elapsed.TotalSeconds;
            if (!interactive && t >= seconds) break;

            double tc = t % 16.0;
            Vector3 pos;
            if (tc < 8.0) { double th = 2 * Math.PI * (tc / 4.0); pos = new Vector3((float)Math.Sin(th) * 3f, 0f, (float)Math.Cos(th) * 3f); }
            else { double ph = 2 * Math.PI * ((tc - 8.0) / 4.0); pos = new Vector3(0f, (float)Math.Sin(ph) * 3f, (float)Math.Cos(ph) * 3f); }

            emitter.Position = pos;
            provider.UpdateSpatialAttributes(emitter);
            provider.Update();
            Thread.Sleep(20);
        }

        provider.StopSound(1);
        provider.Dispose();
        if (!interactive) Console.WriteLine("RESULT: provider orbit ran to completion (see log for 'Steam Audio HRTF binaural enabled').");
        return 0;
    }

    /// <summary>
    /// Stress test: rapidly create and stop many transient spatial voices (footsteps + reflections)
    /// while the FMOD mixer thread runs the Steam Audio DSP callbacks — reproducing the native crash
    /// seen in-game when moving near walls (voice create/release churn racing the mixer callback).
    /// Headless: survives `seconds` and prints a count, or crashes (segfault / heap corruption).
    /// </summary>
    public static int RunChurn(double seconds = 12.0)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }
        provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);

        int id = 100000;
        int created = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rnd = new Random(12345);

        SpatialEmitter MakeVoice(int eid, bool reflection) => new SpatialEmitter
        {
            EntityId = eid,
            Type = EmitterType.WorldLocked,
            IsSynth = true, SynthWave = SynthWaveType.Noise, SynthFrequency = 180f + rnd.Next(400),
            SynthFilterCutoff = 1.0f, Volume = 0.25f,
            Position = new Vector3(rnd.Next(-6, 6), rnd.Next(-2, 2), 1 + rnd.Next(8)),
            Range = 40f, MinDistance = 1f, IsEvent = true, IsReflection = reflection,
        };

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            // Burst of new voices (some flagged as reflections, like the in-game wall bounces).
            for (int k = 0; k < 10; k++) { provider.PlaySpatialSound(MakeVoice(++id, k % 2 == 0)); created++; }
            provider.Update();
            Thread.Sleep(4);
            // Stop a batch of older voices to force release churn against the live mixer callbacks.
            for (int k = 0; k < 10; k++) provider.StopSound(id - 20 - k);
            provider.Update();
            Thread.Sleep(4);
            if (created % 200 == 0) Console.WriteLine($"  churned {created} voices ({sw.Elapsed.TotalSeconds:F1}s)...");
        }

        provider.Dispose();
        Console.WriteLine($"RESULT: SURVIVED — churned {created} transient Steam Audio voices in {seconds:F0}s without crashing.");
        return 0;
    }

    /// <summary>
    /// The same churn, against the PHYSICAL voices — standing machines and aircraft.
    ///
    /// Written because the client died three seconds after the first aircraft voice started on the
    /// city, and rendering the same models offline did not fault: so whatever it is lives in the
    /// plumbing between the model and the mixer, not in the model. That is exactly what this file
    /// exists to exercise, and doing it headlessly is the difference between a bug you can bisect
    /// and a bug somebody has to keep walking into.
    ///
    /// It creates every preset at once, at the distances and ranges the map really uses — an
    /// airliner a kilometre away with a three-kilometre range, a window unit at arm's length — moves
    /// them, and takes them away again.
    /// </summary>
    public static int RunPhysicalChurn(double seconds = 12.0)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }
        provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);

        string[] machines = { "machine:ac_window", "machine:ac_condenser", "machine:mower_push", "machine:mower_riding" };
        string[] aircraft = { "aircraft:airliner", "aircraft:turboprop", "aircraft:piston_single", "aircraft:helicopter" };

        int id = 500000, created = 0;
        var rnd = new Random(4242);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        SpatialEmitter Make(int eid, string key, bool flying)
        {
            float levelDb = flying ? 130f : 70f;
            var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb, flying ? 2f : 0.6f);
            return new SpatialEmitter
            {
                EntityId = eid,
                Type = EmitterType.EntityAttached,
                IsSynth = true,
                PhysicalKey = key,
                EngineRunning = true,
                PowerLever = 0.2f + (float)rnd.NextDouble() * 0.8f,
                Position = flying
                    ? new Vector3(rnd.Next(-400, 400), 60 + rnd.Next(800), rnd.Next(-1000, 1200))
                    : new Vector3(rnd.Next(-30, 30), 1.4f, rnd.Next(-30, 30)),
                Velocity = flying ? new Vector3(0, rnd.Next(-40, 40), 220f) : Vector3.Zero,
                Direction = Vector3.UnitZ,
                Volume = gain,
                MinDistance = reference,
                ExtentMetres = flying ? 2f : 0.6f,
                Range = OpenFPS.Common.Loudness.AudibleRange(levelDb),
                EnableReverb = true,
                TargetRegionId = -1,
            };
        }

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            foreach (string k in machines) { provider.PlaySpatialSound(Make(++id, k, flying: false)); created++; }
            foreach (string k in aircraft) { provider.PlaySpatialSound(Make(++id, k, flying: true)); created++; }
            provider.Update();
            Thread.Sleep(8);
            // Move them, the way the game does every frame — PlaySpatialSound is both the create and
            // the update, and a flying source covers ground fast between two of them.
            for (int back = 7; back >= 0; back--)
            {
                int eid = id - back;
                string key = back < 4 ? aircraft[back] : machines[back - 4];
                var again = Make(eid, key, flying: back < 4);
                again.Position += new Vector3(0, 0, 60f);
                provider.PlaySpatialSound(again);
            }
            provider.Update();
            Thread.Sleep(8);
            // ...and retire the older ones, so creation and release overlap against a live mixer.
            for (int k = 0; k < 8; k++) provider.StopSound(id - 16 - k);
            provider.Update();
            Thread.Sleep(8);
            if (created % 80 == 0) Console.WriteLine($"  churned {created} physical voices ({sw.Elapsed.TotalSeconds:F1}s)...");
        }

        provider.Dispose();
        Console.WriteLine($"RESULT: SURVIVED — churned {created} machine/aircraft voices in {seconds:F0}s without crashing.");
        return 0;
    }

    /// <summary>
    /// ONE QUESTION, ASKED OF FMOD DIRECTLY: when a Channel ENDS ON ITS OWN, is a DSP that was
    /// added to it still attached afterwards?
    ///
    /// Everything about the city crash hangs on the answer and it was being assumed rather than
    /// measured. If FMOD detaches the unit itself, then `removeDSP` failing with
    /// ERR_INVALID_HANDLE on a finished voice is harmless and the pooled DSP is clean. If it does
    /// NOT, then every pooled DSP that outlived its channel is permanently attached to a retired
    /// Channel object, there is no call left that can free it, and re-using it wires a live voice to
    /// a corpse — which is what the mixer is walking when it reads a null at 0x7c.
    ///
    /// No provider, no acoustics, no engine: raw FMOD, one sound, one DSP, and the state of the
    /// graph printed before and after.
    /// </summary>
    public static int RunEndedChannelProbe()
    {
        var res = FMOD.Factory.System_Create(out FMOD.System sys);
        if (res != FMOD.RESULT.OK) { Console.WriteLine($"System_Create: {res}"); return 1; }
        sys.init(64, FMOD.INITFLAGS.NORMAL, IntPtr.Zero);
        sys.getSoftwareFormat(out int rate, out _, out _);

        // A 60 ms sample that will simply finish.
        int len = rate * 60 / 1000;
        var pcm = new byte[len * 2];
        var rnd = new Random(1);
        for (int i = 0; i < len; i++)
        {
            short v = (short)((rnd.NextDouble() * 2 - 1) * 6000);
            pcm[i * 2] = (byte)(v & 0xFF); pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        var info = new FMOD.CREATESOUNDEXINFO
        {
            cbsize = System.Runtime.InteropServices.Marshal.SizeOf<FMOD.CREATESOUNDEXINFO>(),
            length = (uint)pcm.Length, numchannels = 1, defaultfrequency = rate,
            format = FMOD.SOUND_FORMAT.PCM16,
        };
        sys.createSound(pcm, FMOD.MODE.OPENMEMORY | FMOD.MODE.OPENRAW | FMOD.MODE.LOOP_OFF, ref info, out var snd);
        sys.createDSPByType(FMOD.DSP_TYPE.THREE_EQ, out var dsp);

        void Report(string when)
        {
            var r1 = dsp.getNumOutputs(out int outs);
            var r2 = dsp.getNumInputs(out int ins);
            dsp.getActive(out bool active);
            Console.WriteLine($"  {when,-28} inputs={ins} ({r2})  outputs={outs} ({r1})  active={active}");
        }

        sys.playSound(snd, default, false, out var ch);
        ch.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.TAIL, dsp);
        for (int i = 0; i < 4; i++) { sys.update(); Thread.Sleep(10); }
        Report("while playing");

        // Let it FINISH. No stop() — that is the whole point.
        for (int i = 0; i < 60; i++) { sys.update(); Thread.Sleep(10); }
        var playing = ch.isPlaying(out bool isPlaying);
        Console.WriteLine($"  isPlaying -> {isPlaying} ({playing})");
        Report("after it ended on its own");

        var rm = ch.removeDSP(dsp);
        Console.WriteLine($"  removeDSP on the ended channel: {rm}");
        Report("after removeDSP");

        var dc = dsp.disconnectAll(true, true);
        Console.WriteLine($"  disconnectAll:                  {dc}");
        Report("after disconnectAll");

        var rel = dsp.release();
        Console.WriteLine($"  release:                        {rel}");
        Console.WriteLine(rel == FMOD.RESULT.OK
            ? "\nVERDICT: the unit was FREE after its channel ended. A pooled DSP is safe to re-use."
            : "\nVERDICT: the unit is STILL ATTACHED to a channel that no longer exists, and nothing "
            + "can detach it.\n         Pooling it re-wires a live voice to a corpse.");

        snd.release();
        sys.close(); sys.release();
        return 0;
    }

    /// <summary>
    /// Voices that END ON THEIR OWN, which is the one thing none of the other harnesses did.
    ///
    /// Every churn in this file stops its voices by calling StopSound, and StopSound stops the
    /// channel itself. That is the SAFE path, and it is why 42,000 voices never reproduced a crash
    /// the client hit in sixteen seconds.
    ///
    /// The game's commonest voice is a one-shot — a footstep, a door, a wall reflection — which
    /// nobody stops, because it simply finishes. FMOD then retires and RECYCLES that channel, so by
    /// the time the reaper sees `isPlaying == false` the channel handle is already stale and every
    /// pooled DSP hung on it (binaural, three-band EQ, diffraction) can no longer be removed from
    /// it. The client said so once the counter existed: nineteen in one five-second report.
    ///
    /// So this plays short PCM one-shots, at a rate, and NEVER stops one. The only way a voice
    /// leaves here is by ending — which is the path that was broken.
    /// </summary>
    public static int RunReapChurn(double seconds = 20.0)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }
        provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);

        // A 90 ms burst of noise with a short decay: a footstep's shape, and short enough that a
        // voice is born and reaped several times a second.
        const int Rate = 44100, Len = Rate * 90 / 1000;
        var pcm = new byte[Len * 2];
        var noise = new Random(7);
        for (int i = 0; i < Len; i++)
        {
            float env = 1f - (float)i / Len;
            short v = (short)((noise.NextDouble() * 2 - 1) * env * env * 12000);
            pcm[i * 2] = (byte)(v & 0xFF); pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        for (int k = 0; k < 8; k++) provider.RegisterSynthesisedSound($"reap_{k}", pcm, Rate);

        int id = 700000, created = 0;
        var rnd = new Random(4242);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            for (int k = 0; k < 12; k++)
            {
                provider.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = ++id,
                    Type = EmitterType.WorldLocked,
                    SoundId = $"reap_{rnd.Next(8)}",
                    Volume = 0.2f,
                    Position = new Vector3(rnd.Next(-9, 9), rnd.Next(-2, 3), 1 + rnd.Next(12)),
                    Range = 60f, MinDistance = 1f, IsEvent = true,
                    // Half of them as reflections, because a wall bounce is a one-shot too and
                    // carries the diffraction DSP the direct sound does not.
                    IsReflection = k % 2 == 0,
                });
                created++;
            }
            provider.Update();
            Thread.Sleep(5);
            // The listener moves, so region routing and the reverb sends churn underneath as well.
            provider.UpdateListener(new Vector3(MathF.Sin((float)sw.Elapsed.TotalSeconds) * 5f, 0,
                                                MathF.Cos((float)sw.Elapsed.TotalSeconds) * 5f),
                                    Quaternion.Identity, Vector3.Zero, -1);
            provider.Update();
            Thread.Sleep(5);
            if (created % 600 == 0) Console.WriteLine($"  {created} one-shots born and reaped ({sw.Elapsed.TotalSeconds:F1}s)...");
        }

        provider.Dispose();
        Console.WriteLine($"RESULT: SURVIVED — {created} one-shots ended on their own and were reaped in {seconds:F0}s.");
        return 0;
    }

    /// <summary>
    /// The whole scene at once: engines, machines, aircraft and region reverb buses, created and
    /// released against a live mixer while the listener walks.
    ///
    /// Written after two client crashes that both landed on the SAME instruction — the line
    /// immediately after "voice started", once for an aircraft and once for a diesel. A voice being
    /// born in a busy scene is the only thing the two have in common, and neither the physical churn
    /// (no engines, no buses) nor the reverb churn (no voices being made) reproduces it. This has
    /// all of it running together, which is what the city is.
    /// </summary>
    public static int RunSceneChurn(double seconds = 30.0)
    {
        // EVERYTHING THE GAME HAS ON THE MASTER BUS, which the first version of this did not.
        //
        // The client crashes on FMOD's mixer thread and this harness would not reproduce it at
        // 28,000 voices, so what it was missing mattered more than what it had. Two units run in
        // every real session and in none of these runs: the CAPTURE TAP, which writes a WAV from
        // inside the mixer callback (Cody's sessions are all `run-gtk-client.sh capture`), and the
        // BOUNDARY unit, which is on the master bus and processes every block of the whole mix
        // against a delay line driven by the near-field probes.
        //
        // Both are now on, and the probes are moved every tick the way walking moves them.
        string cap = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE")
                     ?? Path.Combine(Path.GetTempPath(), "openfps-churn-capture.wav");
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", cap);

        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }
        Console.WriteLine($"  master tap capturing to {cap}");

        // A map with rooms in it, so sends make buses the way walking a city does.
        const int regions = 120;
        var map = new OpenFPS.Common.AcousticMap(new Vector3(2000, 200, 2000), new Vector3(-1000, 0, -1000), 1f);
        for (int i = 0; i < regions; i++)
        {
            map.Regions[9000 + i] = new OpenFPS.Common.Components.RegionComponent
            {
                FriendlyName = $"Room {i}", IsIndoor = true,
                RoomSize = new Vector3(6f, 3f, 8f),
                Materials = new[] { 18, 18, 18, 18, 18, 18 },
            };
            map.RegionPositions[9000 + i] = new Vector3((i % 12) * 60f - 360f, 1.6f, (i / 12) * 60f - 300f);
        }
        provider.SetAcousticMap(map);

        string[] engines = { "engine:diesel_i4", "engine:i4_economy", "engine:v8_muscle", "engine:school_bus",
                             "engine:diesel_truck", "engine:police_v8", "engine:i6", "engine:vtwin" };
        string[] physical = { "machine:ac_window", "machine:ac_condenser", "machine:mower_push",
                              "aircraft:airliner", "aircraft:turboprop", "aircraft:piston_single", "aircraft:helicopter" };

        int id = 700000, created = 0;
        var rnd = new Random(99);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        SpatialEmitter Make(int eid, string key, int region)
        {
            bool engine = key.StartsWith("engine:");
            bool flying = key.StartsWith("aircraft:");
            float levelDb = engine ? 116f : flying ? 130f : 70f;
            var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb, engine ? 3.5f : flying ? 2f : 0.6f);
            return new SpatialEmitter
            {
                EntityId = eid,
                Type = EmitterType.EntityAttached,
                IsSynth = true,
                EngineKey = engine ? key[7..] : "",
                PhysicalKey = engine ? "" : key,
                EngineRunning = true,
                EngineSpeed = engine ? 6f + (float)rnd.NextDouble() * 20f : 0f,
                PowerLever = 0.3f + (float)rnd.NextDouble() * 0.7f,
                Position = flying
                    ? new Vector3(rnd.Next(-400, 400), 80 + rnd.Next(700), rnd.Next(-800, 900))
                    : new Vector3(rnd.Next(-300, 300), 0.5f, rnd.Next(-300, 300)),
                Velocity = flying ? new Vector3(0, 10f, 200f) : new Vector3(0, 0, engine ? 14f : 0f),
                Direction = Vector3.UnitZ,
                Volume = gain, MinDistance = reference,
                ExtentMetres = engine ? 3.5f : flying ? 2f : 0.6f,
                Range = OpenFPS.Common.Loudness.AudibleRange(levelDb),
                EnableReverb = true,
                TargetRegionId = 9000 + region,
            };
        }

        int step = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            step++;
            // The listener walks, which is what changes which rooms and which sources are in play.
            float t = (float)sw.Elapsed.TotalSeconds;
            provider.UpdateListener(new Vector3(MathF.Sin(t * 0.3f) * 250f, 1.8f, t * 12f - 150f),
                                    Quaternion.Identity, new Vector3(0, 0, 1.4f), 9000 + (step % regions));

            // The near-field probes, moved every tick — this is what drives the boundary DSP's delay
            // line, and a delay that glides is the part with arithmetic in it.
            var probes = new BoundaryProbe[6];
            for (int b = 0; b < probes.Length; b++)
            {
                float ang = t * 0.7f + b * 1.05f;
                probes[b] = new BoundaryProbe(
                    Vector3.Normalize(new Vector3(MathF.Cos(ang), 0.1f * MathF.Sin(ang * 3f), MathF.Sin(ang))),
                    0.4f + 9.0f * (0.5f + 0.5f * MathF.Sin(t * 0.9f + b)),
                    (b % 2 == 0) ? "Concrete" : "Glass");
            }
            provider.UpdateBoundaries(probes);

            foreach (string k in engines) { provider.PlaySpatialSound(Make(++id, k, rnd.Next(regions))); created++; }
            foreach (string k in physical) { provider.PlaySpatialSound(Make(++id, k, rnd.Next(regions))); created++; }
            provider.Update();
            Thread.Sleep(6);
            // Move everything that is still alive, then retire the oldest — creation, movement and
            // release all overlapping, which is the state the client is in while you walk.
            for (int back = 0; back < 15; back++)
            {
                int eid = id - back;
                string key = back < physical.Length ? physical[back] : engines[(back - physical.Length) % engines.Length];
                var again = Make(eid, key, rnd.Next(regions));
                again.Position += new Vector3(0, 0, 25f);
                provider.PlaySpatialSound(again);
            }
            provider.Update();
            Thread.Sleep(6);
            for (int k = 0; k < 15; k++) provider.StopSound(id - 30 - k);
            provider.Update();
            Thread.Sleep(6);
            if (step % 20 == 0) Console.WriteLine($"  {created} voices made, {sw.Elapsed.TotalSeconds:F0}s...");
        }

        provider.Dispose();
        Console.WriteLine($"RESULT: SURVIVED — {created} engine/machine/aircraft voices against {regions} rooms in {seconds:F0}s.");
        return 0;
    }

    /// <summary>
    /// Reverb SENDS on voices that END ON THEIR OWN, re-routed every update.
    ///
    /// --reap-churn has one-shots ending on their own, but it hands the provider region -1 and no
    /// acoustic map, so UpdateReverbRouting returns at its first line and no voice in it has ever
    /// had a send. The client's voices all do: two SEND connections from the channel's own FADER
    /// into the room buses, torn down and re-made whenever the listener changes room — which /tp
    /// does for every voice in one frame. A send is an FMOD-owned connection whose input end is a
    /// DSP FMOD frees when the channel finishes, so this is the one place the game hands FMOD a
    /// handle it no longer owns.
    ///
    /// Rooms, one-shots into them, and the listener's region flipped every update.
    /// </summary>
    public static int RunSendChurn(double seconds = 30.0, bool flip = true, bool ownRoom = false)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }

        const int regions = 12;   // under the bus cap, so every room really gets a bus
        var map = new OpenFPS.Common.AcousticMap(new Vector3(400, 50, 400), new Vector3(-200, 0, -200), 1f);
        for (int i = 0; i < regions; i++)
        {
            map.Regions[9000 + i] = new OpenFPS.Common.Components.RegionComponent
            {
                FriendlyName = $"Room {i}", IsIndoor = true,
                RoomSize = new Vector3(6f, 3f, 8f),
                Materials = new[] { 18, 18, 18, 18, 18, 18 },
            };
            map.RegionPositions[9000 + i] = new Vector3((i % 4) * 30f - 45f, 1.6f, (i / 4) * 30f - 30f);
        }
        if (ownRoom)
        {
            // The city names the outdoors as a region of its own, so the GLOBAL id has a bus — and
            // that is what a stale region id resolves to. Without this entry TryGetReverbInput(-1)
            // is false and the wrong-bus disconnect is skipped, which is why the first run of this
            // survived. See the note on DropSend.
            map.Regions[AcousticConstants.GlobalRegionId] = new OpenFPS.Common.Components.RegionComponent
            {
                FriendlyName = "Outside", IsIndoor = false,
                RoomSize = new Vector3(200f, 50f, 200f),
                Materials = new[] { 18, 18, 18, 18, 18, 18 },
            };
            map.RegionPositions[AcousticConstants.GlobalRegionId] = new Vector3(0, 1.6f, -80);
        }
        provider.SetAcousticMap(map);

        const int Rate = 44100;
        var noise = new Random(7);
        // Several lengths, so voices end at every phase of the update loop.
        int[] lengthsMs = { 30, 45, 60, 90, 120, 150 };
        for (int k = 0; k < lengthsMs.Length; k++)
        {
            int len = Rate * lengthsMs[k] / 1000;
            var pcm = new byte[len * 2];
            for (int i = 0; i < len; i++)
            {
                float env = 1f - (float)i / len;
                short v = (short)((noise.NextDouble() * 2 - 1) * env * env * 12000);
                pcm[i * 2] = (byte)(v & 0xFF); pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            provider.RegisterSynthesisedSound($"send_{k}", pcm, Rate);
        }

        int id = 800000, created = 0, step = 0;
        var rnd = new Random(4242);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            step++;
            // ownRoom: the city's shape. The listener is sometimes OUTDOORS (region -1, which has a bus
            // of its own in a city), and most one-shots are in the listener's OWN room — footsteps,
            // for one — so a voice's cross-send and source-send point at the same bus, and the
            // listener then walks out. Without it, the listener is always in some room and every
            // source is in a random one, which is what the first run of this survived.
            int listenerRegion;
            if (ownRoom) listenerRegion = (step % 16 < 4) ? -1 : 9000 + rnd.Next(regions);
            else listenerRegion = 9000 + (flip ? rnd.Next(regions) : 0);
            var listenerPos = listenerRegion == -1 ? new Vector3(0, 1.6f, -80) : map.RegionPositions[listenerRegion];
            provider.UpdateListener(listenerPos, Quaternion.Identity, Vector3.Zero, listenerRegion);
            for (int k = 0; k < 10; k++)
            {
                int room = rnd.Next(regions);
                int target = 9000 + room;
                var pos = map.RegionPositions[target];
                if (ownRoom && rnd.Next(10) < 6) { target = listenerRegion; pos = listenerPos; }
                provider.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = ++id,
                    Type = EmitterType.WorldLocked,
                    SoundId = $"send_{rnd.Next(lengthsMs.Length)}",
                    Volume = 0.2f,
                    Position = pos + new Vector3(rnd.Next(-2, 3), 0, rnd.Next(-3, 4)),
                    Range = 60f, MinDistance = 1f, IsEvent = true,
                    EnableReverb = true,
                    TargetRegionId = target,
                });
                created++;
            }
            if (ownRoom && step == 200) Console.WriteLine("  outdoor bus: " + provider.DescribeReverbChain(-1));
            provider.Update();
            Thread.Sleep(4);
            if (step % 250 == 0) Console.WriteLine($"  {created} one-shots with sends, {sw.Elapsed.TotalSeconds:F1}s...");
        }

        provider.Dispose();
        Console.WriteLine($"RESULT: SURVIVED — {created} one-shots with reverb sends, listener region {(flip ? "flipped" : "fixed")} every update{(ownRoom ? ", sources in the listener's own room and an outdoor bus" : "")}, in {seconds:F0}s.");
        return 0;
    }

    /// <summary>
    /// The same thing with no provider in the way: FMOD alone, a reverb unit on a bus, short
    /// one-shots each with a SEND from its fader into the unit, and the game thread doing what the
    /// client does — fetch the fader, disconnect the send it stored last time, make a new one — as
    /// fast as it can, so that the window between "the channel was alive when I asked" and "the
    /// mixer applied what I queued" is hit as often as possible.
    ///
    /// mode: "client" = disconnectFrom the stored connection (what the game does);
    ///       "forget" = never disconnect, just drop the handle and add a new send;
    ///       "stop"   = stop the channel ourselves before its sound ends (the safe path).
    /// </summary>
    public static int RunSendWindowProbe(double seconds = 20.0, string mode = "client")
    {
        var res = FMOD.Factory.System_Create(out FMOD.System sys);
        if (res != FMOD.RESULT.OK) { Console.WriteLine($"System_Create: {res}"); return 1; }
        sys.setDSPBufferSize(1024, 8);
        sys.init(512, FMOD.INITFLAGS.NORMAL, IntPtr.Zero);
        sys.getSoftwareFormat(out int rate, out _, out _);

        var sounds = new FMOD.Sound[6];
        int[] lengthsMs = { 20, 30, 45, 60, 90, 120 };
        var rnd = new Random(1);
        for (int k = 0; k < sounds.Length; k++)
        {
            int len = rate * lengthsMs[k] / 1000;
            var pcm = new byte[len * 2];
            for (int i = 0; i < len; i++)
            {
                short v = (short)((rnd.NextDouble() * 2 - 1) * 6000);
                pcm[i * 2] = (byte)(v & 0xFF); pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            var info = new FMOD.CREATESOUNDEXINFO
            {
                cbsize = System.Runtime.InteropServices.Marshal.SizeOf<FMOD.CREATESOUNDEXINFO>(),
                length = (uint)pcm.Length, numchannels = 1, defaultfrequency = rate,
                format = FMOD.SOUND_FORMAT.PCM16,
            };
            sys.createSound(pcm, FMOD.MODE.OPENMEMORY | FMOD.MODE.OPENRAW | FMOD.MODE.LOOP_OFF, ref info, out sounds[k]);
        }

        // Three room buses, each a group with a reverb unit at its tail — the client's shape.
        sys.getMasterChannelGroup(out var master);
        var buses = new FMOD.ChannelGroup[3]; var reverbs = new FMOD.DSP[3];
        for (int b = 0; b < 3; b++)
        {
            sys.createChannelGroup($"bus{b}", out buses[b]);
            master.addGroup(buses[b]);
            sys.createDSPByType(FMOD.DSP_TYPE.SFXREVERB, out reverbs[b]);
            buses[b].addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.TAIL, reverbs[b]);
        }
        // A pool of EQ units on each voice's head, the way the client pools its three-band EQ.
        var pool = new Stack<FMOD.DSP>();
        for (int i = 0; i < 128; i++) { sys.createDSPByType(FMOD.DSP_TYPE.THREE_EQ, out var d); pool.Push(d); }

        var chans = new FMOD.Channel[96];
        var conns = new FMOD.DSPConnection[96];
        var connBus = new int[96];
        var eqs = new FMOD.DSP[96];
        int born = 0, reroutes = 0, staleGetDsp = 0, disconnectErrs = 0, addErrs = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            for (int i = 0; i < chans.Length; i++)
            {
                bool alive = chans[i].hasHandle() && chans[i].isPlaying(out bool p) == FMOD.RESULT.OK && p;
                if (!alive)
                {
                    // Reap: the pooled unit comes back (FMOD detached it itself), the send died with the fader.
                    if (eqs[i].hasHandle()) { chans[i].removeDSP(eqs[i]); pool.Push(eqs[i]); eqs[i] = default; }
                    conns[i] = default;
                    sys.playSound(sounds[rnd.Next(sounds.Length)], buses[rnd.Next(3)], true, out chans[i]);
                    if (pool.Count > 0) { eqs[i] = pool.Pop(); eqs[i].setBypass(false); chans[i].addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.HEAD, eqs[i]); }
                    chans[i].getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.FADER, out var fader0);
                    connBus[i] = rnd.Next(3);
                    reverbs[connBus[i]].addInput(fader0, out conns[i], FMOD.DSPCONNECTION_TYPE.SEND);
                    conns[i].setMix(0.3f);
                    chans[i].setPaused(false);
                    born++;
                    continue;
                }
                if (mode == "stop" && rnd.Next(4) == 0) { chans[i].stop(); continue; }
                // Re-route, the way UpdateReverbRouting does when the listener changes room.
                var gr = chans[i].getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.FADER, out var fader);
                if (gr != FMOD.RESULT.OK || !fader.hasHandle()) { staleGetDsp++; continue; }
                if (conns[i].hasHandle())
                {
                    if (mode == "client")
                    {
                        var dr = reverbs[connBus[i]].disconnectFrom(fader, conns[i]);
                        if (dr != FMOD.RESULT.OK) disconnectErrs++;
                    }
                    conns[i] = default;
                }
                connBus[i] = rnd.Next(3);
                var ar = reverbs[connBus[i]].addInput(fader, out conns[i], FMOD.DSPCONNECTION_TYPE.SEND);
                if (ar != FMOD.RESULT.OK) { addErrs++; conns[i] = default; }
                else conns[i].setMix((float)rnd.NextDouble() * 0.5f);
                reroutes++;
            }
            sys.update();
            if (born % 2000 < chans.Length && born > 0 && reroutes % 50000 < chans.Length)
                Console.WriteLine($"  born={born} reroutes={reroutes} staleGetDsp={staleGetDsp} disconnectErr={disconnectErrs} addErr={addErrs} {sw.Elapsed.TotalSeconds:F1}s");
        }
        Console.WriteLine($"RESULT: SURVIVED mode={mode} — born={born} reroutes={reroutes} staleGetDsp={staleGetDsp} disconnectErr={disconnectErrs} addErr={addErrs} in {seconds:F0}s");
        foreach (var c in chans) if (c.hasHandle()) c.stop();
        sys.update();
        sys.close(); sys.release();
        return 0;
    }

    /// <summary>
    /// ONE foreign disconnect, on purpose, and what it does to FMOD's bookkeeping.
    ///
    /// A send from a voice's fader into reverb A is disconnected by asking reverb B to do it —
    /// `B.disconnectFrom(fader, connectionIntoA)` — which is what DropSend does when the bus it is
    /// handed is not the bus the connection was made into. FMOD's queued disconnect checks only that
    /// the connection's source is the fader, then unlinks the node from whichever list it is in (A's)
    /// and decrements the count on the unit it was told (B's). A is then one short in its list and
    /// not in its count; the next honest disconnect on A takes the count to 1 with an empty list, and
    /// the cache recompute at the end of DSPI::disconnectFrom dereferences the list head's null owner
    /// at offset 0x7c. That is the crash in every dump.
    /// </summary>
    public static int RunForeignDisconnectProbe()
    {
        var res = FMOD.Factory.System_Create(out FMOD.System sys);
        if (res != FMOD.RESULT.OK) { Console.WriteLine($"System_Create: {res}"); return 1; }
        sys.init(64, FMOD.INITFLAGS.NORMAL, IntPtr.Zero);
        sys.getSoftwareFormat(out int rate, out _, out _);
        int len = rate; var pcm = new byte[len * 2]; var rnd = new Random(1);
        for (int i = 0; i < len; i++) { short v = (short)((rnd.NextDouble() * 2 - 1) * 3000); pcm[i * 2] = (byte)(v & 0xFF); pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF); }
        var info = new FMOD.CREATESOUNDEXINFO
        {
            cbsize = System.Runtime.InteropServices.Marshal.SizeOf<FMOD.CREATESOUNDEXINFO>(),
            length = (uint)pcm.Length, numchannels = 1, defaultfrequency = rate, format = FMOD.SOUND_FORMAT.PCM16,
        };
        sys.createSound(pcm, FMOD.MODE.OPENMEMORY | FMOD.MODE.OPENRAW | FMOD.MODE.LOOP_NORMAL, ref info, out var snd);
        sys.getMasterChannelGroup(out var master);
        sys.createChannelGroup("busA", out var busA); master.addGroup(busA);
        sys.createChannelGroup("busB", out var busB); master.addGroup(busB);
        sys.createDSPByType(FMOD.DSP_TYPE.SFXREVERB, out var reverbA); busA.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.TAIL, reverbA);
        sys.createDSPByType(FMOD.DSP_TYPE.SFXREVERB, out var reverbB); busB.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.TAIL, reverbB);
        void Tick() { for (int i = 0; i < 5; i++) { sys.update(); Thread.Sleep(10); } }
        void Report(string when)
        {
            reverbA.getNumInputs(out int a); reverbB.getNumInputs(out int b);
            Console.WriteLine($"  {when,-58} reverbA.inputs={a}  reverbB.inputs={b}");
        }
        sys.playSound(snd, master, false, out var ch1);
        sys.playSound(snd, master, false, out var ch2);
        sys.playSound(snd, master, false, out var ch3);
        ch1.getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.FADER, out var fader1);
        ch2.getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.FADER, out var fader2);
        ch3.getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.FADER, out var fader3);
        Tick();
        var r1 = reverbA.addInput(fader1, out var c1, FMOD.DSPCONNECTION_TYPE.SEND);
        var r2 = reverbA.addInput(fader2, out var c2, FMOD.DSPCONNECTION_TYPE.SEND);
        // B must have a send of its own: DSPI::disconnectFrom returns early when the unit it is
        // called on has no inputs at all, which is the one case the first version of this tested.
        var r0 = reverbB.addInput(fader3, out var cB, FMOD.DSPCONNECTION_TYPE.SEND);
        Tick(); Report($"two sends into A, one into B ({r1},{r2},{r0})");
        // What DropSend's guard relies on: one update after addInput, the connection knows its owner.
        var go = c1.getOutput(out var c1Owner);
        Console.WriteLine($"  c1.getOutput -> {go}: owner is reverbA = {c1Owner.handle == reverbA.handle}, reverbB = {c1Owner.handle == reverbB.handle}");
        var rf = reverbB.disconnectFrom(fader1, c1);
        Tick(); Report($"B.disconnectFrom(fader1, c1) -> {rf}");
        var rh = reverbA.disconnectFrom(fader2, c2);
        Tick(); Report($"A.disconnectFrom(fader2, c2) -> {rh}   (honest)");
        Console.WriteLine("  still alive after the honest disconnect; adding and removing one more send on A...");
        var r3 = reverbA.addInput(fader2, out var c3, FMOD.DSPCONNECTION_TYPE.SEND);
        Tick(); Report($"A.addInput(fader2) -> {r3}");
        var r4 = reverbA.disconnectFrom(fader2, c3);
        Tick(); Report($"A.disconnectFrom(fader2, c3) -> {r4}");
        Console.WriteLine("RESULT: SURVIVED — FMOD tolerated a foreign disconnect (or refused it). Read the counts above.");
        ch1.stop(); ch2.stop(); ch3.stop(); Tick();
        sys.close(); sys.release();
        return 0;
    }

    /// <summary>
    /// Does anything FMOD does to a channel leave a reverb unit's input COUNT above its input LIST?
    ///
    /// The crash state in every dump is a reverb unit with numInputs == 1 and an empty input list.
    /// An honest disconnect keeps the two together, so something earlier took a send OUT of the list
    /// without touching the count. Each scenario here tears a sending channel down one way, then
    /// springs the trip-wire: one honest send added to and removed from the same unit. If the count
    /// had drifted, that removal is the crash.
    ///
    /// scenario: 1 = the channel ends on its own; 2 = stop(); 3 = removeDSP of a head unit while
    /// the send exists; 4 = the channel goes VIRTUAL (software channels exhausted) and comes back;
    /// 5 = the channel goes virtual and is stopped while virtual; 6 = channel group release.
    /// </summary>
    public static int RunSendDriftProbe(int scenario)
    {
        var res = FMOD.Factory.System_Create(out FMOD.System sys);
        if (res != FMOD.RESULT.OK) { Console.WriteLine($"System_Create: {res}"); return 1; }
        if (scenario == 4 || scenario == 5) sys.setSoftwareChannels(4);
        sys.init(64, FMOD.INITFLAGS.NORMAL, IntPtr.Zero);
        sys.getSoftwareFormat(out int rate, out _, out _);
        FMOD.Sound Make(int ms, bool loop)
        {
            int len = rate * ms / 1000; var pcm = new byte[len * 2]; var rnd = new Random(1);
            for (int i = 0; i < len; i++) { short v = (short)((rnd.NextDouble() * 2 - 1) * 3000); pcm[i * 2] = (byte)(v & 0xFF); pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF); }
            var info = new FMOD.CREATESOUNDEXINFO
            {
                cbsize = System.Runtime.InteropServices.Marshal.SizeOf<FMOD.CREATESOUNDEXINFO>(),
                length = (uint)pcm.Length, numchannels = 1, defaultfrequency = rate, format = FMOD.SOUND_FORMAT.PCM16,
            };
            sys.createSound(pcm, FMOD.MODE.OPENMEMORY | FMOD.MODE.OPENRAW | (loop ? FMOD.MODE.LOOP_NORMAL : FMOD.MODE.LOOP_OFF), ref info, out var snd);
            return snd;
        }
        var loopSnd = Make(1000, true); var shortSnd = Make(60, false);
        sys.getMasterChannelGroup(out var master);
        sys.createChannelGroup("bus", out var bus); master.addGroup(bus);
        sys.createDSPByType(FMOD.DSP_TYPE.SFXREVERB, out var reverb); bus.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.TAIL, reverb);
        void Tick(int n = 6) { for (int i = 0; i < n; i++) { sys.update(); Thread.Sleep(10); } }
        int Count() { reverb.getNumInputs(out int n); return n; }
        void Say(string s) { Console.WriteLine("  " + s); Console.Out.Flush(); }
        FMOD.DSPConnection Send(FMOD.Channel ch)
        {
            ch.getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.FADER, out var fader);
            var r = reverb.addInput(fader, out var c, FMOD.DSPCONNECTION_TYPE.SEND);
            if (r != FMOD.RESULT.OK) Say($"addInput -> {r}");
            return c;
        }
        // A permanent send so the trip-wire's honest add/remove is the only thing that moves the count.
        sys.playSound(loopSnd, master, false, out var keeper); Send(keeper); Tick();
        Say($"scenario {scenario}: baseline reverb.inputs={Count()} (the keeper)");

        switch (scenario)
        {
            case 1:
                sys.playSound(shortSnd, master, false, out var c1); Send(c1); Tick(2);
                Say($"one-shot with a send playing: inputs={Count()}"); Tick(12);
                c1.isPlaying(out bool p1); Say($"it ended on its own (isPlaying={p1}): inputs={Count()}");
                break;
            case 2:
                sys.playSound(loopSnd, master, false, out var c2); Send(c2); Tick();
                Say($"loop with a send: inputs={Count()}"); c2.stop(); Tick();
                Say($"after stop(): inputs={Count()}");
                break;
            case 3:
                sys.playSound(loopSnd, master, false, out var c3);
                sys.createDSPByType(FMOD.DSP_TYPE.THREE_EQ, out var eq); c3.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.HEAD, eq);
                var conn3 = Send(c3); Tick();
                Say($"loop with EQ at HEAD and a send: inputs={Count()}");
                c3.removeDSP(eq); Tick(); Say($"after removeDSP(eq): inputs={Count()}");
                c3.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.TAIL, eq); Tick(); Say($"after addDSP(TAIL, eq): inputs={Count()}");
                c3.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.HEAD, eq); Tick(); Say($"after addDSP(HEAD, eq) again (moved): inputs={Count()}");
                var rd = reverb.disconnectFrom(default, conn3); Tick(); Say($"honest disconnect of that send by connection only -> {rd}: inputs={Count()}");
                c3.stop(); Tick(); Say($"after stop(): inputs={Count()}");
                break;
            case 4:
            case 5:
            {
                // 4 software channels, the keeper takes one; play 6 more so some go virtual.
                var chans = new FMOD.Channel[6];
                for (int i = 0; i < chans.Length; i++) { sys.playSound(loopSnd, master, true, out chans[i]); chans[i].setVolume(1f - i * 0.15f); Send(chans[i]); chans[i].setPaused(false); }
                Tick();
                int virt = 0; foreach (var ch in chans) { ch.isVirtual(out bool v); if (v) virt++; }
                Say($"6 sending loops on 4 software channels: {virt} virtual, inputs={Count()}");
                // Swap loudness so the virtual ones come back real and the real ones go virtual.
                for (int i = 0; i < chans.Length; i++) chans[i].setVolume(0.1f + i * 0.15f);
                Tick();
                virt = 0; foreach (var ch in chans) { ch.isVirtual(out bool v); if (v) virt++; }
                Say($"after swapping volumes: {virt} virtual, inputs={Count()}");
                if (scenario == 5)
                {
                    foreach (var ch in chans) { ch.isVirtual(out bool v); if (v) ch.stop(); }
                    Tick(); Say($"stopped the virtual ones: inputs={Count()}");
                }
                foreach (var ch in chans) ch.stop(); Tick();
                Say($"all stopped: inputs={Count()}");
                break;
            }
            case 6:
            {
                sys.createChannelGroup("temp", out var temp); master.addGroup(temp);
                sys.playSound(loopSnd, temp, false, out var c6); Send(c6); Tick();
                Say($"loop in a temp group with a send: inputs={Count()}");
                temp.release(); Tick(); Say($"after temp group release(): inputs={Count()}");
                break;
            }
        }
        Say($"trip-wire: count now {Count()} with the keeper's one real send.");
        keeper.getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.FADER, out var kf);
        var extra = Send(keeper); Tick(); Say($"added one more send: inputs={Count()}");
        var rr = reverb.disconnectFrom(kf, extra); Tick(); Say($"removed it honestly -> {rr}: inputs={Count()}");
        Say("removing the keeper's own send (count must reach 0 with the list empty)...");
        keeper.stop(); Tick(); Say($"keeper stopped: inputs={Count()}");
        var last = Send(keeper); // keeper is dead; this fails, harmless
        sys.playSound(loopSnd, master, false, out var k2); var s2 = Send(k2); Tick(); Say($"fresh send: inputs={Count()}");
        k2.getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.FADER, out var k2f);
        var r2 = reverb.disconnectFrom(k2f, s2); Tick(); Say($"fresh send removed honestly -> {r2}: inputs={Count()}");
        Console.WriteLine($"RESULT: SURVIVED scenario {scenario} — final inputs={Count()} (0 means no drift)");
        k2.stop(); Tick(); sys.close(); sys.release();
        return 0;
    }

    /// <summary>
    /// How many region reverb buses FMOD will actually give out, and what it does when it will not.
    ///
    /// A bus is a channel group with an SFXREVERB unit in it, and one was created for every region
    /// any source had ever sent to. On the city that reached 185 and climbing — the number is bounded
    /// by how far a player has walked, not by the map — and the two FMOD calls that make one had
    /// their results ignored, so a refusal was a null dereference in native code with no exception
    /// and no log line. This asks for many more than a map could ever need and says what happened.
    /// </summary>
    public static int RunReverbChurn(int regions = 400)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }
        provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);

        var map = new OpenFPS.Common.AcousticMap(new Vector3(2000, 200, 2000), new Vector3(-1000, 0, -1000), 1f);
        for (int i = 0; i < regions; i++)
        {
            // A closed room, so RoomAcoustics gives it a real decay and the bus is built wet.
            map.Regions[9000 + i] = new OpenFPS.Common.Components.RegionComponent
            {
                FriendlyName = $"Room {i}", IsIndoor = true,
                RoomSize = new Vector3(6f, 3f, 8f),
                Materials = new[] { 18, 18, 18, 18, 18, 18 },
            };
            map.RegionPositions[9000 + i] = new Vector3((i % 20) * 40f - 400f, 1.6f, (i / 20) * 40f - 400f);
        }
        provider.SetAcousticMap(map);

        // One voice per region, each sending to its own room: the thing that makes a bus.
        for (int i = 0; i < regions; i++)
        {
            provider.PlaySpatialSound(new SpatialEmitter
            {
                EntityId = 900000 + i,
                Type = EmitterType.WorldLocked,
                IsSynth = true, SynthWave = SynthWaveType.Noise, SynthFrequency = 220f,
                SynthFilterCutoff = 1f, Volume = 0.05f,
                Position = map.RegionPositions[9000 + i],
                Range = 60f, MinDistance = 1f,
                EnableReverb = true, TargetRegionId = 9000 + i,
            });
            provider.Update();
            if (i % 50 == 0) Console.WriteLine($"  {i} region(s) asked for a bus...");
        }
        for (int k = 0; k < 40; k++) { provider.Update(); Thread.Sleep(8); }

        Console.WriteLine($"RESULT: SURVIVED — {regions} regions asked for a reverb bus. "
                        + "Any refusal is logged above as 'FMOD would not make a bus'.");
        provider.Dispose();
        return 0;
    }
}
