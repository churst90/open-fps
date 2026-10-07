using System.Numerics;
using System.Text;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Hearing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// An instrument, not a test: which standing sources on the city map win the machine budget at a few
/// listening spots, ranked the old way (the gain the law plays a voice at, its loudness correction
/// included) and the new way (how loud it is to the ear, docs/EAR_MODEL.md, Ranking).
///
/// The city is loaded by the server's own MapManager and handed to a ClientAudioHarness as the server
/// would hand its definitions to a client. Every physical voice kind on the map is rendered offline for
/// a few seconds and measured (BandAnalyser), as the game's live voices measure themselves, so the
/// ranking reads the spectra the game would have. Set OPENFPS_PROBE_OUT to a directory to run it; it
/// writes ranking.txt there. Without it, it does nothing.
/// </summary>
[Collection(nameof(LevelCompressionSetting))]
public class RankingByLoudnessProbe
{
    private readonly ITestOutputHelper _o;
    public RankingByLoudnessProbe(ITestOutputHelper o) => _o = o;

    private const int Rate = 48000;

    /// <summary>(name, east, north, height) as LISTENING_SPOTS.md gives them.</summary>
    private static readonly (string Name, float East, float North, float Height)[] Spots =
    {
        ("Elm Park, by the fountain", -325f, 219f, 0.2f),
        ("58 Alder Street back garden, by the fire", -325f, 141f, 0.2f),
        ("Alder Street pavement", -296f, 124.5f, 0.5f),
        ("Market Square", -66f, 190f, 0.5f),
        ("Foundry Street spawn", 60f, 122f, 0.15f),
        ("Southgate, Mill Road crossing", -160f, -175f, 0.5f),
    };

