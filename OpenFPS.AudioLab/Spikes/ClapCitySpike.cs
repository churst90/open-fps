using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Sound;
using OpenFPS.Materials;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --clap-city [out=DIR] [root=DIR/] [at=x,y,z] [yaw=DEG] [claps=4] [debug]: your own /clap in a room of the city
/// map through the whole client path (the map as the client gets it, ClientAudioSystem over the FmodAudioProvider,
/// its worker and Steam Audio, WorldAudioPlayer.Receive as the server's event arrives), by default where Cody
/// clapped in Selby House's ground-floor lobby on 2026-10-17 (game x 15.17, z 157.07, feet on the tiles).
/// Reports per clap the two ears' level and coherence in the room's early part (6-80 ms) and its tail
/// (80-400 ms, 400-1200 ms), per octave and broadband: a tail whose ears agree at every frequency is one channel
/// in both ears, the "reverb is mono" Cody heard. Also how the listener's traced stage plays its tail, and how
/// late each clap reached the capture after it was received (the mixer's own latency included).
/// Writes DIR/capture-float.wav.
/// </summary>
public static class ClapCitySpike
{
    private static readonly OpenFPS.Sound.Hearing.HearingSettings LabHearing = OpenFPS.Sound.Hearing.HearingSettings.FromEnvironment();

    private static readonly (float Lo, float Hi)[] Octaves = { (177f, 354f), (354f, 707f), (707f, 1414f), (1414f, 2828f), (2828f, 5657f) };

