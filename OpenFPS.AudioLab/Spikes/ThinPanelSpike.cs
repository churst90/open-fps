using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --thin-panel [material=Glass] [face=1.9,2.1] [mm=4,12,30] [ear=2] [src=10]: what Steam Audio's direct stage lets through one panel
/// standing alone between a source and the ear, against what the panel's construction lets through
/// (WallTransmission), at a range of thicknesses. A panel thinner than the step Steam Audio takes past
/// each hit may be crossed as one face instead of two (SteamAudioScene.MaterialIndex, the power 2/3).
/// </summary>
public static class ThinPanelSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string material = args.FirstOrDefault(a => a.StartsWith("material="))?[9..] ?? "Glass";
        var faceArg = args.FirstOrDefault(a => a.StartsWith("face="))?[5..]?.Split(',');
        float w = faceArg != null ? float.Parse(faceArg[0], System.Globalization.CultureInfo.InvariantCulture) : 1.9f;
        float h = faceArg != null ? float.Parse(faceArg[1], System.Globalization.CultureInfo.InvariantCulture) : 2.1f;
        AsyncAcousticWorker.TraceProvenance = true;
        static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));
        var thickArg = args.FirstOrDefault(a => a.StartsWith("mm="))?[3..]?.Split(',');
        float earMetres = float.Parse(args.FirstOrDefault(a => a.StartsWith("ear="))?[4..] ?? "2", System.Globalization.CultureInfo.InvariantCulture);
        float srcMetres = float.Parse(args.FirstOrDefault(a => a.StartsWith("src="))?[4..] ?? "10", System.Globalization.CultureInfo.InvariantCulture);
        float[] mm = thickArg?.Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray() ?? new[] { 4f, 6f, 12f, 20f, 30f, 50f, 100f, 200f };
        Console.WriteLine($"  one {material} panel {w} x {h} m, ear {earMetres} m one side, source {srcMetres} m the other, straight through its middle");
        Console.WriteLine("  thickness   model low/mid/high dB      Steam Audio direct");
        foreach (float t in mm.Select(x => x / 1000f))
        {
            var world = new WorldSnapshot();
            var def = new EntityDefinition
            {
                EntityId = 1,
                Type = EntityType.StaticObject,
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(w, h, t), IsSolid = true },
                Material = new MaterialComponent { Material = material },
                SoundEmitter = new SoundEmitterComponent(),
            };
            def.Transform = new Transform { Position = new Vector3(0, 1.05f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
            world.Entities[1] = new EntitySnapshot { Id = 1, Definition = def, Transform = def.Transform };
            var acoustics = new SpatialAcoustics();
            using var worker = new AsyncAcousticWorker(acoustics);
            worker.UpdateWorld(world);
            worker.Start();
            var ear = new Vector3(0, 1.0f, -earMetres);
            var src = new Vector3(0, 1.0f, srcMetres);
            string answer = "no answer";
            var until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until)
            {
                worker.EnqueueRequest(new AcousticRequest { EntityId = 5, ListenerPos = ear, SourcePos = src, SourceRadius = 0.01f });
                Thread.Sleep(50);
                if (worker.Provenance.TryGetValue(5, out var made) && made.StartsWith("sim")) { answer = made; break; }
            }
            var (l, m, hh) = WallTransmission.BandGains(material, new Vector3(w, h, t), default);
            Console.WriteLine($"  {t * 1000f,6:F0} mm   {Db(l),6:F1} {Db(m),6:F1} {Db(hh),6:F1}        {answer}");
        }
        return 0;
    }
}
