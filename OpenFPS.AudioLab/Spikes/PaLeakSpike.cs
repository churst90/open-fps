using System.Globalization;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --pa-leak [out=DIR] [overlays=DIR] [before=L,M,H@x,y,z] [dry]: Cody's public address speaker on Main Street
/// (pa_speaker, placed with the world editor, 2026-10-09) heard through the real provider, its master
/// written to WAV by FMOD's file writer, each scene alone between gaps: on the pavement outside Selby
/// House's front door, in the lobby behind the shut glass door, and in the lobby with the door open.
///
/// The paths are the occlusion worker's for this build (Steam Audio on, the city as the client gets it);
/// before=L,M,H@x,y,z adds a lobby scene with a path given by hand (band gains in dB and where it was
/// heard from), for what an older build measured. The voice is the emitter ClientAudioSystem submits
/// for the prefab: the announcement once, Volume 1, MinDistance 12, Range 400, its 90/240 cone aimed
/// where the speaker is turned. The lobby's room is set from the client's log line ("ray-traced RT60
/// 1595 ms; enclosure 86 %"). Each scene's name and start go to DIR/segments.csv.
/// </summary>
public static class PaLeakSpike
{
    private static readonly Vector3 Lobby = new(11.84f, 1.7f, 157.36f);
    private static readonly Vector3 Pavement = new(8.0f, 1.7f, 157.5f);

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string outDir = args.FirstOrDefault(a => a.StartsWith("out="))?[4..] ?? "/tmp/openfps-pa-leak";
        string? overlays = args.FirstOrDefault(a => a.StartsWith("overlays="))?[9..];
        Directory.CreateDirectory(outDir);
        if (!File.Exists(OpenFPS.AudioLab.LabPaths.Output("ANNOUNCE", "st_louis_welcome.wav")))
        {
            Console.WriteLine($"  FAIL: no ANNOUNCE/st_louis_welcome.wav under {OpenFPS.AudioLab.LabPaths.Output()}; link the client's ANNOUNCE folder there.");
            return 1;
        }

        var world = PathProbeSpike.LoadAsClient(AppContext.BaseDirectory, "city", overlays);
        var acoustics = new SpatialAcoustics();
        var pa = world.Entities.Values.FirstOrDefault(e => e.Definition.SoundEmitter.SoundId == "ANNOUNCE/st_louis_welcome"
                                                           && Vector3.Distance(e.Transform.Position, new Vector3(-1.1f, 0.45f, 158.1f)) < 1f);
        if (pa.Definition == null) { Console.WriteLine("  FAIL: no PA at Main Street (-1.1, 0.45, 158.1); give overlays=DIR"); return 1; }
        var def = pa.Definition;
        Vector3 mouth = AudioEmission.PointFor(pa);
        Vector3 aim = Vector3.Transform(def.SoundEmitter.Direction.LengthSquared() > 0f ? Vector3.Normalize(def.SoundEmitter.Direction) : Vector3.UnitZ,
                                        pa.Transform.Rotation);
        int paRegion = acoustics.GetRegionAt(world, mouth);