    public static int Run(string[] args)
    {
        string outDir = Arg(args, "out") ?? "/tmp/openfps-clap-city";
        string root = Arg(args, "root") ?? AppContext.BaseDirectory;
        int claps = int.TryParse(Arg(args, "claps"), out int c) ? c : 4;
        var feet = new Vector3(15.17f, 0.05f, 157.07f);
        if (Arg(args, "at") is { } at)
        {
            var p = at.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            feet = new Vector3(p[0], p[1], p[2]);
        }
        float yawDeg = float.TryParse(Arg(args, "yaw"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) ? y : 0f;
        Directory.CreateDirectory(outDir);
        AcousticRegistry.Initialize();
        if (!args.Contains("debug")) Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();

        var snapshot = OpenFPS.AudioLab.Spikes.PathProbeSpike.LoadAsClient(root, "city");
        var world = new ClientWorldState();
        world.Clear(new Vector3(4000, 400, 4000));
        foreach (var e in snapshot.Entities.Values) world.RegisterDefinition(e.Definition, deferAcoustics: true);
        world.SetAcousticMap(snapshot.AcousticMap!);

        string wav = Path.Combine(outDir, "capture-float.wav");
        System.Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", wav);
        System.Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        var provider = new FmodAudioProvider { Hearing = LabHearing };
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        facade.InitializeSteppedByHand(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        // The tap starts with the mixer: a clap's place in the capture against when it was received.
        double tapStart = clock.Elapsed.TotalSeconds;
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var beacons = BeaconPreferences.InMemory();
        foreach (var category in Beacons.Categories) beacons.Set(category, false);
        world.Clock = facade.Clock; world.Hearing = facade.Hearing;
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds, beacons: beacons);
        const int Self = 990_002;
        audio.WorldAudio.Sounds.SelfId = Self;
        audio.WorldAudio.Sounds.Self = () => (player.Position, player.Rotation);

        player.Position = feet;
        player.Yaw = yawDeg * MathF.PI / 180f;
        player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, player.Yaw);
        var received = new List<double>();
        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                audio.Update(world.GetSnapshot());
                facade.StepByHand();
                Thread.Sleep(4);
            }
        }
        // As the server sends /clap (CommandHandler.HandleClap): in front of the chest, on the body.
        var forward = Vector3.Transform(Vector3.UnitZ, player.Rotation);
        WorldAudioEvent Clap(int seed) => new()
        {
            SourceEntityId = Self, Label = "clap", Seed = seed,
            Sounds = new List<TransientSound> { new TransientSound
            {
                Character = SoundCharacter.Knock, Position = feet + new Vector3(0, 1.25f, 0) + forward * 0.3f,
                OnBody = true, BodyOffset = new Vector3(0f, 1.25f, 0.3f), LevelDb = Applause.SingleClapDb,
                SynthKey = Applause.ClapKey, DecaySeconds = 0.15f, Noisiness = 1f,
            } },
        };
        // busy: the binaural pool as a street of traffic leaves it (a few free, under the reserve) while the room's
        // bus is made, then given back, as walking in from the street did on 2026-10-17.
        // busy=hold: held through the claps too, as on a street that stays busy.
        bool hold = args.Contains("busy=hold"), busy = hold || args.Contains("busy");
        try
        {
            if (busy)
            {
                provider.HoldBinauralVoicesForLab(8);
                Pump(3.0);
                if (!hold) provider.ReleaseHeldBinauralVoicesForLab();
                Console.WriteLine($"  the room's bus was made with 8 binaural voices free{(hold ? ", and they stay held" : "")}");
            }
            Pump(12.0);
            Console.WriteLine($"  before: {provider.DescribeListenerStage()}");
            for (int k = 0; k < claps; k++)
            {
                received.Add(clock.Elapsed.TotalSeconds - tapStart);
                audio.WorldAudio.Receive(Clap(k + 1), provider.Clock.Now);
                Pump(0.3);
                if (k == 0) Console.WriteLine($"  clap 1 + 0.3 s: {provider.DescribeListenerStage()}");
                Pump(1.9);
            }
            Pump(0.5);
        }
        finally { facade.Dispose(); }
        return Measure(wav, received);
    }

    /// <summary>Each clap found after it was received, and its room's two ears.</summary>
    private static int Measure(string path, List<double> received)
    {
        var (l, r, sr) = ClapRoomSpike.ReadStereo(path);
        if (l.Length == 0) { Console.WriteLine($"  FAIL: nothing in {path}"); return 1; }
        Console.WriteLine("  clap | late ms | window       |  L dB   R dB | r0 broadband | IACC per octave 250 500 1k 2k 4k");
        int found = 0;
        foreach (double t in received)
        {
            int from = (int)(t * sr), to = Math.Min(l.Length, from + sr);
            double pk = 0; for (int i = from; i < to; i++) pk = Math.Max(pk, Math.Abs(l[i]) + Math.Abs(r[i]));
            if (pk < 1e-4) { Console.WriteLine($"  {found + 1,4} | none within a second of being received"); continue; }
            int on = -1; for (int i = from; i < to; i++) if (Math.Abs(l[i]) + Math.Abs(r[i]) >= 0.3 * pk) { on = i; break; }
            found++;
            foreach (var (name, a, b) in new[] { ("direct 0-6", 0.0, 0.006), ("early 6-80", 0.006, 0.08), ("tail 80-400", 0.08, 0.4), ("tail 400-1200", 0.4, 1.2) })
            {
                int i0 = on + (int)(a * sr), i1 = Math.Min(l.Length, on + (int)(b * sr));
                if (i1 - i0 < 64) continue;
                double el = 0, er = 0, lr = 0;
                var hl = ClapRoomSpike.Band(l, i0, i1, 150f, 8000f, sr); var hr = ClapRoomSpike.Band(r, i0, i1, 150f, 8000f, sr);
                for (int i = 0; i < hl.Length; i++) { el += hl[i] * hl[i]; er += hr[i] * hr[i]; lr += hl[i] * hr[i]; }
                var bands = string.Join(" ", Octaves.Select(o => ClapRoomSpike.Iacc(ClapRoomSpike.Band(l, i0, i1, o.Lo, o.Hi, sr), ClapRoomSpike.Band(r, i0, i1, o.Lo, o.Hi, sr)).ToString("F2")));
                Console.WriteLine($"  {found,4} | {(on - from) * 1000.0 / sr,7:F0} | {name,-12} | {10 * Math.Log10(el / (i1 - i0) + 1e-20),5:F1} {10 * Math.Log10(er / (i1 - i0) + 1e-20),6:F1} | {lr / Math.Sqrt(el * er + 1e-30),12:F2} | {bands}");
            }
        }
        return found == 0 ? 1 : 0;
    }

    private static string? Arg(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }
}
