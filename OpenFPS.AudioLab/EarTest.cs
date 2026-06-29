using System;
using System.Numerics;
using System.IO;
using Thread = System.Threading.Thread;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// Interactive ear-test for the Phase 4 Steam Audio pipeline, driving the REAL FmodAudioProvider +
/// AsyncAcousticWorker (the new code path — the hand-rolled spatializer is bypassed because SA sim is
/// active). A looping police siren plays inside the demo wood-room; you walk the listener around with the
/// keyboard and should hear: occlusion (muffled behind a wall, clear in the doorway), doorway localization
/// (the sound comes from the open door when you're behind a wall), and Doppler (pitch shift while moving).
///
/// HEADPHONES recommended. Set OPENFPS_STEAMAUDIO_SIM=0 to A/B against the old hand-rolled spatializer.
/// </summary>
public static class EarTest
{
    public static int Run()
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed (is libfmod present?)"); return 1; }

        var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        worker.Start();
        worker.UpdateWorld(BuildWoodRoomWorld());

        // The new police siren, looping, inside the room. SoundId contains "ASSETS" so the provider treats
        // it as a direct path (resolved relative to the repo root / cwd).
        string siren = Path.GetFullPath(Path.Combine("OpenFPS.Client", "ASSETS", "SOUNDS", "BEACONS", "siren.wav"));
        if (!File.Exists(siren)) { Console.WriteLine($"siren asset not found at {siren}"); return 1; }

        var sourcePos = new Vector3(7, 1.5f, 15); // inside the wood room
        const int sourceId = 1;
        var emitter = new SpatialEmitter
        {
            EntityId = sourceId,
            SoundId = siren,
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.WorldLocked,
            Position = sourcePos,
            ApparentPosition = sourcePos,
            Volume = 1.0f,
            Pitch = 1.0f,
            Range = 60f,
            MinDistance = 2f,
            ConeInside = 360f,
        };
        provider.PlaySpatialSound(emitter);

        Console.WriteLine("=== Phase 4 EAR TEST (Steam Audio sim active) — HEADPHONES ===");
        Console.WriteLine("Siren is inside a concrete room; the only opening is a doorway on the south wall (around x=7, z=10).");
        Console.WriteLine("Move:  W/S = forward/back (±z)   A/D = left/right (±x)   J/L = turn left/right   Q = quit");
        Console.WriteLine("Try:   in the doorway (clear/loud) -> step east behind the wall (muffled, arrives from the door).");
        Console.WriteLine();

        var listenerPos = new Vector3(7, 1.5f, 4); // just outside the doorway
        float yaw = 0f; // facing +Z (toward the room)
        var prevPos = listenerPos;

        // Interactive when we have a real terminal; otherwise run a scripted flythrough (also lets the test
        // run hands-free and stay smoke-testable headless).
        bool interactive = !Console.IsInputRedirected;
        bool running = true;
        if (interactive)
        {
            var keyThread = new Thread(() =>
            {
                try
                {
                    while (running)
                    {
                        var k = Console.ReadKey(true).Key;
                        Vector3 fwd = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
                        Vector3 right = Vector3.Transform(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
                        const float step = 0.7f;
                        switch (k)
                        {
                            case ConsoleKey.W: listenerPos += fwd * step; break;
                            case ConsoleKey.S: listenerPos -= fwd * step; break;
                            case ConsoleKey.A: listenerPos -= right * step; break;
                            case ConsoleKey.D: listenerPos += right * step; break;
                            case ConsoleKey.J: yaw -= 0.20f; break;
                            case ConsoleKey.L: yaw += 0.20f; break;
                            case ConsoleKey.Q: running = false; break;
                        }
                    }
                }
                catch (InvalidOperationException) { running = false; } // no console input available
            }) { IsBackground = true };
            keyThread.Start();
        }
        else
        {
            Console.WriteLine("(no interactive terminal — running a 16 s scripted flythrough: doorway → behind the east wall → back → into the room)");
        }

        // Scripted waypoints (used in non-interactive mode): doorway, behind east wall, doorway, inside.
        var path = new[]
        {
            new Vector3(7, 1.5f, 4), new Vector3(16, 1.5f, 15), new Vector3(7, 1.5f, 4), new Vector3(7, 1.5f, 13),
        };
        const float legSeconds = 4f;

        const float dt = 0.03f;
        int frame = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (running)
        {
            if (!interactive)
            {
                float t = (float)sw.Elapsed.TotalSeconds;
                if (t >= legSeconds * (path.Length - 1)) break;
                int leg = (int)(t / legSeconds);
                float f = (t - leg * legSeconds) / legSeconds;
                listenerPos = Vector3.Lerp(path[leg], path[leg + 1], f);
            }

            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            Vector3 vel = (listenerPos - prevPos) / dt;
            prevPos = listenerPos;

            worker.EnqueueRequest(new AcousticRequest { EntityId = sourceId, ListenerPos = listenerPos, SourcePos = sourcePos });
            if (worker.TryGetResult(sourceId, out var paths) && paths.Count > 0)
                provider.SetAcousticPath(sourceId, paths[0]);

            // Keep the emitter's velocity-free real position; the listener carries the motion (for Doppler).
            provider.UpdateSpatialAttributes(emitter);
            provider.UpdateListener(listenerPos, rot, vel, -1);
            provider.Update();

            if (++frame % 10 == 0)
            {
                float occ = 0f;
                if (worker.TryGetResult(sourceId, out var p) && p.Count > 0) occ = p[0].Occlusion;
                float dist = Vector3.Distance(listenerPos, sourcePos);
                Console.Write($"\rlistener=({listenerPos.X,5:F1},{listenerPos.Z,5:F1}) yaw={yaw * 180f / MathF.PI,4:F0}°  dist={dist,4:F1}  occlusion={occ:F2}   ");
            }
            Thread.Sleep((int)(dt * 1000));
        }

        Console.WriteLine("\nstopping...");
        provider.StopSound(sourceId);
        worker.Dispose();
        provider.Dispose();
        return 0;
    }

    private static WorldSnapshot BuildWoodRoomWorld()
    {
        var world = new WorldSnapshot();
        int id = 100;
        void AddBox(Vector3 c, Vector3 s, string mat)
        {
            var def = new EntityDefinition
            {
                EntityId = id,
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = s, IsSolid = true },
                Material = new MaterialComponent { Material = mat },
            };
            world.Entities[id] = new EntitySnapshot
            {
                Id = id, Definition = def,
                Transform = new Transform { Position = c, Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            id++;
        }

        AddBox(new Vector3(7, -0.25f, 13), new Vector3(30, 0.5f, 30), "Wood");     // floor (for pathing probes)
        AddBox(new Vector3(7, 2, 20),  new Vector3(10, 4, 0.5f), "Concrete");      // north
        AddBox(new Vector3(12, 2, 15), new Vector3(0.5f, 4, 10), "Concrete");      // east
        AddBox(new Vector3(2, 2, 15),  new Vector3(0.5f, 4, 10), "Concrete");      // west
        AddBox(new Vector3(4, 2, 10),  new Vector3(4, 4, 0.5f), "Concrete");       // south-left  (x2..6)
        AddBox(new Vector3(10, 2, 10), new Vector3(4, 4, 0.5f), "Concrete");       // south-right (x8..12) -> door x6..8
        return world;
    }
}
