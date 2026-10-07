using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A sustained recording's copies off the walls (ClientAudioSystem, the reflection voices) keep one
/// wall to one voice. The voice was the copy's place in the list of the four loudest arrivals sorted by
/// surface, so when a wall joined or left that list every wall after it moved to the next voice, and
/// that voice jumped from one wall's image to another's (probable bug 7 of 2026-10-07: the surface's
/// id, AcousticPathData.ReflectionId, was written and never read).
/// </summary>
public class ReflectionSlotTests
{
    private readonly ITestOutputHelper _o;
    public ReflectionSlotTests(ITestOutputHelper o) => _o = o;

    private const int Speaker = 50;

    private static void Solid(ClientAudioHarness h, int id, Vector3 centre, Vector3 size, string material)
    {
        var def = new EntityDefinition
        {
            EntityId = id, Type = EntityType.StaticObject,
            Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true };
        def.Material.Material = material;
        h.World.RegisterDefinition(def);
    }

    /// <summary>A walled yard 30 m square, the walls far enough off for each copy to be an event of its
    /// own (EarlyReflections.FusionSeconds), and a PA speaker playing in it.</summary>
    private static ClientAudioHarness Yard()
    {
        var h = new ClientAudioHarness();
        Solid(h, 1, new Vector3(0f, -0.25f, 0f), new Vector3(60f, 0.5f, 60f), "Asphalt");
        Solid(h, 2, new Vector3(15.25f, 3f, 0f), new Vector3(0.5f, 6f, 31f), "Concrete");
        Solid(h, 3, new Vector3(-15.25f, 3f, 0f), new Vector3(0.5f, 6f, 31f), "Concrete");
        Solid(h, 4, new Vector3(0f, 3f, 15.25f), new Vector3(31f, 6f, 0.5f), "Concrete");
        Solid(h, 5, new Vector3(0f, 3f, -15.25f), new Vector3(31f, 6f, 0.5f), "Concrete");
        var def = new EntityDefinition
        {
            EntityId = Speaker, Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(3f, 2f, 4f), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.SoundEmitter = new SoundEmitterComponent();
        def.SoundEmitter.SoundId = "ANNOUNCE/st_louis_welcome";
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.Range = 400f;
        def.SoundEmitter.MinDistance = 12f;
        h.World.RegisterDefinition(def);
        return h;
    }

    private static int VoiceOf(int slot) => -30000 - Speaker * (EarlyReflections.MaxArrivals + 1) - slot;

    [Fact]
    public void AWallsCopyStaysOnItsOwnVoiceAsYouWalk()
    {
        var h = Yard();
        h.StandAt(new Vector3(-12f, 0f, -12f));
        Assert.True(h.TickUntil(() => h.Mixer.Live.Contains(Speaker), 60), "the speaker never played");

        // Each copy's image is fixed (a fixed source in a fixed wall), so a voice whose image moves while it
        // plays has been handed to another wall.
        var at = new Dictionary<int, (int Starts, Vector3 Image)>();
        int jumps = 0, voices = 0;
        var seen = new HashSet<int>();
        for (int step = 0; step <= 96; step++)
        {
            float t = step / 96f;
            // Round the yard, inside the walls: past every wall in turn.
            float a = t * MathF.Tau;
            h.StandAt(new Vector3(11f * MathF.Cos(a), 0f, 11f * MathF.Sin(a)));
            for (int f = 0; f < 3; f++)
            {
                h.Tick();
                Thread.Sleep(2);
                for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++)
                {
                    int id = VoiceOf(slot);
                    if (!h.Mixer.Live.Contains(id) || !h.Mixer.Latest.TryGetValue(id, out var e)) continue;
                    if (seen.Add(id)) voices++;
                    int starts = h.Mixer.Started.Count(s => s.EntityId == id);
                    if (at.TryGetValue(id, out var was) && was.Starts == starts && Vector3.Distance(was.Image, e.Position) > 0.5f)
                    {
                        jumps++;
                        _o.WriteLine($"step {step}: voice {id} moved from {was.Image} to {e.Position} while playing");
                    }
                    at[id] = (starts, e.Position);
                }
            }
        }
        _o.WriteLine($"{voices} reflection voice(s) used, {jumps} jump(s) between walls");
        Assert.True(voices > 0, "no wall answered: the yard is not doing what the test needs");
        Assert.Equal(0, jumps);
    }
}
