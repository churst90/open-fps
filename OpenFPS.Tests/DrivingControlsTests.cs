using Arch.Core;
using MemoryPack;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Systems;

namespace OpenFPS.Tests;

/// <summary>
/// The horn and the siren from the driver's seat: H held is the horn for as long as it is held, U is
/// the siren switch and Shift+U its tone. Everybody hears both from the vehicle's state, so these hold
/// the switch, the byte on the wire and the voice's key to what the driver did.
/// </summary>
public class DrivingControlsTests
{
    private static EntityState RoundTrip(EntityState s)
    {
        var list = new List<EntityState>();
        StatePacking.Unpack(StatePacking.Pack(new List<EntityState> { s }, 0, 1), list);
        return Assert.Single(list);
    }

    [Fact]
    public void TheSignalsByteComesBackAndIsAbsentWhenZero()
    {
        var s = new EntityState { EntityId = 7, Signals = VehicleSignalBits.Encode(true, true, true, SirenMode.Yelp) };
        var back = RoundTrip(s);
        Assert.Equal(s.Signals, back.Signals);
        Assert.True(VehicleSignalBits.HornHeld(back.Signals));
        Assert.Equal(SirenMode.Yelp, VehicleSignalBits.Siren(back.Signals));

        var plain = new EntityState { EntityId = 7 };
        int withBytes = StatePacking.Pack(new List<EntityState> { s }, 0, 1).Length;
        int without = StatePacking.Pack(new List<EntityState> { plain }, 0, 1).Length;
        Assert.Equal(without + 1, withBytes);
        Assert.Equal(0, RoundTrip(plain).Signals);
    }

    [Theory]
    [InlineData(SirenMode.Wail)]
    [InlineData(SirenMode.Yelp)]
    [InlineData(SirenMode.Phaser)]
    [InlineData(SirenMode.HiLo)]
    public void EveryToneSurvivesTheByte(SirenMode tone)
    {
        byte on = VehicleSignalBits.Encode(true, false, true, tone);
        Assert.Equal(tone, VehicleSignalBits.Siren(on));
        Assert.True(VehicleSignalBits.IsManual(on));
        Assert.False(VehicleSignalBits.HornHeld(on));
        Assert.Equal(SirenMode.Off, VehicleSignalBits.Siren(VehicleSignalBits.Encode(true, false, false, tone)));
    }

    [Fact]
    public void AHornPressedOnAParkedCarIsSentAlthoughNothingMoved()
    {
        var sent = new Dictionary<int, SentState>();
        var s = new EntityState { EntityId = 3 };
        for (long t = 0; t < 40; t++) { var c = s; RestingStates.ShouldSend(sent, ref c, t, false); }
        var quiet = s;
        Assert.False(RestingStates.ShouldSend(sent, ref quiet, 41, false));
        var horn = s;
        horn.Signals = VehicleSignalBits.Encode(true, true, false, SirenMode.Wail);
        Assert.True(RestingStates.ShouldSend(sent, ref horn, 42, false));
        var released = s;
        for (long t = 43; t < 50; t++) { var c = horn; RestingStates.ShouldSend(sent, ref c, t, false); }
        Assert.True(RestingStates.ShouldSend(sent, ref released, 50, false));
    }

    [Fact]
    public void TheHornKeyCrossesTheWire()
    {
        var input = new ClientInputUpdate { SequenceId = 5, Horn = true, Sprint = true };
        var bytes = MemoryPackSerializer.Serialize<IMessage>(input);
        var back = Assert.IsType<ClientInputUpdate>(MemoryPackSerializer.Deserialize<IMessage>(bytes));
        Assert.True(back.Horn);
        Assert.True(back.Sprint);
    }