    [Fact]
    [Trait("Category", "Probe")]
    public void Probe_city_machine_budget_by_gain_and_by_loudness()
    {
        string? outDir = Environment.GetEnvironmentVariable("OPENFPS_PROBE_OUT");
        if (string.IsNullOrEmpty(outDir)) return;
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        void Say(string line) { report.AppendLine(line); _o.WriteLine(line); }

        // The city as the server loads it.
        string dir = Path.Combine(Path.GetTempPath(), "openfps-rankprobe-" + Guid.NewGuid().ToString("N"), "maps");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "city.json"), Path.Combine(dir, "city.json"));
        var maps = new MapManager(new MapRepository(dir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out var lookup));
        var defs = lookup.Values.Select(e => EntityDefinitionFactory.From(world, e))
                                .Where(d => d.SoundEmitter.IsSynth && !string.IsNullOrEmpty(d.SoundEmitter.SoundId))
                                .ToList();
        Say($"city: {lookup.Count} entities, {defs.Count} synthesised emitters");
        foreach (var g in defs.GroupBy(d => d.SoundEmitter.SoundId).OrderByDescending(g => g.Count()))
            Say($"  {g.Count(),5}  {g.Key}");

        using var wind = WindField.Hold(WindWeather.Steady(4.5f, 270f, 0f));
        bool wasOn = EarModel.Enabled;
        try
        {
            EarModel.Enabled = true;
            EarTimbres.Clear();
            // Each kind's spectrum, as its live voice would measure itself.
            Say("\nmeasured spectra (6 s each, the first second left out), and what the law and the ear make of each:");
            Say("  sound                               level  correction  heard-gain over played gain at 10 m");
            foreach (string id in defs.Select(d => d.SoundEmitter.SoundId!).Distinct())
            {
                var voice = Voice(id);
                if (voice == null) { Say($"  {id,-36} not rendered here: ranked unmeasured"); continue; }
                var pcm = new float[Rate * 6];
                for (int at = 0; at < pcm.Length; at += 1024) voice.Render(pcm.AsSpan(at, Math.Min(1024, pcm.Length - at)));
                var timbre = Timbre.FromBandPowers(BandAnalyser.Measure(pcm.AsSpan(Rate), Rate), id);
                if (timbre == null) { Say($"  {id,-36} silent (dry or off): ranked unmeasured"); continue; }
                EarTimbres.Set(id, timbre);
                float level = LevelOf(id);
                float corr = Loudness.TimbreCorrectionDb(level, timbre);
                var (gain, reference) = Loudness.Place(level);
                float played = Loudness.RenderedGain(gain * MathF.Pow(10f, corr / 20f), reference, 500f, 10f);
                float heard = Loudness.HeardGain(played, timbre, physical: true);
                Say($"  {id,-36} {level,5:F0}  {corr,+10:+0.0;-0.0}  {20 * MathF.Log10(heard / played),+8:+0.0;-0.0} dB");
            }

            foreach (var spot in Spots)
            {
                var feet = new Vector3(spot.East, spot.Height, spot.North);
                var before = Choose(defs, feet, onGain: true);
                var after = Choose(defs, feet, onGain: false);
                Say($"\n{spot.Name} (/tp {spot.East} {spot.North} {spot.Height}): budget {ClientAudioSystem.MachineVoiceBudget} machines");
                Say("  source                                  dist m   rank by gain   rank by loudness   before  after");
                var all = before.Keys.Union(after.Keys)
                                .OrderByDescending(k => after.TryGetValue(k, out var a) ? a.Rank : float.NegativeInfinity)
                                .Take(18);
                foreach (var key in all)
                {
                    var b = before.GetValueOrDefault(key);
                    var a = after.GetValueOrDefault(key);
                    var any = a.Name != null ? a : b;
                    string Mark(bool live) => live ? "voice" : "-";
                    string flag = b.Live != a.Live ? (a.Live ? "   WINS" : "   LOSES") : "";
                    Say($"  {any.Name,-38} {any.Distance,7:F0}   {Db(b.Rank),10}   {Db(a.Rank),12}       {Mark(b.Live),-6}  {Mark(a.Live)}{flag}");
                }
                var won = after.Where(kv => kv.Value.Live && !before.GetValueOrDefault(kv.Key).Live).Select(kv => kv.Value.Name).ToList();
                var lost = before.Where(kv => kv.Value.Live && !after.GetValueOrDefault(kv.Key).Live).Select(kv => kv.Value.Name).ToList();
                Say($"  wins a voice: {(won.Count == 0 ? "nothing" : string.Join(", ", won))}");
                Say($"  loses its voice: {(lost.Count == 0 ? "nothing" : string.Join(", ", lost))}");
            }
        }
        finally
        {
            Loudness.RankOnPlayedGain = false;
            EarModel.Enabled = wasOn;
            EarTimbres.Clear();
        }
        File.WriteAllText(Path.Combine(outDir, "ranking.txt"), report.ToString());
        try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch (IOException) { }
    }

    private static string Db(float rank) => rank > 0f ? $"{20f * MathF.Log10(rank),6:F1} dB" : "     -";

    private static readonly Vector3 Somewhere = new(-300f, 0f, 150f);

    /// <summary>A voice of this kind, as the provider would build it, for measuring.</summary>
    private static PhysicalVoiceState? Voice(string id)
    {
        if (id.StartsWith("machine:", StringComparison.OrdinalIgnoreCase))
            return new MachineVoiceState(SmallMachineSpec.ByName(id[8..]), Rate, entityId: 4242, seed: 11);
        if (id.StartsWith("foliage:", StringComparison.OrdinalIgnoreCase))
            return new FoliageVoiceState(FoliageSpec.ByName(id[8..]), Rate, 9, Somewhere);
        if (id.StartsWith("fire:", StringComparison.OrdinalIgnoreCase))
            return new FireVoiceState(FireSpec.ByName(id[5..]), Rate, 13, Somewhere);
        if (WaterFeatureVoice.ParseKey(id, out string preset, out string feature, out int tap))
            return new WaterTapState(new WaterFeatureVoice(preset + "/" + feature, WaterFeatureSpec.ByName(preset), Rate, 1), tap, Rate, Somewhere);
        if (id.StartsWith("flow:", StringComparison.OrdinalIgnoreCase))
            return new NaturePlaceState(new PlacedNatureVoice(id, RunningWaterSpec.ByName(id[5..]), Rate, 19, Somewhere), 0, Rate, Somewhere);
        return null;
    }

    private static float LevelOf(string id)
    {
        if (id.StartsWith("machine:", StringComparison.OrdinalIgnoreCase)) return SmallMachineSpec.ByName(id[8..]).SourceLevelDb;
        if (id.StartsWith("foliage:", StringComparison.OrdinalIgnoreCase)) return FoliageSpec.ByName(id[8..]).SourceLevelDb;
        if (id.StartsWith("fire:", StringComparison.OrdinalIgnoreCase)) return FireSpec.ByName(id[5..]).SourceLevelDb;
        if (WaterFeatureVoice.ParseKey(id, out string preset, out _, out _)) return WaterFeatureSpec.ByName(preset).SourceLevelDb;
        if (id.StartsWith("flow:", StringComparison.OrdinalIgnoreCase)) return RunningWaterSpec.ByName(id[5..]).SourceLevelDb;
        return 0f;
    }

    private struct Ranked
    {
        public string? Name;
        public float Distance, Rank;
        public bool Live;
    }

    /// <summary>The machine budget's choice at a spot, from a standing start, under one rule: per group
    /// (a machine, a fountain's taps), what it ranked at and whether it has a voice.</summary>
    private static Dictionary<string, Ranked> Choose(List<EntityDefinition> defs, Vector3 feet, bool onGain)
    {
        Loudness.RankOnPlayedGain = onGain;
        var h = new ClientAudioHarness();
        foreach (var d in defs) h.World.RegisterDefinition(d);
        h.StandAt(feet);
        h.Tick(12);
        var groups = (System.Collections.IDictionary)typeof(ClientAudioSystem)
            .GetField("_machineGroups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(h.Audio)!;
        var byId = defs.ToDictionary(d => d.EntityId);
        var result = new Dictionary<string, Ranked>();
        var ear = feet + new Vector3(0f, 1.6f, 0f);
        foreach (System.Collections.DictionaryEntry kv in groups)
        {
            var g = kv.Value!;
            var members = (List<int>)g.GetType().GetField("Members")!.GetValue(g)!;
            float level = (float)g.GetType().GetField("Level")!.GetValue(g)!;
            if (members.Count == 0) continue;
            int first = members[0];
            string name = byId.TryGetValue(first, out var def) ? def.SoundEmitter.SoundId! : $"#{first}";
            if (kv.Key is string k && k.StartsWith("water:")) name = k;
            float dist = members.Where(byId.ContainsKey).Select(m => Vector3.Distance(byId[m].Transform.Position, ear)).DefaultIfEmpty(0f).Min();
            result[(string)kv.Key] = new Ranked
            {
                Name = $"{name} #{first}", Distance = dist, Rank = level,
                Live = members.Any(h.Mixer.Live.Contains),
            };
        }
        Loudness.RankOnPlayedGain = false;
        return result;
    }
}
