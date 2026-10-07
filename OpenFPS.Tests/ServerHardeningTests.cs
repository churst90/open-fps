using System.Net;
using System.Numerics;
using Arch.Core;
using MemoryPack;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// The hardening round of 2026-10-07 (docs/SERVER_SECURITY.md, "Hardening, 2026-10-07"): what a modified
/// client could send, and what the server does with it. Each test is one finding; each failed before its fix.
/// </summary>
public class ServerHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-hardening-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Clock
    {
        public long Ms = 1_000_000;
        public void Advance(double seconds) => Ms += (long)(seconds * 1000);
    }

    // ── Files: names that would leave their folder ─────────────────────────────────────────────────

    [Theory]
    [InlineData("../escaped")]
    [InlineData("../../escaped")]
    [InlineData("sub/escaped")]
    public void ADesignNameCannotLeaveTheDesignsFolder(string name)
    {
        string root = Path.Combine(_dir, "server");
        var maps = new MapManager(new MapRepository(Path.Combine(root, "maps")), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        Directory.CreateDirectory(Path.Combine(root, "maps"));
        maps.Initialize();
        Assert.True(maps.CreateMap(MapTemplates.Flat("mine", "tester"), out string e), e);
        var composites = new CompositeService(maps, maps.PrefabRepository, new CompositeRepository(Path.Combine(root, "composites")));
        var sessions = new SessionManager();
        var server = new GameServer(new WorldEditorTests.FakeUsers());
        server.Attach(maps, sessions);
        var commands = new CommandHandler(sessions, maps, server, composites);
        var feet = new Vector3(5, 0.05f, 5);
        Assert.True(maps.TryGetMap("mine", out var world, out _, out _, out _));
        var body = world.Create(new PlayerComponent { ConnectionId = 1, Username = "tester" }, EntityType.Player,
                                new Transform { Position = feet, Rotation = Quaternion.Identity }, new Velocity());
        maps.IndexEntity("mine", body);
        sessions.AddSession(1, new UserSession { ConnectionId = 1, Username = "tester", Role = UserRole.Player, CurrentMapId = "mine", Entity = body, Welcomed = true });
        for (int i = 0; i < 3; i++)
            maps.SpawnEntity("mine", w => maps.PrefabRepository.Spawn(w, "concrete_wall", feet + new Vector3(i * 1.4f - 1.4f, 0, 2), Quaternion.Identity, Vector3.One));
        Assert.True(composites.Group("mine", feet + new Vector3(0, 0, 2), 4f, "shed", false, "tester", out _) >= 0);

        var said = new List<string>();
        commands.HandleTextCommand(1, new TextCommand { Command = "saveas", Args = new[] { name } }, m => { if (m is TextEvent t) said.Add(t.Text); });
        server.DrainCommandBuffer();

        Assert.False(File.Exists(Path.Combine(root, "escaped.json")), string.Join(" | ", said));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.json")));
        Assert.False(File.Exists(Path.Combine(root, "composites", "sub", "escaped.json")));
        Assert.Contains("letters, digits", string.Join(" | ", said));
    }

    [Fact]
    public void TheDesignStoreRefusesAPathItself()
    {
        var repo = new CompositeRepository(Path.Combine(_dir, "composites"));
        Assert.ThrowsAny<ArgumentException>(() => repo.Save(new CompositeTemplate { Id = Path.Combine(_dir, "abs") }));
        Assert.False(File.Exists(Path.Combine(_dir, "abs.json")));
    }

    [Theory]
    [InlineData("abc\n", false)]
    [InlineData("abc", true)]
    [InlineData("../abc", false)]
    [InlineData("", false)]
    public void AModelIdIsAPlainNameWithNoNewlineAtTheEnd(string id, bool ok) => Assert.Equal(ok, ModelStore.IsSafeId(id));

    // ── Sound ids a visitor's client opens as a file ────────────────────────────────────────────

    [Theory]
    [InlineData(@"\\evil.example\share\ASSETS\x")]
    [InlineData("../../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:x")]
    [InlineData("SOUNDS/ASSETS/x")]
    public void APlayerCannotGiveAThingASoundIdThatIsAPathOutOfTheSoundsFolder(string id)
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.StartsWith("Placed", rig.Run("edit", "place", "concrete_wall"));
        foreach (var command in new[] { "set_sound", "play_folder", "start_state" })
        {
            string said = command == "start_state" ? rig.Run(command, id, id) : rig.Run(command, id, "1");
            Assert.Contains("sound", said);
        }
        var found = new List<string>();
        rig.World("mine").Query(new Arch.Core.QueryDescription().WithAll<SoundEmitterComponent>(), (ref SoundEmitterComponent em) => found.Add(em.SoundId ?? ""));
        Assert.DoesNotContain(id, found);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e30")]
    [InlineData("-1")]
    public void AVolumeIsAFiniteNumberFromZeroToFour(string volume)
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.StartsWith("Placed", rig.Run("edit", "place", "concrete_wall"));
        string said = rig.Run("set_sound", "AMBIENCE/woods_mid_day", volume);
        Assert.Contains("volume", said);
        var volumes = new List<float>();
        rig.World("mine").Query(new Arch.Core.QueryDescription().WithAll<SoundEmitterComponent>(), (ref SoundEmitterComponent em) => volumes.Add(em.Volume));
        Assert.All(volumes, v => Assert.True(float.IsFinite(v) && v >= 0 && v <= 4));
    }

    [Fact]
    public void APlainSoundIdIsStillTaken()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.StartsWith("Placed", rig.Run("edit", "place", "concrete_wall"));
        Assert.StartsWith("Attached sound", rig.Run("set_sound", "AMBIENCE/woods_mid_day", "0.8"));
    }

    [Theory]
    [InlineData(@"\\evil.example\share\ASSETS\x.wav")]
    [InlineData("//evil.example/share/ASSETS/x.wav")]
    [InlineData("../../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    public void TheClientOpensNoSoundFileOutsideItsSoundsFolder(string id)
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS"));
        string path = OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.SoundFilePath(id);
        Assert.True(path.Length == 0 || Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal), path);
    }

    // ── The editor: what a player may place, and the cap ────────────────────────────────────────

    /// <summary>A thing on the tester's map that the editor can take hold of, as staff would have put it there.</summary>
    private static int PutThere(WorldEditorTests.Rig rig, string prefab, Vector3 at)
    {
        var e = rig.Maps.SpawnPrefab("mine", prefab, at, Quaternion.Identity, Vector3.One, null);
        Assert.NotEqual(Entity.Null, e);
        int id = 800_000_000 + e.Id;
        rig.Maps.AuthoredEntities("mine")[id] = e;
        return id;
    }

    [Theory]
    [InlineData("teleporter")]
    [InlineData("akm_rifle")]
    public void APlayerCannotCopyAPremiumItemOrAThingToCarry(string prefab)
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        int id = PutThere(rig, prefab, new Vector3(8, 0.5f, 8));
        Assert.StartsWith("Selected", rig.Run("edit", "select", "#" + id));
        int before = rig.Overlay("mine").Added.Count;
        Assert.DoesNotContain("Copied", rig.Run("edit", "duplicate"));
        Assert.DoesNotContain("A row", rig.Run("edit", "row", "5"));
        Assert.Equal(before, rig.Overlay("mine").Added.Count);
    }

    [Fact]
    public void APlayerCannotPlaceAGroupThatHoldsAThingToCarry()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        _ = rig.Editor.Catalog;
        rig.Server.Models.Commit(GroupKind.KindId, "kit", "{\"Name\":\"Kit\",\"Parts\":[{\"PrefabId\":\"concrete_wall\"},{\"PrefabId\":\"akm_rifle\",\"RightMetres\":2}]}", "dev", "test");
        string said = rig.Run("edit", "place", "group", "kit");
        Assert.DoesNotContain("Placed the group", said);
        Assert.Empty(rig.Overlay("mine").Added);
    }

    [Fact]
    public void APlayerCannotReplaceWallsWithTeleporters()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.StartsWith("Placed", rig.Run("edit", "place", "concrete_wall"));
        string said = rig.Run("edit", "model", "replace", "prefab", "concrete_wall", "with", "teleporter", "here");
        Assert.DoesNotContain("Replaced", said);
        Assert.DoesNotContain(rig.Overlay("mine").Added, a => a.Entity.PrefabId == "teleporter");
    }

    [Fact]
    public void UndoCannotTakeAnOwnersMapPastTheCap()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Maps.TryGetMapData("mine", out var mine);
        mine.Editors.Add("other");
        rig.Stand(rig.Other, "mine", new Vector3(-20, 0.05f, -20));
        rig.Editor.PlacedCap = 3;
        for (int i = 0; i < 3; i++)
        {
            rig.Stand(rig.Tester, "mine", new Vector3(5 + i * 3, 0.05f, 5));
            Assert.StartsWith("Placed", rig.Run("edit", "place", "concrete_wall"));
        }
        Assert.StartsWith("Deleted", rig.Run("edit", "delete"));
        Assert.Contains("Placed", rig.RunAs(rig.Other, "edit", "place", "concrete_wall"));
        Assert.Equal(3, rig.Overlay("mine").Added.Count);

        string said = rig.Run("edit", "undo");
        Assert.Equal(3, rig.Overlay("mine").Added.Count);
        Assert.Contains("Cannot undo", said);
    }

    [Fact]
    public void AThingCannotBeMovedFurtherThanTwentyKilometresOut()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.StartsWith("Placed", rig.Run("edit", "place", "concrete_wall"));
        int id = rig.Selected;
        for (int i = 0; i < 25; i++) rig.Run("edit", "move", "1000", "0", "0");
        Assert.True(rig.PoseOf(id).Position.X <= WorldEditor.MaxDistanceMetres, rig.PoseOf(id).Position.ToString());
    }

    [Fact]
    public void ARowIsWrittenToTheOverlayOnceNotOncePerThing()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.StartsWith("Placed", rig.Run("edit", "place", "concrete_wall"));
        int before = rig.Maps.Overlays!.Writes;
        Assert.StartsWith("A row of 20", rig.Run("edit", "row", "20", "2"));
        Assert.Equal(1, rig.Maps.Overlays.Writes - before);
    }

    [Fact]
    public void UndoingARowRefilesTheMapOnceNotOncePerThing()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.StartsWith("Placed", rig.Run("edit", "place", "concrete_wall"));
        Assert.StartsWith("A row of 20", rig.Run("edit", "row", "20", "2"));
        int before = rig.Maps.GridRefreshes;
        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.Equal(1, rig.Maps.GridRefreshes - before);
    }

    // ── Map data: once per manifest ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheMapIsSentOncePerManifestNotOncePerAsking()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Server.SendManifest(rig.Tester);
        for (int i = 0; i < 5; i++) rig.Server.MapDataAsked(rig.Tester, new MapDataRequest { MapName = "mine" });
        Assert.Single(rig.SentTo("tester"), m => m is MapLoadComplete);
        rig.Server.SendManifest(rig.Tester);
        rig.Server.MapDataAsked(rig.Tester, new MapDataRequest { MapName = "mine" });
        Assert.Equal(2, rig.SentTo("tester").Count(m => m is MapLoadComplete));
    }

    // ── The dispatcher: limits, and what text may be ──────────────────────────────────────────

    private static (MessageDispatcher Dispatcher, Clock Clock, List<IMessage> Handled, List<IMessage> Replies) Dispatcher()
    {
        var clock = new Clock();
        var d = new MessageDispatcher { Limits = new MessageLimits(() => clock.Ms), KeyOf = id => id is 1 or 2 ? "tester" : null };
        var handled = new List<IMessage>();
        var replies = new List<IMessage>();
        d.RegisterHandler<ChatMessage>((_, m, _) => handled.Add(m));
        d.RegisterHandler<TextCommand>((_, m, _) => handled.Add(m));
        d.RegisterHandler<VoiceData>((_, m, _) => handled.Add(m));
        d.RegisterHandler<InteractRequest>((_, m, _) => handled.Add(m));
        d.RegisterHandler<PlayerListRequest>((_, m, _) => handled.Add(m));
        return (d, clock, handled, replies);
    }

    [Fact]
    public void ChatIsSixLinesAtOnceThenOneEveryTwoSeconds()
    {
        var (d, clock, handled, replies) = Dispatcher();
        for (int i = 0; i < 20; i++) d.Dispatch(1, new ChatMessage { Text = "hello " + i }, replies.Add);
        Assert.Equal(6, handled.Count);
        Assert.Contains(replies, r => r is TextEvent { Text: MessageLimits.ChatTooFast });
        clock.Advance(2);
        d.Dispatch(1, new ChatMessage { Text = "again" }, replies.Add);
        Assert.Equal(7, handled.Count);
    }

    [Fact]
    public void TheLimitsAreTheAccountsSoASecondConnectionSharesThem()
    {
        var (d, _, handled, replies) = Dispatcher();
        for (int i = 0; i < 6; i++) d.Dispatch(1, new ChatMessage { Text = "a" }, replies.Add);
        d.Dispatch(2, new ChatMessage { Text = "b" }, replies.Add);
        Assert.Equal(6, handled.Count);
    }

    [Fact]
    public void AFloodOfVoiceIsDroppedWithoutAWord()
    {
        var (d, _, handled, replies) = Dispatcher();
        for (int i = 0; i < 1000; i++) d.Dispatch(1, new VoiceData { OpusData = new byte[100] }, replies.Add);
        Assert.InRange(handled.Count, 100, 101);
        Assert.Empty(replies);
    }

    [Fact]
    public void CommandsAreTwentyAtOnceThenFiveASecond()
    {
        var (d, clock, handled, replies) = Dispatcher();
        for (int i = 0; i < 50; i++) d.Dispatch(1, new TextCommand { Command = "scan" }, replies.Add);
        Assert.Equal(20, handled.Count);
        Assert.Contains(replies, r => r is TextEvent { Text: MessageLimits.CommandsTooFast });
        clock.Advance(1);
        for (int i = 0; i < 10; i++) d.Dispatch(1, new TextCommand { Command = "scan" }, replies.Add);
        Assert.Equal(25, handled.Count);
    }

    [Fact]
    public void AnEditCostsByHowMuchItChanges()
    {
        Assert.Equal(0, WorldEditor.EditCost(new[] { "menu", "root" }));
        Assert.Equal(0, WorldEditor.EditCost(new[] { "select", "nearest" }));
        Assert.Equal(1, WorldEditor.EditCost(new[] { "nudge", "north" }));
        Assert.Equal(6, WorldEditor.EditCost(new[] { "row", "50" }));
        Assert.Equal(5, WorldEditor.EditCost(new[] { "place", "group", "house" }));
        Assert.Equal(10, WorldEditor.EditCost(new[] { "model", "replace", "prefab", "a", "with", "b", "everywhere" }));

        var (d, clock, handled, replies) = Dispatcher();
        for (int i = 0; i < 4; i++) d.Dispatch(1, new TextCommand { Command = "edit", Args = new[] { "row", "50" } }, replies.Add);
        Assert.Equal(3, handled.Count);
        Assert.Contains(replies, r => r is TextEvent { Text: MessageLimits.EditsTooFast });
        // Looking costs nothing against the editor's limit.
        d.Dispatch(1, new TextCommand { Command = "edit", Args = new[] { "menu", "root" } }, replies.Add);
        Assert.Equal(4, handled.Count);
    }

    [Fact]
    public void TravelIsThreeMapsAtOnceThenOneEveryTenSeconds()
    {
        var (d, clock, handled, replies) = Dispatcher();
        for (int i = 0; i < 6; i++) d.Dispatch(1, new TextCommand { Command = "join", Args = new[] { "city" } }, replies.Add);
        Assert.Equal(3, handled.Count);
        Assert.Contains(replies, r => r is TextEvent { Text: MessageLimits.TravelTooFast });
        clock.Advance(10);
        d.Dispatch(1, new TextCommand { Command = "join", Args = new[] { "city" } }, replies.Add);
        Assert.Equal(4, handled.Count);
    }

    [Fact]
    public void ListsAndTheInteractKeyAreDroppedPastTheirLimit()
    {
        var (d, _, handled, replies) = Dispatcher();
        for (int i = 0; i < 100; i++) d.Dispatch(1, new InteractRequest { Action = "interact" }, replies.Add);
        for (int i = 0; i < 100; i++) d.Dispatch(1, new PlayerListRequest(), replies.Add);
        Assert.Equal(10, handled.Count);
    }

    [Fact]
    public void AFloodIsOneLogLineAMinuteNotOnePerMessage()
    {
        var (d, clock, _, replies) = Dispatcher();
        for (int i = 0; i < 500; i++) d.Dispatch(1, new VoiceData { OpusData = new byte[10] }, replies.Add);
        Assert.Equal(1, d.Limits.Abuse.Written);
        clock.Advance(61);
        for (int i = 0; i < 500; i++) d.Dispatch(1, new VoiceData { OpusData = new byte[10] }, replies.Add);
        Assert.Equal(2, d.Limits.Abuse.Written);
    }

    [Fact]
    public void ChatLosesControlCharactersAndReversingMarksAndIsCut()
    {
        var (d, _, handled, replies) = Dispatcher();
        d.Dispatch(1, new ChatMessage { Text = "hi\u001b[2Jthere\nforged log line\u202Eevil" }, replies.Add);
        d.Dispatch(1, new ChatMessage { Text = new string('a', 5000) }, replies.Add);
        var lines = handled.OfType<ChatMessage>().Select(c => c.Text).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, t => Assert.DoesNotContain(t, c => char.IsControl(c) || c == '\u202E'));
        Assert.Equal(SafeText.MaxChatChars, lines[1].Length);
    }

    [Fact]
    public void AnOverlongCommandIsRefusedAndItsWordsLoseControlCharacters()
    {
        var (d, _, handled, replies) = Dispatcher();
        d.Dispatch(1, new TextCommand { Command = "pm", Args = new[] { "sean", new string('x', 3000) } }, replies.Add);
        d.Dispatch(1, new TextCommand { Command = "pm", Args = Enumerable.Repeat("w", 500).ToArray() }, replies.Add);
        Assert.Empty(handled);
        Assert.Equal(2, replies.Count(r => r is TextEvent { Text: MessageLimits.CommandTooLong }));

        d.Dispatch(1, new TextCommand { Command = "pm", Args = new[] { "sean", "hi\u001b]0;pwned\u0007" } }, replies.Add);
        var cmd = Assert.IsType<TextCommand>(Assert.Single(handled));
        Assert.DoesNotContain(cmd.Args[1], c => char.IsControl(c));
    }

    // ── Reading what arrives ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheServersOwnMessagesAreDroppedBeforeTheyAreRead()
    {
        byte[] state = MemoryPackSerializer.Serialize<IMessage>(new ServerStateUpdate { Packed = new byte[] { 1, 2, 3 } });
        Assert.False(NetworkService.IsClientMessage(state[0]));
        byte[] roads = MemoryPackSerializer.Serialize<IMessage>(new MapRoads { Json = "{}" });
        Assert.False(NetworkService.IsClientMessage(roads[0]));
        foreach (var type in MessageDispatcher.SentByClients)
        {
            var sample = (IMessage)Activator.CreateInstance(type)!;
            Assert.True(NetworkService.IsClientMessage(MemoryPackSerializer.Serialize<IMessage>(sample)[0]), type.Name);
        }
    }

    public static IEnumerable<object[]> ClientSamples() => new IMessage[]
    {
        new ClientInputUpdate { SequenceId = 5, MoveDirection = Vector3.UnitX },
        new ChatMessage { Text = "hello there", Sender = "x" },
        new LoginRequest { Username = "sean", Password = "password1", Build = "abc" },
        new TextCommand { Command = "edit", Args = new[] { "nudge", "north", "0.5" } },
        new InteractRequest { Action = "interact", TargetEntityId = 4 },
        new VoiceData { OpusData = new byte[60], Sequence = 9 },
        new RegisterRequest { Username = "sean", Password = "password1" },
        new MapDataRequest { MapName = "city", FullDetailMetres = 100 },
        new MapListRequest { Scope = MapListScope.Mine }, new ScopedShot { Yaw = 1 }, new InventoryRequest(),
        new PlayerListRequest(), new FriendListRequest(), new LogoutRequest(),
        new InteractRequest { Action = "interact", TargetEntityId = null },
        new TextCommand { Command = "pm", Args = new[] { "sean", "héllo wörld", "" } },
        new ChatMessage { Text = "ünïcode text", To = null!, Channel = ChatChannel.All },
    }.Select(m => new object[] { m.GetType().Name, MemoryPackSerializer.Serialize(m) });

    [Theory]
    [MemberData(nameof(ClientSamples))]
    public void AMangledMessageNeverThrowsAndNeverAllocatesMuch(string name, byte[] good)
    {
        var random = new Random(name.Sum(c => c));
        long worst = 0;
        for (int round = 0; round < 3000; round++)
        {
            var bytes = (byte[])good.Clone();
            if (round % 3 == 0 && bytes.Length > 5)
            {
                // A length or count read as huge: every int after the tag, in turn.
                int at = 1 + round / 3 % Math.Max(1, bytes.Length - 4);
                BitConverter.TryWriteBytes(bytes.AsSpan(at), round % 2 == 0 ? int.MaxValue : int.MinValue + 7);
            }
            else
            {
                int flips = 1 + random.Next(4);
                for (int f = 0; f < flips; f++) bytes[1 + random.Next(Math.Max(1, bytes.Length - 1)) % Math.Max(1, bytes.Length)] = (byte)random.Next(256);
                if (round % 7 == 0) Array.Resize(ref bytes, random.Next(1, bytes.Length + 1));
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            NetworkService.TryDecode(bytes, out _, out _);
            worst = Math.Max(worst, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.True(worst < 1_000_000, $"{name}: one mangled message allocated {worst:N0} bytes");
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MinValue + 1)]
    [InlineData(int.MinValue + 2)]
    [InlineData(int.MinValue + 3)]
    public void AStringLengthThatOverflowsIsRefusedNotReadPastTheBuffer(int header)
    {
        // MemoryPack asks for ~header + 4 bytes, which overflows to a negative size and passes its bounds
        // check: before the wire check this read 2 GB past the buffer and the process died
        // (AccessViolationException), from one LoginRequest sent before logging in.
        foreach (var sample in new IMessage[] { new LoginRequest { Username = "aaaa", Password = "bbbb" }, new ChatMessage { Text = "cccc" },
                                                 new TextCommand { Command = "dddd", Args = new[] { "eeee" } } })
        {
            byte[] good = MemoryPackSerializer.Serialize<IMessage>(sample);
            for (int at = 2; at + 4 <= good.Length; at++)
            {
                var bytes = (byte[])good.Clone();
                BitConverter.TryWriteBytes(bytes.AsSpan(at), header);
                if (WireCheck.Walked(bytes) >= 0 && !WireCheck.IsSafe(bytes)) continue;
                NetworkService.TryDecode(bytes, out _, out _);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ClientSamples))]
    public void TheWireCheckWalksEveryClientMessageExactly(string name, byte[] good)
    {
        Assert.True(WireCheck.IsSafe(good), name);
        int walked = WireCheck.Walked(good);
        // A type with no strings is not walked; one with strings must be walked to its last byte, or the
        // layout is not MemoryPack's.
        Assert.True(walked == -1 || walked == good.Length, $"{name}: walked {walked} of {good.Length} bytes");
        Assert.True(NetworkService.TryDecode(good, out var back, out var problem), problem);
        Assert.Equal(name, back!.GetType().Name);
    }

    // ── Fragments: what LiteNetLib would hold before any message is read ───────────────────────

    /// <summary>A reliable fragment as LiteNetLib puts it on the wire (NetPacket, v1.2.0).</summary>
    private static byte[] Fragment(ushort id, ushort part, ushort total, int payload = 100)
    {
        var p = new byte[10 + payload];
        p[0] = 1 | 0x80;                       // Channeled, fragmented
        BitConverter.TryWriteBytes(p.AsSpan(1), (ushort)7); // sequence
        p[3] = 0;                              // channel
        BitConverter.TryWriteBytes(p.AsSpan(4), id);
        BitConverter.TryWriteBytes(p.AsSpan(6), part);
        BitConverter.TryWriteBytes(p.AsSpan(8), total);
        return p;
    }

    private static int Through(FragmentGuard guard, byte[] packet, string from = "192.0.2.1:5000")
    {
        var ep = IPEndPoint.Parse(from);
        var data = packet;
        int length = packet.Length;
        guard.ProcessInboundPacket(ref ep, ref data, ref length);
        return length;
    }

    [Fact]
    public void AFragmentClaimingSixtyFiveThousandPartsIsDropped()
    {
        var guard = new FragmentGuard();
        Assert.Equal(0, Through(guard, Fragment(1, 0, ushort.MaxValue)));
        Assert.Equal(0, Through(guard, Fragment(2, 0, FragmentGuard.MaxFragments + 1)));
        Assert.Equal(0, Through(guard, Fragment(3, 5, 4)));
        Assert.Equal(0, Through(guard, Fragment(3, 0, 0)));
        var ordinary = Fragment(4, 0, 12);
        Assert.Equal(ordinary.Length, Through(guard, ordinary));
    }

    [Fact]
    public void OnlyAFewMessagesMayBeHalfArrivedAtOnce()
    {
        var guard = new FragmentGuard();
        for (ushort id = 0; id < FragmentGuard.MaxOpenGroups; id++) Assert.NotEqual(0, Through(guard, Fragment(id, 0, 4)));
        Assert.Equal(0, Through(guard, Fragment(100, 0, 4)));
        // Another sender has its own.
        Assert.NotEqual(0, Through(guard, Fragment(100, 0, 4), "192.0.2.2:5000"));
        // A message that finishes makes room.
        for (ushort part = 1; part < 4; part++) Assert.NotEqual(0, Through(guard, Fragment(0, part, 4)));
        Assert.NotEqual(0, Through(guard, Fragment(101, 0, 4)));
        // So does the connection closing.
        guard.Forget(IPEndPoint.Parse("192.0.2.1:5000"));
        Assert.NotEqual(0, Through(guard, Fragment(102, 0, 4)));
    }

    [Fact]
    public void ABadFragmentInsideAMergedPacketIsDroppedToo()
    {
        var guard = new FragmentGuard();
        var bad = Fragment(1, 0, ushort.MaxValue, 20);
        var merged = new byte[1 + 2 + bad.Length];
        merged[0] = 12;                        // Merged
        BitConverter.TryWriteBytes(merged.AsSpan(1), (ushort)bad.Length);
        bad.CopyTo(merged, 3);
        Assert.Equal(0, Through(guard, merged));
        // A merged part that says it is longer than what arrived reaches into an old buffer: dropped.
        BitConverter.TryWriteBytes(merged.AsSpan(1), (ushort)(bad.Length + 50));
        Assert.Equal(0, Through(guard, merged));
    }

    [Fact]
    public void ARealClientsMessagesStillArriveThroughTheGuard()
    {
        // Real LiteNetLib packets over the loopback: small ones merged into one datagram, and one big
        // enough to come in fragments.
        var server = new NetworkService();
        server.Start(0);
        var client = new OpenFPS.Client.Core.ClientNetworkService();
        client.Start();
        try
        {
            client.Connect("127.0.0.1", server.LocalPort);
            Assert.True(Pump(() => client.IsConnected), "the client never connected");
            for (int i = 0; i < 20; i++) client.Send(new TextCommand { Command = "scan" });
            client.Send(new ChatMessage { Text = new string('x', 8000) });
            client.Flush();
            var got = new List<IMessage>();
            Assert.True(Pump(() =>
            {
                while (server.TryDequeueMessage(out var item)) got.Add(item.message);
                return got.Count >= 21;
            }), $"{got.Count} of 21 messages arrived");
            Assert.Equal(8000, got.OfType<ChatMessage>().Single().Text.Length);
        }
        finally
        {
            client.Disconnect();
            server.Stop();
        }

        bool Pump(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                server.PollEvents();
                client.Poll();
                if (done()) return true;
                Thread.Sleep(5);
            }
            return done();
        }
    }

    [Fact]
    public void AMalformedLoginOverTheWireIsDroppedAndTheServerCarriesOn()
    {
        // Before the wire check this one packet ended the test host (AccessViolationException in MemoryPack).
        var server = new NetworkService();
        server.Start(0);
        var listener = new LiteNetLib.EventBasedNetListener();
        var client = new LiteNetLib.NetManager(listener) { AutoRecycle = true };
        client.Start();
        try
        {
            var peer = client.Connect("127.0.0.1", server.LocalPort, "OpenFPS_Key");
            Assert.True(Pump(() => peer.ConnectionState == LiteNetLib.ConnectionState.Connected), "no connection");
            byte[] bad = MemoryPackSerializer.Serialize<IMessage>(new LoginRequest { Username = "aaaa", Password = "bbbb" });
            BitConverter.TryWriteBytes(bad.AsSpan(2), int.MinValue);
            peer.Send(bad, LiteNetLib.DeliveryMethod.ReliableOrdered);
            peer.Send(MemoryPackSerializer.Serialize<IMessage>(new TextCommand { Command = "scan" }), LiteNetLib.DeliveryMethod.ReliableOrdered);
            var got = new List<IMessage>();
            Assert.True(Pump(() =>
            {
                while (server.TryDequeueMessage(out var item)) got.Add(item.message);
                return got.Count >= 1;
            }));
            Assert.IsType<TextCommand>(Assert.Single(got));
        }
        finally
        {
            client.Stop();
            server.Stop();
        }

        bool Pump(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                server.PollEvents();
                client.PollEvents();
                if (done()) return true;
                Thread.Sleep(5);
            }
            return done();
        }
    }

    // ── Logs ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMudRegistrationNeverReachesTheLogWithItsPassword()
    {
        Assert.DoesNotContain("hunter22", MudGateway.Redact("register sean hunter22"));
        Assert.DoesNotContain("hunter22", MudGateway.Redact("REGISTER sean hunter22"));
    }
}
