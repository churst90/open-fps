using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Client.Core;
using OpenFPS.Models;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;
using OpenFPS.Materials;

namespace OpenFPS.Tests;

/// <summary>
/// A sound heard from several places is heard where each place is now (Resonance's list of 2026-10-17, item 2.3: its
/// river's far places were traced every few rounds and kept the old answer's direction between, so walking one way
/// the river was behind and walking back ahead). Here a line cut at the ear places every piece each frame from the
/// ear of that frame (AudioWorld.PlacePiece), and a piece's path is its middle's, carried for its tone only: where a
/// place is heard from is its own point, never an answer's.
/// </summary>
public class ResonanceMaybesPlacesTests
{
    private readonly ITestOutputHelper _o;
    public ResonanceMaybesPlacesTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    /// <summary>
    /// Walking at 1.4 m/s 1.5 m beside a 60 m creek, 40 m along it and back, every voice of it, every frame, is placed
    /// where its own piece is heard from by that frame's ear (ExtendedSources.Place over its piece's edges): within a
    /// centimetre, its bearing within its piece's ends, and none left where an earlier frame put it.
    /// Measured 2026-10-17: 3,430 frames, 12 pieces, 41,160 voice-frames, the largest distance from its piece's place
    /// 0.000 m. Placed from the ear the line was last cut at instead (what a reused answer would do), 31,147 were left
    /// where an earlier frame put them, up to 0.069 m off.
    /// </summary>
    [Fact]
    public void Walking_beside_a_creek_every_piece_is_heard_from_where_it_is_this_frame()
    {
        const int Creek = 700;
        var line = new[] { new Vector3(-30f, 0.05f, 0f), new Vector3(30f, 0.05f, 0f) };
        var position = new Vector3(0f, 0.05f, 0f);
        var h = new ClientAudioHarness();
        var def = new EntityDefinition
        {
            EntityId = Creek,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "flow:creek";
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 60f;
        def.SoundEmitter.Line = line.Select(p => p - position).ToArray();
        h.World.RegisterDefinition(def);
        var path = WideSources.PathOf(position, Quaternion.Identity, def.SoundEmitter.Line,
                                      h.Facade.Options.WideSources.LineLength("flow:creek", ModelLibrary.Presets),
                                      h.Facade.Options.WideSources.LineWidth("flow:creek", ModelLibrary.Presets));

        float eye = LocalPlayerState.StandingEyeHeight;
        float step = (float)(1.4 / ClientAudioSystem.UpdateHz);
        var walk = new List<float>();
        for (float x = -20f; x <= 20f; x += step) walk.Add(x);
        for (float x = 20f; x >= -20f; x -= step) walk.Add(x);
        h.StandAt(new Vector3(walk[0], 0f, 1.5f));
        h.Tick(240);

        int frames = 0, most = 0, stale = 0, outside = 0, checkedVoices = 0;
        float worst = 0f, worstBearing = 0f;
        var before = new Dictionary<int, Vector3>();
        foreach (float x in walk)
        {
            var feet = new Vector3(x, 0f, 1.5f);
            h.StandAt(feet);
            h.Tick();
            frames++;
            var ear = feet + new Vector3(0f, eye, 0f);
            int middle = h.Voice(VoiceKind.Machine, Creek);
            Assert.True(h.Mixer.Latest.TryGetValue(middle, out var m), "the creek has no voice");
            Assert.True(h.Mixer.Pieces.TryGetValue(middle, out var cut), "the creek was not cut");
            most = Math.Max(most, cut.Count);
            for (int k = 0; k < cut.Count; k++)
            {
                int id = k == 0 ? middle : h.Voice(VoiceKind.Place, Creek, k);
                if (!h.Mixer.Live.Contains(id) || !h.Mixer.Latest.TryGetValue(id, out var e)) continue;
                checkedVoices++;
                int piece = cut.PieceOfPlace(k);
                var want = ExtendedSources.Place(path, ear, cut.Edges[piece], cut.Edges[piece + 1], m.MinDistance).At;
                worst = MathF.Max(worst, Vector3.Distance(e.ApparentPosition, want));
                // Its bearing on the flat lies between its piece's two ends, as seen from here.
                float b = Bearing(e.ApparentPosition - ear);
                float b0 = Bearing(path.PointAt(cut.Edges[piece]) - ear), b1 = Bearing(path.PointAt(cut.Edges[piece + 1]) - ear);
                float span = Off(b0, b1), off = MathF.Max(Off(b, b0), Off(b, b1)) - span;
                if (off > 0.5f) outside++;
                worstBearing = MathF.Max(worstBearing, Off(Bearing(e.ApparentPosition - ear), Bearing(want - ear)));
                // Left where an earlier frame put it while its piece's place moved.
                if (before.TryGetValue(id, out var was) && was == e.ApparentPosition && Vector3.Distance(was, want) > 1e-3f) stale++;
                before[id] = e.ApparentPosition;
            }
        }
        _o.WriteLine($"{frames} frames beside the creek and back, {checkedVoices} voice-frames, up to {most} pieces: " +
                     $"furthest from its piece's place {worst:F3} m ({worstBearing:F2}°), {outside} outside their piece, {stale} left behind");
        Assert.True(most >= 4, $"cut into {most} at most");
        Assert.True(worst < 0.01f, $"a voice {worst:F3} m from its piece's place ({worstBearing:F2}°)");
        Assert.Equal(0, outside);
        Assert.Equal(0, stale);
    }

    private static float Bearing(Vector3 to) => MathF.Atan2(to.X, to.Z) * 180f / MathF.PI;

    private static float Off(float a, float b) => MathF.Abs(((a - b) % 360f + 540f) % 360f - 180f);
}
