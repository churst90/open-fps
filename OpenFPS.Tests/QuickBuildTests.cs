using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>
/// The quick build (docs/WORLD_EDITOR.md section 15): /edit build places a floor, wall, roof, door, window
/// or prefab at the size asked; a door or window is fitted into the wall in front of you; the Control+B
/// dialog is made from the server's form and builds the same command.
/// </summary>
public class QuickBuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-build-" + Guid.NewGuid().ToString("N"));
    private const string Denied = "You do not have permission to execute this command.";

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Rig Mine(string dir)
    {
        var rig = new Rig(dir, UserRole.Player);
        rig.On("mine");
        return rig;
    }

    private static (Vector3 Size, Pose Pose, string Prefab) Piece(Rig rig, int id)
    {
        var world = rig.World("mine");
        var e = rig.Thing(id);
        return (world.Get<ColliderComponent>(e).Size, rig.PoseOf(id), world.Get<IdentityComponent>(e).PrefabId);
    }

    private static void Near(Vector3 want, Vector3 got) => Assert.True(Vector3.Distance(want, got) < 1e-3f, $"wanted {want}, got {got}");

    // ── Pieces at a size ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFloorIsTheSizeAskedWithItsTopAtYourFeet()
    {
        var rig = Mine(_dir);
        Assert.Equal("Placed: floor, 6 by 8 metres, concrete, at your feet.",
                     rig.Run("edit", "build", "floor", "width", "6", "length", "8", "material", "Concrete", "here"));
        var (size, pose, prefab) = Piece(rig, rig.Selected);
        Assert.Equal("concrete_floor", prefab);
        Near(new Vector3(6, 0.1f, 8), size);
        Near(new Vector3(5, 0.05f - 0.05f, 5), pose.Position);
    }

    [Fact]
    public void AWallStandsInFrontOfYouFacingEachWay()
    {
        var rig = Mine(_dir);
        Assert.Equal("Placed: wall, 6 metres long and 2.7 high, brick, 2 metres in front of you, facing north.",
                     rig.Run("edit", "build", "wall", "length", "6", "height", "2.7", "material", "brick", "ahead", "2"));
        var (size, pose, prefab) = Piece(rig, rig.Selected);
        Assert.Equal("brick_wall", prefab);
        // Brick's own thickness, since none was said.
        Near(new Vector3(6, 2.7f, 0.35f), size);
        Near(new Vector3(5, 0.05f + 1.35f, 5 + 2 + 0.175f), pose.Position);

        // Facing east while you face north: it runs away from you, its near end two metres ahead.
        Assert.EndsWith("facing east.", rig.Run("edit", "build", "wall", "length", "6", "facing", "east", "ahead", "2"));
        (size, pose, _) = Piece(rig, rig.Selected);
        Near(new Vector3(6, 2.7f, 0.35f), size);
        Near(new Vector3(5, 1.4f, 5 + 2 + 3), pose.Position);
        Assert.Equal(MathF.PI / 2f, WorldEditor.YawOf(pose.Rotation), 3);

        // You facing west: it is across your way, west of you, facing west.
        rig.World("mine").Get<PlayerComponent>(rig.Tester.Entity).Yaw = -MathF.PI / 2f;
        Assert.EndsWith("facing west.", rig.Run("edit", "build", "wall", "length", "4", "material", "Concrete", "ahead", "1"));
        (size, pose, _) = Piece(rig, rig.Selected);
        Near(new Vector3(4, 2.7f, 0.5f), size);
        Near(new Vector3(5 - 1 - 0.25f, 1.4f, 5), pose.Position);

        // South, as a compass word, and a roof over you.
        Assert.EndsWith("facing south.", rig.Run("edit", "build", "wall", "facing", "south", "here"));
        Assert.Equal("Placed: roof, 5 by 6 metres, asphalt, over you.",
                     rig.Run("edit", "build", "roof", "width", "5", "length", "6", "above", "2.7", "material", "Asphalt"));
        (size, pose, _) = Piece(rig, rig.Selected);
        Near(new Vector3(5, 0.2f, 6), size);
        Assert.Equal(0.05f + 2.7f + 0.1f, pose.Position.Y, 3);
    }

    [Fact]
    public void ABuiltWallIsKeptReloadedAndUndone()
    {
        var rig = Mine(_dir);
        rig.Run("edit", "build", "wall", "length", "6", "height", "3", "thickness", "0.2", "material", "Brick", "ahead", "2");
        int id = rig.Selected;
        var pose = rig.PoseOf(id);

        var maps = new MapManager(new MapRepository(rig.MapDir), rig.Prefabs)
        {
            Access = new MapAccessRepository(rig.AccessPath),
            Overlays = new MapOverlayStore(Path.Combine(rig.MapDir, "overlays")),
        };
        maps.Initialize();
        Assert.True(maps.TryGetMap("mine", out var world, out _, out _, out _));
        var again = maps.AuthoredEntities("mine")[id];
        Near(new Vector3(6, 3, 0.2f), world.Get<ColliderComponent>(again).Size);
        Near(pose.Position, world.Get<Transform>(again).Position);

        Assert.Equal("Undid: built Brick Wall.", rig.Run("edit", "undo"));
        Assert.False(rig.Exists(id));
        Assert.Equal("Redid: built Brick Wall.", rig.Run("edit", "redo"));
        Assert.True(rig.Exists(id));
    }

    [Fact]
    public void ABuiltWallStopsYouWalkingIntoIt()
    {
        var rig = Mine(_dir);
        rig.Run("edit", "build", "wall", "length", "6", "material", "Brick", "ahead", "1");
        rig.Maps.SyncGeometry("mine");
        Assert.True(rig.Maps.TryGetGeometry("mine", out var geometry));
        var end = Walk(geometry.World, new Vector3(5, 0.05f, 5), Vector3.UnitZ, 120);
        // The wall's near face is at 6 metres north; the body stops a radius short of it.
        Assert.True(end.Z < 6f - PhysicsConstants.PlayerRadius + 0.02f, $"walked to {end}");
        Assert.True(end.Z > 5.4f, $"stopped early at {end}");
    }

    private static Vector3 Walk(TriangleWorld world, Vector3 from, Vector3 direction, int ticks)
    {
        Vector3 pos = from, vel = Vector3.Zero;
        var all = new AcceptAll();
        var solids = new List<SolidRef>();
        for (int t = 0; t < ticks; t++)
        {
            float ground = world.Ground(pos, PhysicsConstants.PlayerRadius, PhysicsConstants.StepHeight, GeometryLayers.Ground, ref all, out _, out _);
            var ctx = new SharedMovementEngine.MovementContext
            {
                Position = pos, Velocity = vel, InputDirection = direction, DeltaTime = PhysicsConstants.FixedDeltaTime, GroundHeight = ground,
                Gravity = PhysicsConstants.Gravity, JumpForce = PhysicsConstants.JumpPower, Speed = PhysicsConstants.FootSpeed(false, float.MaxValue),
                PlayerRadius = PhysicsConstants.PlayerRadius, PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
                MapMin = new Vector3(-100, -100, -100), MapMax = new Vector3(100, 100, 100), Body = BodyShape.Capsule,
            };
            ctx.Grade = SharedMovementEngine.GradeAlong(world, ref all, pos, direction);
            SharedMovementEngine.GatherSolids(ctx, world, ref all, solids);
            var obstacles = new SharedMovementEngine.Obstacles(ReadOnlySpan<SharedMovementEngine.Collider>.Empty, world,
                                                               System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids));
            (pos, vel, _) = SharedMovementEngine.Step(ctx, obstacles, out _);
        }
        return pos;
    }

    [Fact]
    public void APrefabIsItsOwnSizeOrTheSizeAsked()
    {
        var rig = Mine(_dir);
        Assert.StartsWith("Placed: Generic Wall, 3 by 0.1 by 1 metre high", rig.Run("edit", "build", "prefab", "prefab", "wall_generic", "width", "3", "height", "1", "depth", "0.1"));
        Near(new Vector3(3, 1, 0.1f), Piece(rig, rig.Selected).Size);
        // A machine is the size a machine is.
        Assert.Equal("Placed: Fire, 1 metre in front of you, facing north.", rig.Run("edit", "build", "prefab", "prefab", "fire_pit", "width", "9"));
    }

    // ── Doors and windows fitted into a wall ────────────────────────────────────────────────────

    private static int BrickWallAhead(Rig rig)
    {
        rig.Run("edit", "build", "wall", "length", "6", "height", "2.7", "material", "Brick", "ahead", "1");
        return rig.Selected;
    }

    private static List<(int Id, Vector3 Size, Vector3 At)> Bricks(Rig rig)
    {
        var world = rig.World("mine");
        return rig.Maps.AuthoredEntities("mine")
            .Where(p => world.IsAlive(p.Value) && world.Has<IdentityComponent>(p.Value) && world.Get<IdentityComponent>(p.Value).PrefabId == "brick_wall")
            .Select(p => (p.Key, world.Get<ColliderComponent>(p.Value).Size, world.Get<Transform>(p.Value).Position))
            .ToList();
    }

    [Fact]
    public void ADoorIsFittedWhereYouFaceAndWorksAndOneUndoMendsTheWall()
    {
        var rig = Mine(_dir);
        int wall = BrickWallAhead(rig);
        Assert.StartsWith("Placed: door, 0.9 by 2.1 metres, knob, in Brick Wall,", rig.Run("edit", "build", "door"));
        int door = rig.Selected;
        Assert.False(rig.Exists(wall));

        // Left and right of the opening, full height, and the lintel over it; nothing under a door.
        var pieces = Bricks(rig);
        Assert.Equal(3, pieces.Count);
        Near(new Vector3(2.55f, 2.7f, 0.35f), pieces.Single(p => p.At.X < 4).Size);
        Near(new Vector3(2.55f, 2.7f, 0.35f), pieces.Single(p => p.At.X > 6).Size);
        var lintel = pieces.Single(p => MathF.Abs(p.At.X - 5) < 0.01f);
        Near(new Vector3(0.9f, 0.6f, 0.35f), lintel.Size);
        Near(new Vector3(5, 0.05f + 2.1f + 0.3f, 6.175f), lintel.At);

        // The leaf: lapping each jamb, on your floor, opening away from you, a doorway that swings.
        var (size, pose, prefab) = Piece(rig, door);
        Assert.Equal("door", prefab);
        Near(new Vector3(1.0f, 2.1f, 0.06f), size);
        Near(new Vector3(5, 0.05f + 1.05f, 6.175f), pose.Position);
        var world = rig.World("mine");
        var leaf = rig.Thing(door);
        Assert.True(world.Has<PortalComponent>(leaf));
        var front = Vector3.Transform(Vector3.UnitZ, pose.Rotation);
        Assert.True(front.Z < -0.99f, "a knob door is pushed from its front: its front faces you");
        rig.Stand(rig.Tester, "mine", new Vector3(-20, 0.05f, -20));
        var doors = new DoorSystem();
        doors.Update(world, 0.05f, _ => { });
        Assert.True(DoorSystem.Set(world, leaf, true));
        for (int i = 0; i < 80; i++) doors.Update(world, 0.05f, _ => { });
        Assert.Equal(1f, world.Get<DoorComponent>(leaf).Openness);

        Assert.Equal("Undid: fitted a door into Brick Wall.", rig.Run("edit", "undo"));
        Assert.True(rig.Exists(wall));
        Assert.False(rig.Exists(door));
        Assert.Single(Bricks(rig));
        Near(new Vector3(6, 2.7f, 0.35f), Bricks(rig)[0].Size);
        Assert.Single(rig.Overlay("mine").Added);
    }

    [Fact]
    public void AWindowIsCutAtItsSillAndKeptInsideTheWallsEnds()
    {
        var rig = Mine(_dir);
        BrickWallAhead(rig);
        // Looking a little right of the wall's end: the window is pushed back inside it.
        rig.World("mine").Get<PlayerComponent>(rig.Tester.Entity).Yaw = 1.2f;
        Assert.StartsWith("Placed: window, 1.2 by 1.2 metres, 0.9 above the floor, in Brick Wall",
                          rig.Run("edit", "build", "window", "width", "1.2", "height", "1.2", "above", "0.9"));
        var (size, pose, prefab) = Piece(rig, rig.Selected);
        Assert.Equal("glass_wall", prefab);
        Near(new Vector3(1.2f, 1.2f, 0.02f), size);
        Assert.Equal(0.05f + 0.9f + 0.6f, pose.Position.Y, 3);
        Assert.Equal(8f - 0.6f, pose.Position.X, 3);   // the wall's east end is at x 8
        // Left of it, the sill under it and the head over it; no piece past the end.
        var pieces = Bricks(rig);
        Assert.Equal(3, pieces.Count);
        Near(new Vector3(4.8f, 2.7f, 0.35f), pieces.Single(p => p.Size.Y > 2).Size);
        Near(new Vector3(1.2f, 0.9f, 0.35f), pieces.Single(p => p.At.Y < 1).Size);
        Near(new Vector3(1.2f, 0.6f, 0.35f), pieces.Single(p => p.At.Y > 2 && p.Size.Y < 1).Size);
    }

    [Fact]
    public void WithNoWallInReachNothingIsCutAndTheDialogIsToldWhy()
    {
        var rig = Mine(_dir);
        Assert.Equal("There is no wall within 3 metres in front of you to fit the door into.", rig.Run("edit", "build", "door"));
        Assert.Equal("", rig.Run("edit", "build", "door", "dialog"));
        Assert.Equal("build.refused", rig.LastMenu!.Path);
        Assert.Equal("There is no wall within 3 metres in front of you to fit the door into.", rig.LastMenu.Title);
        // Free standing it is built.
        Assert.StartsWith("Placed: door, 0.9 by 2.1 metres, knob, 1 metre in front of you", rig.Run("edit", "build", "door", "free"));
        Assert.Equal("", rig.Run("edit", "build", "floor", "dialog"));
        Assert.Equal("build.placed", rig.LastMenu!.Path);
        Assert.StartsWith("Placed: floor", rig.LastMenu.Title);
    }

    [Fact]
    public void ADoorTooTallForTheWallIsRefused()
    {
        var rig = Mine(_dir);
        rig.Run("edit", "build", "wall", "length", "6", "height", "2", "material", "Brick", "ahead", "1");
        Assert.Equal("Brick Wall reaches 2 metres above your floor; the door is 2.1 metres high.", rig.Run("edit", "build", "door"));
        Assert.Single(Bricks(rig));
    }

    // ── Who may, and the form ───────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyAnEditorIsAnsweredAndTheFormOffersWhatExists()
    {
        var rig = Mine(_dir);
        var form = Assert.IsType<EditorMenu>(rig.Menu("build", "form"));
        Assert.Equal(BuildCatalog.FormPath, form.Path);
        var catalog = BuildCatalog.From(form);
        Assert.Equal(new[] { "floor", "wall", "roof", "door", "window", "shape", "prefab" }, catalog.Kinds.Select(k => k.Word));
        var materials = catalog.OptionsOf("wall", "material");
        Assert.Contains(materials, m => m.Value == "Brick" && m.Sets["thickness"] == "0.35");
        AcousticRegistry.Initialize();
        Assert.All(materials.Concat(catalog.OptionsOf("floor", "material")).Concat(catalog.OptionsOf("roof", "material")),
                   m => Assert.True(AcousticRegistry.IsKnown(m.Value), m.Value));
        var doors = catalog.OptionsOf("door", "type").Select(o => o.Value).ToList();
        Assert.Contains("knob", doors);
        Assert.Contains("pushbar", doors);
        Assert.DoesNotContain("auto-slide", doors);
        Assert.DoesNotContain("elevator", doors);

        // Somebody else's map: the form is not answered at all, and building is refused.
        rig.On("theirs");
        int replies = rig.Replies.Count;
        Assert.Equal("", rig.Run("edit", "build", "form"));
        Assert.Null(rig.LastMenu);
        Assert.Equal(replies, rig.Replies.Count);
        Assert.Equal(Denied, rig.Run("edit", "build", "wall"));
    }

    // ── The dialog's form, on the client ────────────────────────────────────────────────────────

    private BuildCatalog Catalog()
    {
        var rig = Mine(_dir);
        return BuildCatalog.From(rig.Menu("build", "form")!);
    }

    [Fact]
    public void EachKindHasItsFieldsAndBuildsTheCommand()
    {
        var form = new BuildForm(Catalog(), new BuildMemory());
        Assert.Equal("floor", form.Kind);
        Assert.Equal(new[] { "width", "length", "thickness", "material", "where", "distance", "facing" }, form.Fields.Select(f => f.Word));
        var width = form.Fields[0];
        Assert.Equal("Width, in metres", width.Label);
        Assert.Equal("From 0.1 to 200 m. Left to right as you face it.", width.Description);
        // A floor starts at your feet, so its distance is not in use.
        Assert.False(form.IsEnabled("distance"));

        form.SetKind("wall");
        Assert.Equal(new[] { "length", "height", "thickness", "material", "where", "distance", "facing" }, form.Fields.Select(f => f.Word));
        Assert.True(form.TryCommand(out var command, out _));
        Assert.Equal("/edit build wall length 4 height 2.7 thickness 0.35 material Brick where ahead distance 1 facing me dialog", command);
        // A material brings its own thickness.
        form.Set("material", "Metal");
        Assert.Equal("0.05", form.Text("thickness"));
        form.Set("where", "here");
        Assert.True(form.TryCommand(out command, out _));
        Assert.DoesNotContain("distance", command);

        form.Set("length", "300");
        Assert.False(form.TryCommand(out _, out var why));
        Assert.Equal("Length must be 0.1 to 200 m; 300 is outside it.", why);
        form.Set("length", "6,5");
        Assert.True(form.TryCommand(out command, out _));
        Assert.Contains("length 6.5 ", command);

        form.SetKind("roof");
        Assert.Equal(new[] { "above", "width", "length", "thickness", "material", "where", "distance", "facing" }, form.Fields.Select(f => f.Word));
        form.SetKind("window");
        Assert.Equal(new[] { "width", "height", "above", "where", "distance", "facing", "fit" }, form.Fields.Select(f => f.Word));
    }

    [Fact]
    public void FittingTakesPlaceOfWhereAndAPrefabIsSizedOnlyIfItCanBe()
    {
        var form = new BuildForm(Catalog(), new BuildMemory());
        form.SetKind("door");
        Assert.Equal("true", form.Text("fit"));
        Assert.False(form.IsEnabled("where"));
        Assert.False(form.IsEnabled("facing"));
        Assert.True(form.TryCommand(out var command, out _));
        Assert.Equal("/edit build door width 0.9 height 2.1 type knob fit yes dialog", command);
        form.Set("fit", "false");
        Assert.True(form.IsEnabled("where") && form.IsEnabled("distance") && form.IsEnabled("facing"));

        form.SetKind("prefab");
        form.Set("category", "fire");
        Assert.All(form.Options("prefab"), o => Assert.Equal("fire", o.Category));
        Assert.Equal("fire", form.Options("prefab").Single(o => o.Value == form.Text("prefab")).Category);
        Assert.False(form.IsEnabled("width"));
        form.Set("category", "walls-and-fences");
        form.Set("prefab", "brick_wall");
        Assert.True(form.IsEnabled("width"));
        Assert.Equal("0.35", form.Text("depth"));
        Assert.True(form.TryCommand(out command, out _));
        Assert.StartsWith("/edit build prefab category walls-and-fences prefab brick_wall width 2 height 3 depth 0.35 ", command);
    }

    [Fact]
    public void TheLastValuesOfEachKindAreRemembered()
    {
        var catalog = Catalog();
        var memory = new BuildMemory();
        var form = new BuildForm(catalog, memory);
        form.SetKind("wall");
        form.Set("length", "6");
        form.Remember();
        form.SetKind("floor");
        form.Set("width", "9");

        var again = new BuildForm(catalog, memory);
        Assert.Equal("wall", again.Kind);
        Assert.Equal("6", again.Text("length"));
        // A kind not placed this session starts as offered.
        again.SetKind("floor");
        Assert.Equal("4", again.Text("width"));
    }

    // ── Control+B, through the session ──────────────────────────────────────────────────────────

    private sealed class Speech : ISpeechOutput
    {
        public readonly List<string> Spoken = new();
        public string BackendName => "test";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Spoken.Add(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class Shell : IClientShell
    {
        public BuildDialog? Build;
        public bool IsGameInputActive => true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowBuildDialog(BuildDialog dialog) => Build = dialog;
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }

    [Fact]
    public void ControlBOpensTheDialogOnlyWhenTheServerSendsItAndClosesItAgain()
    {
        var formMenu = Mine(_dir).Menu("build", "form")!;
        AcousticRegistry.Initialize();
        var network = new ClientNetworkService();
        var sent = new List<IMessage>();
        network.Sending = sent.Add;
        var speech = new Speech();
        var shell = new Shell();
        var session = new ClientGameSession(network, speech, shell, new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("none"), enableAudio: false);

        // Control+B asks; it is a chord, so B on its own and Control on their own do not.
        Assert.True(session.Press(GameKey.B, KeyModifiers.Control));
        var ask = Assert.IsType<TextCommand>(Assert.Single(sent));
        Assert.Equal("edit", ask.Command);
        Assert.Equal(new[] { "build", "form" }, ask.Args);
        Assert.False(session.IsBound(InputContext.Gameplay, GameKey.ControlLeft));
        // No answer (no permission): nothing opens and nothing is said.
        Assert.Null(shell.Build);
        Assert.Empty(speech.Spoken);

        session.HandleMessage(formMenu);
        var dialog = Assert.IsType<BuildDialog>(shell.Build);
        sent.Clear();
        Assert.Null(dialog.Place());
        var command = Assert.IsType<TextCommand>(Assert.Single(sent));
        Assert.Equal("edit", command.Command);
        Assert.Equal("dialog", command.Args[^1]);
        Assert.Equal("Still placing the last one.", dialog.Place());

        // Placed: said, and the dialog stays open with focus back on What.
        bool placed = false;
        dialog.Placed += () => placed = true;
        session.HandleMessage(new EditorMenu { Path = "build.placed", Title = "Placed: floor, 4 by 4 metres, wood, at your feet." });
        Assert.True(placed);
        Assert.True(dialog.IsOpen);
        Assert.Contains("Placed: floor, 4 by 4 metres, wood, at your feet.", speech.Spoken);
        string? refused = null;
        dialog.Refused += why => refused = why;
        Assert.Null(dialog.Place());
        session.HandleMessage(new EditorMenu { Path = "build.refused", Title = "Not built: that would put Wood Floor through sean." });
        Assert.Equal("Not built: that would put Wood Floor through sean.", refused);
        Assert.True(dialog.IsOpen);

        // The key that opened it closes it, as Escape does.
        Assert.True(dialog.IsCloseKey(GameKey.B, KeyModifiers.Control));
        Assert.True(dialog.IsCloseKey(GameKey.Escape, KeyModifiers.None));
        Assert.False(dialog.IsCloseKey(GameKey.B, KeyModifiers.None));
        bool closed = false;
        dialog.CloseRequested += () => closed = true;
        sent.Clear();
        session.Press(GameKey.B, KeyModifiers.Control);
        Assert.True(closed);
        Assert.False(dialog.IsOpen);
        Assert.Empty(sent);
    }
}
