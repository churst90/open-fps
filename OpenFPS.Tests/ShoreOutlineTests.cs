using System.Numerics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Audio.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;
using OpenFPS.Materials;

namespace OpenFPS.Tests;

/// <summary>
/// A pond's or a lake's shore heard along its whole outline as one source (docs/RUNNING_WATER.md 15.7): cut at the ear
/// as a line of running water is, each piece a column with the sea its own stretch of the outline faces, worked out
/// from the outline itself (which way the water lies, how far it reaches across), so the shore the wind blows onto laps
/// and the lee shore is still; before, it was stretches of about 20 m each a source with its fetch in its box.
/// </summary>
public class ShoreOutlineTests
{
    private readonly ITestOutputHelper _o;
    public ShoreOutlineTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;

    /// <summary>A rectangular pond 100 m east to west and 40 m north to south, its outline anticlockwise.</summary>
    private static Vector3[] Pond() => new[]
    {
        new Vector3(-50f, 0f, -20f), new Vector3(50f, 0f, -20f), new Vector3(50f, 0f, 20f), new Vector3(-50f, 0f, 20f), new Vector3(-50f, 0f, -20f),
    };

    /// <summary>A whole outline's key carries its length, and its spec is its stretches' together: as loud as that many
    /// stretches, as long as the outline.</summary>
    [Fact]
    public void AnOutlinesKeyIsAsLoudAsItsStretchesTogether()
    {
        string key = ShoreSpec.OutlineKey("shore:pond_bank", 410f);
        Assert.Equal("shore:pond_bank/outline/410", key);
        Assert.True(ShoreSpec.TryOutline(key, out string preset, out float perimeter));
        Assert.Equal("pond_bank", preset);
        var stretch = ModelLibrary.Presets.Shore("pond_bank");
        var outline = ModelLibrary.Presets.Shore(key);
        Assert.Equal(410f, outline.LengthMetres);
        Assert.Equal(stretch.SourceLevelDb + 10f * MathF.Log10(410f / stretch.LengthMetres), outline.SourceLevelDb, 3);
        Assert.False(ShoreSpec.TryOutline("shore:pond_bank/300/90/20", out _, out _));
        Assert.Null(new WideSources().Layout(key, ModelLibrary.Presets));
        Assert.True(new WideSources().IsOutline(key));
    }

    /// <summary>
    /// Cut from the middle of the pond with the wind from the west, each piece reads its own water from the outline:
    /// the east shore faces 100 m of water the wind blows across onto it and laps; the west shore is the lee and is
    /// still; the north and south shores face 40 m across and the wind along them. Rendered, the east shore's places
    /// carry the sound and the west shore's are silent.
    /// </summary>
    [Fact]
    public void TheShoreTheWindBlowsOntoLapsAndTheLeeShoreIsStill()
    {
        var path = new LinePath(Pond());
        var ear = new Vector3(0f, 1.7f, 0f);
        var cut = ExtendedSources.Cut(path, ear, ExtendedSources.MaxPlaces);
        var spec = ModelLibrary.Presets.Shore("pond_bank").AlongOutline(path.Length);
        var synth = new ShoreSynth(spec, Rate, 7, null, ExtendedSources.MaxPlaces) { WindSpeed = 8f, WindFromDegrees = 270f, Cut = cut };
        synth.Control(0.01f);
        var columns = synth.OutlineColumns;
        var energy = new double[ExtendedSources.MaxPlaces];
        var buf = new float[ExtendedSources.MaxPlaces];
        for (int i = 0; i < Rate * 12; i++)
        {
            if (i % 256 == 0) synth.Control(256f / Rate);
            synth.NextPlaces(buf);
            if (i < Rate * 2) continue;
            for (int k = 0; k < buf.Length; k++) energy[k] += buf[k] * (double)buf[k];
        }
        double east = 0, west = 0;
        for (int k = 0; k < cut.Count; k++)
        {
            int i = cut.PieceOfPlace(k);
            var at = path.PointAt(0.5f * (cut.Edges[i] + cut.Edges[i + 1]));
            static string SideOf(Vector3 p) => p.X > 49.9f ? "east" : p.X < -49.9f ? "west" : p.Z > 0f ? "north" : "south";
            // A piece round a corner holds two shores; only a piece of one is judged.
            string side = SideOf(path.PointAt(cut.Edges[i] + 0.01f)) == SideOf(path.PointAt(cut.Edges[i + 1] - 0.01f))
                ? SideOf(at) : "corner";
            _o.WriteLine($"place {k,2} {side,-5} at ({at.X,5:F1}, {at.Z,5:F1}): Hs {columns[k].Hs * 100:F1} cm on {columns[k].Width:F1} m, fetch {columns[k].Fetch:F0} m; {10 * Math.Log10(Math.Max(1e-30, energy[k])):F1} dB");
            if (side == "east") { Assert.True(columns[k].Hs > 0.02f); Assert.InRange(columns[k].Fetch, 95f, 105f); east += energy[k]; }
            if (side == "west") { Assert.Equal(0f, columns[k].Hs); Assert.Equal(0f, columns[k].Width); west += energy[k]; }
            if (side is "north" or "south") Assert.InRange(columns[k].Fetch, 35f, 45f);
        }
        Assert.True(east > 0 && west < 1e-6 * east, $"east {east:E2}, west {west:E2}");
    }

