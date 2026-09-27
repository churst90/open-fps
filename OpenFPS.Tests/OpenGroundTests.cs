using System.Linq;
using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// What the trace from the listener's head is given: the map without its open ground. A voice close by
/// came back off the ground under the listener's own feet 10-15 ms late and sounded like a small room
/// (2026-09-27). The floor of a room, a roof and the walls must all stay.
/// </summary>
public class OpenGroundTests
{
    private static readonly Quaternion Q = Quaternion.Identity;

    [Fact]
    public void Roads_pavements_and_the_ground_are_left_out_and_rooms_are_not()
    {
        var ground = new SteamAudioScene.Box(new Vector3(0, -0.1f, 0), new Vector3(400, 0.2f, 400), Q, "Dirt");
        var road = new SteamAudioScene.Box(new Vector3(0, 0.025f, 0), new Vector3(12, 0.05f, 200), Q, "Asphalt");
        var pavement = new SteamAudioScene.Box(new Vector3(7.75f, 0.06f, 0), new Vector3(3.5f, 0.12f, 200), Q, "Concrete");
        // A house: a floor with a ceiling over it, walls, and a roof.
        var floor = new SteamAudioScene.Box(new Vector3(50, 0.1f, 50), new Vector3(10, 0.2f, 8), Q, "Wood");
        var ceiling = new SteamAudioScene.Box(new Vector3(50, 2.8f, 50), new Vector3(10, 0.2f, 8), Q, "Plaster");
        var wall = new SteamAudioScene.Box(new Vector3(45, 1.4f, 50), new Vector3(0.3f, 2.8f, 8), Q, "Brick");
        // A tower's roof: a slab with the sky over it, twenty metres up.
        var roof = new SteamAudioScene.Box(new Vector3(-50, 20f, 50), new Vector3(20, 0.3f, 20), Q, "Concrete");
        var all = new[] { ground, road, pavement, floor, ceiling, wall, roof };

        var kept = SteamAudioScene.WithoutOpenGround(all);

        Assert.DoesNotContain(ground, kept);
        Assert.DoesNotContain(road, kept);
        Assert.DoesNotContain(pavement, kept);
        Assert.Contains(floor, kept);
        Assert.Contains(ceiling, kept);
        Assert.Contains(wall, kept);
        Assert.Contains(roof, kept);
    }

    [Fact]
    public void A_road_through_a_tunnel_stays_where_it_is_roofed()
    {
        var q = Q;
        var road = new SteamAudioScene.Box(new Vector3(0, 0.025f, 0), new Vector3(12, 0.05f, 100), q, "Asphalt");
        var tunnelRoof = new SteamAudioScene.Box(new Vector3(0, 5.6f, 0), new Vector3(14, 0.3f, 100), q, "Concrete");
        Assert.Contains(road, SteamAudioScene.WithoutOpenGround(new[] { road, tunnelRoof }));
    }
}
