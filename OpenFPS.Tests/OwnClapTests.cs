using System.Diagnostics;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// "When I'm walking sidewalks and clapping, the clap sounds off to the side where I just came from"
/// (Cody, 2026-10-03). The server places a clap where it last had the body; walking, that is a step
/// behind the client. A sound made on a body is placed on the body as it is now.
/// </summary>
public class OwnClapTests
{
    [Fact]
    public void YourOwnClapIsOnYouWhereYouAreNow()
    {
        var provider = new SteelDoorSoundTests.CapturingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeForTest();
        var player = new WorldAudioPlayer(facade, new OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics(new SpatialService()));
        // The client has you 1.4 m further along than the server did, facing east (+x).
        var feet = new Vector3(11.4f, 0f, 5f);
        var east = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        player.SelfId = 7;
        player.Self = () => (feet, east);
        var ear = feet + new Vector3(0, 1.7f, 0);
        facade.UpdateListener(ear, east, Vector3.Zero, -1);

        var clap = new TransientSound
        {
            Character = SoundCharacter.Knock, LevelDb = Applause.SingleClapDb, SynthKey = Applause.ClapKey,
            DecaySeconds = 0.15f, Noisiness = 1f,
            Position = new Vector3(10f, 1.25f, 5f) + new Vector3(0.3f, 0f, 0f),     // where the server had you
            OnBody = true, BodyOffset = new Vector3(0f, 1.25f, 0.3f),
        };
        var client = new ClientWorldState();
        client.Clear(new Vector3(100, 20, 100));
        var clock = Stopwatch.StartNew();
        player.Receive(new WorldAudioEvent { SourceEntityId = 7, Label = "clap", Sounds = new() { clap }, Seed = 1 }, clock.Elapsed.TotalSeconds);
        // Rendered off the game thread: up to ten seconds for it, because a full test run on a busy
        // machine took longer than one and the test reported a clap that was simply still rendering.
        double until = clock.Elapsed.TotalSeconds + 10.0;
        while (clock.Elapsed.TotalSeconds < until && provider.Emitters.Count == 0)
        {
            player.Update(client.GetSnapshot(), ear, clock.Elapsed.TotalSeconds);
            facade.PumpForTest();
            System.Threading.Thread.Sleep(5);
        }
        var played = provider.Emitters.Find(e => e.SoundId.Contains("clap") && !e.IsReflection);
        Assert.NotNull(played.SoundId);
        // In front of your chest, where you are now: 0.3 m east of your feet, 1.25 m up.
        Assert.True(Vector3.Distance(played.Position, new Vector3(11.7f, 1.25f, 5f)) < 0.01f, $"played at {played.Position}");
    }
}