    [Fact]
    public void TheHornSoundsWhileHeldAndLetsGoWhenThePacketsStop()
    {
        int id = 880_001;
        VehicleSignals.Horn(id, true);
        Assert.True(VehicleSignals.HornSounding(id));
        // Packets every tick keep it going.
        for (int i = 0; i < 30; i++) { VehicleSignals.Horn(id, true); VehicleSignals.Update(1f / 30f); }
        Assert.True(VehicleSignals.HornSounding(id));
        // Key up.
        VehicleSignals.Horn(id, false);
        Assert.False(VehicleSignals.HornSounding(id));
        // Down, then the driver goes quiet: let go within the hold time.
        VehicleSignals.Horn(id, true);
        for (int i = 0; i < 12; i++) VehicleSignals.Update(1f / 30f);
        Assert.False(VehicleSignals.HornSounding(id));
        // A tap from a text session blows for half a second.
        VehicleSignals.Tap(id);
        for (int i = 0; i < 10; i++) VehicleSignals.Update(1f / 30f);
        Assert.True(VehicleSignals.HornSounding(id));
        for (int i = 0; i < 10; i++) VehicleSignals.Update(1f / 30f);
        Assert.False(VehicleSignals.HornSounding(id));
    }

    [Fact]
    public void TheSirenSwitchStepsThroughItsTonesAndOnlyAPoliceCarHasOne()
    {
        var world = World.Create();
        try
        {
            var police = world.Create(new DriveComponent { Preset = "police_interceptor" });
            var hatch = world.Create(new DriveComponent { Preset = "i4_economy" });
            var walker = world.Create(new Transform());

            Assert.Equal("This vehicle has no siren.", VehicleSignals.SirenCommand(world, hatch, Array.Empty<string>()));
            Assert.Equal("Siren on, wail.", VehicleSignals.SirenCommand(world, police, Array.Empty<string>()));
            Assert.Equal("Siren on, yelp.", VehicleSignals.SirenCommand(world, police, new[] { "next" }));
            Assert.Equal("Siren on, phaser.", VehicleSignals.SirenCommand(world, police, new[] { "next" }));
            Assert.Equal("Siren on, wail.", VehicleSignals.SirenCommand(world, police, new[] { "next" }));
            Assert.Equal("Siren on, hi-lo.", VehicleSignals.SirenCommand(world, police, new[] { "hilo" }));

            byte b = VehicleSignals.WireByte(world, police);
            Assert.True(VehicleSignalBits.IsManual(b));
            Assert.Equal(SirenMode.HiLo, VehicleSignalBits.Siren(b));
            Assert.Equal("Siren off.", VehicleSignals.SirenCommand(world, police, new[] { "off" }));
            Assert.Equal(SirenMode.Off, VehicleSignalBits.Siren(VehicleSignals.WireByte(world, police)));

            // Anything somebody can drive is marked as the driver's, siren or not, so the client never
            // runs traffic's siren logic on it; anything else sends nothing at all.
            Assert.True(VehicleSignalBits.IsManual(VehicleSignals.WireByte(world, hatch)));
            Assert.Equal(0, VehicleSignals.WireByte(world, walker));
        }
        finally { World.Destroy(world); }
    }

    [Fact]
    public void AHeldHornKeyIsOneBlastWithNoEnd()
    {
        string key = Honk.HoldKey(VehicleProfile.HornFor(MachineRegistry.VehicleFor("transit_bus")));
        Assert.Equal("horn:air:bus_horn:hold", key);
        Assert.True(Honk.TryParse(key, out string horn, out var pattern));
        Assert.Equal("air:bus_horn", horn);
        Assert.True(Honk.Held(pattern));
        Assert.True(Honk.BlowingAt(pattern, 0.01f));
        Assert.True(Honk.BlowingAt(pattern, 3600f));
        // An ordinary honk is still a rhythm.
        Assert.True(Honk.TryParse(Honk.Key("electric:disc_pair", new[] { 0.2f }), out _, out var tap));
        Assert.False(Honk.Held(tap));
    }

    [Fact]
    public void HornAndSirenCommandsAreInTheCatalogue()
    {
        Assert.Contains(CommandCatalog.All, e => e.Name == "horn");
        Assert.Contains(CommandCatalog.All, e => e.Name == "siren");
    }
}