    /// <summary>The rectangular pond's shore as one source, the wind across it from the west.</summary>
    private static ClientAudioHarness PondShore()
    {
        var h = new ClientAudioHarness();
        h.Facade.Weather.Wind = WindWeather.Steady(8f, 270f, 0.2f);
        var def = new EntityDefinition
        {
            EntityId = 900,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "shore:pond_bank";
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 120f;
        def.SoundEmitter.Line = Pond().Select(p => p + new Vector3(0f, 0.06f, 0f)).ToArray();
        h.World.RegisterDefinition(def);
        return h;
    }

    /// <summary>
    /// Walking along the pond's long south shore 2 m back from its edge, its whole outline one source cut at the ear: the
    /// level by the mixer's law stays within ±1.5 dB of its mean along the middle 60 m and comes from the water beside
    /// you, where its stretches of 20 m rose and fell 4.2 dB (docs/RUNNING_WATER.md 15.2).
    /// </summary>
    [Fact]
    public void AlongAShoreTheLevelIsFlatAndBesideYou()
    {
        var h = PondShore();
        float eye = LocalPlayerState.StandingEyeHeight;
        h.StandAt(new Vector3(-30f, 0f, -22f));
        h.Tick(240);
        var levels = new List<double>();
        float worst = 0f;
        int frames = (int)(0.5 * ClientAudioSystem.UpdateHz), most = 0;
        for (float x = -30f; x <= 30f; x += 0.5f)
        {
            for (int f = 1; f <= frames; f++) { h.StandAt(new Vector3(x - 0.5f + 0.5f * f / frames, 0f, -22f)); h.Tick(); }
            var ear = new Vector3(x, eye, -22f);
            var heard = AudioWorld.HeardTogether(h.Mixer.Latest, h.Mixer.Live, h.Mixer.Pieces, id => id.StartsWith("shore:"), ear, h.Facade.Options.WideSources);
            levels.Add(10 * Math.Log10(heard.Power));
            most = Math.Max(most, heard.Voices);
            // The water is north of you.
            worst = MathF.Max(worst, MathF.Abs(((heard.BearingDegrees + 180f) % 360f) - 180f));
        }
        double mean = 10 * Math.Log10(levels.Average(l => Math.Pow(10, l / 10)));
        _o.WriteLine($"along the pond's south shore 2 m back: {levels.Min() - mean:F2} to {levels.Max() - mean:F2} dB about its mean, {most} voices at most, heard at most {worst:F0}° off north");
        Assert.True(most > 2, "the shore was not cut");
        Assert.InRange(levels.Max() - mean, 0, 1.5);
        Assert.InRange(levels.Min() - mean, -1.5, 0);
        Assert.True(worst < 30f, $"heard {worst:F0}° off the water");
    }
}