        // The worker's answers, door shut, as the game asks them.
        var shut = new Dictionary<string, AcousticPathData>();
        // Kept running while the scenes play: it traces the reverb at the ear (TracedReverbSet), as in the game.
        using var worker = new AsyncAcousticWorker(acoustics);
        {
            worker.UpdateWorld(world);
            worker.Start();
            int id = 1;
            foreach (var (name, ear) in new[] { ("pavement", Pavement), ("lobby", Lobby) })
            {
                var until = DateTime.UtcNow.AddSeconds(120);
                while (DateTime.UtcNow < until)
                {
                    worker.EnqueueRequest(new AcousticRequest { EntityId = id, ListenerPos = ear, SourcePos = mouth, SourceRadius = AudioEmission.OcclusionRadiusFor(pa) });
                    Thread.Sleep(50);
                    if (worker.TryGetResult(id, out var paths) && paths.Count > 0 && paths[0].SourcePosition == mouth)
                    { shut[name] = paths.First(p => !p.IsReflection); break; }
                }
                id++;
            }
        }
        static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));
        foreach (var (name, p) in shut)
            Console.WriteLine($"  {name}: worker {Db(p.EqLow):F1}/{Db(p.EqMid):F1}/{Db(p.EqHigh):F1} dB, occlusion {p.Occlusion:F2}, heard from ({p.ApparentPosition.X:F1}, {p.ApparentPosition.Y:F1}, {p.ApparentPosition.Z:F1})");

        var scenes = new List<(string Name, Vector3 Ear, AcousticPathData Path)>
        {
            ("pavement_outside_door", Pavement, shut["pavement"]),
            ("lobby_door_shut", Lobby, shut["lobby"]),
            ("lobby_door_open", Lobby, Clear(mouth, Lobby, paRegion)),
        };
        if (args.FirstOrDefault(a => a.StartsWith("before="))?[7..] is { } before)
        {
            var parts = before.Split('@');
            var g = parts[0].Split(',').Select(v => MathF.Pow(10f, float.Parse(v, CultureInfo.InvariantCulture) / 20f)).ToArray();
            var at = parts[1].Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var p = shut["lobby"];
            p.EqLow = g[0]; p.EqMid = g[1]; p.EqHigh = g[2];
            p.Occlusion = Math.Clamp(1f - MathF.Max(g[0], MathF.Max(g[1], g[2])), 0f, AcousticConstants.OcclusionCap);
            p.ApparentPosition = new Vector3(at[0], at[1], at[2]);
            scenes.Insert(1, ("lobby_door_shut_before_fix", Lobby, p));
        }
        // dry: each lobby scene again with no reverb at all (listener and source in no place), to tell the
        // path from the room.
        if (args.Contains("dry"))
            foreach (var s in scenes.Where(s => s.Name.StartsWith("lobby")).ToList())
                scenes.Add((s.Name + "_dry", s.Ear, s.Path with { RegionId = -2 }));

        string wav = Path.Combine(outDir, "capture.wav");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", wav);
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }
        var csv = new System.Text.StringBuilder("name,start_s,seconds\n");
        try
        {
            if (world.AcousticMap != null) provider.SetAcousticMap(world.AcousticMap);
            // Other places' fields through the openings (OpeningRoutes.FieldAt), as ClientAudioSystem wires it.
            acoustics.RoutesFor(world);
            provider.RoutesSource = () => acoustics.Routes;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Run(double seconds, Action? frame = null)
            {
                double end = clock.Elapsed.TotalSeconds + seconds;
                while (clock.Elapsed.TotalSeconds < end) { frame?.Invoke(); provider.Update(); Thread.Sleep(16); }
            }
            // Facing east, into the building, as Cody stood ("facing 90"): the speaker behind him.
            var facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(1f, 0f));
            Run(1.0);
            int voice = 8955;
            foreach (var (name, ear, path) in scenes)
            {
                int region = name.EndsWith("_dry") ? -2 : acoustics.GetRegionAt(world, ear);
                bool inside = region != AcousticConstants.GlobalRegionId && world.AcousticMap!.Regions.TryGetValue(region, out var r) && r.IsIndoor;
                provider.UpdateListener(ear, facing, Vector3.Zero, region);
                // The lobby as the client's log measured it; the street as open air.
                if (inside) provider.SetSimulatedReverbDecay(1595f, 0.86f, 0.8f, 1.2f);
                else provider.SetSimulatedReverbDecay(AcousticConstants.MinReverbDecayMs, 0f, 1f, 1f);
                void Ask() => worker.EnqueueRequest(new AcousticRequest { EntityId = 50, ListenerPos = ear, SourcePos = mouth, SourceRadius = AudioEmission.OcclusionRadiusFor(pa) });
                Run(1.5, () => { Ask(); provider.UpdateListener(ear, facing, Vector3.Zero, region); });
                var e = new SpatialEmitter
                {
                    EntityId = voice,
                    SoundId = "ANNOUNCE/st_louis_welcome",
                    Mode = def.SoundEmitter.Mode,
                    Type = EmitterType.EntityAttached,
                    Position = mouth,
                    ApparentPosition = path.ApparentPosition,
                    EffectiveDistance = path.EffectiveDistance,
                    Occlusion = path.Occlusion,
                    EqLow = path.EqLow, EqMid = path.EqMid, EqHigh = path.EqHigh,
                    AirLowDb = path.AirLowDb, AirMidDb = path.AirMidDb, AirHighDb = path.AirHighDb,
                    ApertureFactor = path.ApertureFactor,
                    TransmissionBleed = path.TransmissionBleed,
                    Direction = aim,
                    Volume = def.SoundEmitter.Volume,
                    Range = MathF.Max(1f, def.SoundEmitter.Range),
                    MinDistance = def.SoundEmitter.MinDistance,
                    Pitch = 1f,
                    ConeInside = def.SoundEmitter.ConeInsideAngle,
                    ConeOutside = def.SoundEmitter.ConeOutsideAngle,
                    ConeOutsideVolume = def.SoundEmitter.ConeOutsideVolume,
                    TargetRegionId = path.RegionId,
                    PositionSampledAt = AudioClock.Now,
                };
                double start = clock.Elapsed.TotalSeconds;
                provider.PlaySpatialSound(e);
                Run(4.5, () => { Ask(); provider.UpdateListener(ear, facing, Vector3.Zero, region); provider.SetAcousticPath(voice, path); });
                csv.Append(CultureInfo.InvariantCulture, $"{name},{start:F3},4.5\n");
                Console.WriteLine($"  {name}: ear ({ear.X:F1}, {ear.Y:F1}, {ear.Z:F1}) in region {region}{(inside ? " (indoors)" : "")}, "
                                + $"path {Db(path.EqLow):F1}/{Db(path.EqMid):F1}/{Db(path.EqHigh):F1} dB, started at {start:F2} s");
                provider.StopSound(voice);
                voice++;
                Run(1.5);
            }
        }
        finally
        {
            provider.Dispose();
            File.WriteAllText(Path.Combine(outDir, "segments.csv"), csv.ToString());
            Console.WriteLine($"  mix written to {wav}");
        }
        return 0;
    }

    private static AcousticPathData Clear(Vector3 source, Vector3 ear, int region) => new()
    {
        Occlusion = 0f, EqLow = 1f, EqMid = 1f, EqHigh = 1f,
        ApparentPosition = source, EffectiveDistance = Vector3.Distance(source, ear),
        ApertureFactor = 1f, RoomGain = 1f, RegionId = region,
        SourcePosition = source,
    };
}
