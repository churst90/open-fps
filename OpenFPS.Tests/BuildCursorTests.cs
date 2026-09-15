using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Putting something exactly where you meant, without being able to see where that is.
///
/// Every other building verb solves this by using the player's own body as the coordinate —
/// `/group` sweeps a radius around you, `/addseat` puts a seat where you stand. That works until
/// you need a roof, which you cannot walk to, or a straight wall, which you cannot pace out
/// reliably. So the cursor: moved in metres from an origin the builder chose, announcing itself and
/// what is already there on every move. A review cursor, applied to a building site.
///
/// What these hold: that the cursor's arithmetic is right in world terms, that a run of parts comes
/// out straight and touching, that you are told what is already where you are about to build, that
/// a mistake can be taken back, and that the thing which says "this is not a room yet" says WHY.
/// </summary>
public class BuildCursorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-build-{Guid.NewGuid():N}");

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    // ── The cursor ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The axes are the BUILDER'S, fixed when the origin is set. Fixed rather than live, because a
    /// coordinate system that rotates when you turn round is one where the wall you placed a moment
    /// ago has moved.
    /// </summary>
    [Fact]
    public void TheCursorsAxesAreTheBuildersAndTheyDoNotMove()
    {
        var build = new BuildSession();
        build.SetOrigin(new Vector3(100, 0, 100), yaw: 0f);       // facing +Z

        build.Cursor = new Vector3(0, 0, 3);
        Assert.Equal(new Vector3(100, 0, 103), build.WorldCursor, Compare);
        build.Cursor = new Vector3(2, 1, 0);
        Assert.Equal(new Vector3(102, 1, 100), build.WorldCursor, Compare);

        // Facing east, "forward" is +X — and it stays +X however the player turns afterwards.
        var east = new BuildSession();
        east.SetOrigin(new Vector3(100, 0, 100), yaw: MathF.PI / 2f);
        east.Cursor = new Vector3(0, 0, 3);
        Assert.Equal(new Vector3(103, 0, 100), east.WorldCursor, Compare);
    }

    /// <summary>
    /// The cursor reads out in small named numbers, never a world coordinate. "Two right, three
    /// forward" is somewhere a person can hold in their head and walk back to; "204.7, 12.3, -88.1"
    /// is what every one of these commands would be reduced to if the origin were the map's.
    /// </summary>
    [Fact]
    public void TheCursorReadsOutAsSomewhereAPersonCanRememberIt()
    {
        var build = new BuildSession();
        build.SetOrigin(Vector3.Zero, 0f);
        Assert.Equal("at the origin", build.Describe());

        build.Cursor = new Vector3(2, 1, 3);
        string said = build.Describe();
        Assert.Contains("3 forward", said);
        Assert.Contains("2 right", said);
        Assert.Contains("1 up", said);
        Assert.DoesNotContain("-", said);

        build.Cursor = new Vector3(-2, -1, -3);
        said = build.Describe();
        Assert.Contains("3 back", said);
        Assert.Contains("2 left", said);
        Assert.Contains("1 down", said);
    }

    // ── Building with it ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PuttingSomethingDownPutsItWhereTheCursorIs()
    {
        var f = new Fixture(_dir);
        f.Run("origin");
        f.Run("at", "0", "0", "4");
        f.Run("put", "concrete_wall");

        var placed = f.PlacedEntities().Single();
        Assert.Equal(f.Feet + new Vector3(0, 0, 4), f.World.Get<Transform>(placed).Position, Compare);
    }

    /// <summary>
    /// A wall is not one part, it is a line of them, and a line placed by hand from a cursor is only
    /// as straight as the arithmetic somebody did in their head. A run steps by the part's OWN
    /// footprint, so the panels touch and the wall is straight.
    /// </summary>
    [Fact]
    public void ARunComesOutStraightAndTouching()
    {
        var f = new Fixture(_dir);
        f.Run("origin");
        f.Run("at", "right", "0");           // travel along +X, so the run follows it
        f.Run("put", "concrete_wall", "run", "4");

        var placed = f.PlacedEntities().Select(e => f.World.Get<Transform>(e).Position)
                      .OrderBy(p => p.X).ToList();
        Assert.Equal(4, placed.Count);

        float width = f.PrefabSize("concrete_wall").X;
        for (int i = 1; i < placed.Count; i++)
        {
            Assert.Equal(placed[0].Z, placed[i].Z, 3);   // dead straight
            Assert.Equal(placed[0].Y, placed[i].Y, 3);
            Assert.Equal(width, placed[i].X - placed[i - 1].X, 2);   // touching, not overlapping or gapped
        }
    }

    /// <summary>
    /// A run can be aimed on the spot. Without this the only way to aim one is to move the cursor
    /// zero metres in the direction you want first, which works and is a riddle.
    /// </summary>
    [Fact]
    public void ARunCanBeAimedWhereItIsAskedFor()
    {
        var f = new Fixture(_dir);
        f.Run("origin");
        f.Run("put", "concrete_wall", "run", "3", "right");

        var placed = f.PlacedEntities().Select(e => f.World.Get<Transform>(e).Position).ToList();
        Assert.Equal(3, placed.Count);
        Assert.Single(placed.Select(p => p.Z).Distinct());       // all on one line...
        Assert.True(placed.Max(p => p.X) - placed.Min(p => p.X) > 3f);     // ...and that line runs right
    }

    /// <summary>The cursor is left at the end of a run, where the next thing goes — otherwise every
    /// run is followed by arithmetic the builder has to do themselves.</summary>
    [Fact]
    public void ARunLeavesTheCursorAtTheEndOfIt()
    {
        var f = new Fixture(_dir);
        f.Run("origin");
        f.Run("at", "right", "0");
        f.Run("put", "concrete_wall", "run", "3");
        Assert.Equal(3f * f.PrefabSize("concrete_wall").X, f.Session.Build.Cursor.X, 2);
    }

    [Fact]
    public void TurningAPartTurnsIt()
    {
        var f = new Fixture(_dir);
        f.Run("origin");
        f.Run("at", "0", "0", "4");
        f.Run("put", "concrete_wall", "turn", "90");

        var placed = f.PlacedEntities().Single();
        MathHelper.ToYawPitch(f.World.Get<Transform>(placed).Rotation, out float yaw, out _);
        Assert.Equal(MathF.PI / 2f, MathF.Abs(yaw), 2);
    }

    /// <summary>
    /// Moving the cursor says what is already there. Moving to a spot and being told nothing is the
    /// same as not moving; being told "Concrete Wall" is how you find the wall you placed a minute
    /// ago and build the next one against it.
    /// </summary>
    [Fact]
    public void TheCursorSaysWhatIsAlreadyThere()
    {
        var f = new Fixture(_dir);
        f.Run("origin");
        f.Run("at", "0", "0", "4");
        f.Run("put", "concrete_wall");

        f.Run("at", "0", "0", "12");
        Assert.Contains("Empty", f.LastReply);

        f.Run("at", "0", "0", "4");
        Assert.Contains("Concrete Wall", f.LastReply);
    }

    [Fact]
    public void ItRefusesToBuildInsideSomethingThatIsAlreadyThere()
    {
        var f = new Fixture(_dir);
        f.Run("origin");
        f.Run("at", "0", "0", "4");
        f.Run("put", "concrete_wall");
        f.Run("put", "concrete_wall");

        Assert.Contains("already something there", f.LastReply);
        Assert.Single(f.PlacedEntities());
    }

    /// <summary>
    /// Undo is not a convenience. A part in the wrong place is invisible to somebody who cannot see
    /// it, so the mistake is not merely unfixed, it is undetectable until they walk into it — and by
    /// then they have built three more things around it.
    /// </summary>
    [Fact]
    public void AMistakeCanBeTakenBack()
    {
        var f = new Fixture(_dir);
        f.Run("origin");
        f.Run("at", "0", "0", "4");
        f.Run("put", "concrete_wall");
        int wall = f.PlacedEntities().Single().Id;

        f.Run("undo");
        Assert.Contains("Took back", f.LastReply);
        Assert.False(f.Lookup.ContainsKey(wall));

        f.Run("undo");
        Assert.Contains("not placed anything", f.LastReply);
    }

    // ── Standing back ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The feedback loop the whole of building without sight hangs off. A sighted builder stands
    /// back and sees the roof is missing; /room is the replacement for standing back, and it has to
    /// name what is missing rather than merely saying no.
    /// </summary>
    [Fact]
    public void RoomSaysWhatIsMissingAndNotJustNo()
    {
        var f = new Fixture(_dir);
        f.BuildShell(sides: 3);            // one wall short of enclosing anything

        f.Run("room", "9");
        Assert.DoesNotContain("encloses a room", f.LastReply);
        Assert.Contains("4 are needed", f.LastReply);
        Assert.Contains("wall", f.LastReply);          // and it names which faces are open

        f.BuildShell(sides: 4);            // the missing side
        f.Run("room", "9");
        Assert.Contains("encloses a room", f.LastReply);
    }

    /// <summary>
    /// Even when it IS a room, it says what is still open. Four walls with nothing above or below is
    /// a shaft — which genuinely does sound like a room, and is still not what most people mean when
    /// they say they have built one.
    /// </summary>
    [Fact]
    public void ItNamesWhatIsStillOpenEvenWhenItPasses()
    {
        var f = new Fixture(_dir);
        f.BuildShell(sides: 4);

        f.Run("room", "9");
        Assert.Contains("encloses a room", f.LastReply);
        Assert.Contains("ceiling", f.LastReply);
        Assert.Contains("floor", f.LastReply);

        f.BuildRoof();
        f.Run("room", "9");
        Assert.Contains("encloses a room", f.LastReply);
        Assert.DoesNotContain("ceiling", f.LastReply);
    }

    /// <summary>...and it answers BEFORE anything is grouped, which is the point of asking.</summary>
    [Fact]
    public void RoomAnswersBeforeAnythingIsCommittedTo()
    {
        var f = new Fixture(_dir);
        f.BuildShell(sides: 4);
        f.BuildRoof();

        int composites = 0;
        f.World.Query(new QueryDescription().WithAll<CompositeComponent>(), (Entity _) => composites++);
        Assert.Equal(0, composites);

        f.Run("room", "9");
        Assert.Contains("encloses a room", f.LastReply);
    }

    [Fact]
    public void RoomSaysSoWhenThereIsNothingToMeasure()
    {
        var f = new Fixture(_dir);
        f.Run("room", "3");
        Assert.Contains("Nothing within", f.LastReply);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private static readonly VectorComparer Compare = new();

    private sealed class VectorComparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.02f;
        public int GetHashCode(Vector3 v) => 0;
    }

    /// <summary>
    /// A real map, a real player and the real command handler, driven the way a telnet session drives
    /// it. Going through the commands rather than the services is the point: the arithmetic being
    /// right is worth nothing if the words a player types do not reach it.
    /// </summary>
    private sealed class Fixture
    {
        public readonly MapManager Maps;
        public readonly CompositeService Composites;
        public readonly UserSession Session;
        public readonly string MapId = "default";
        public readonly Vector3 Feet = new(140, 0, 140);

        public World World = null!;
        public Dictionary<int, Entity> Lookup = null!;
        public string LastReply = "";

        private readonly CommandHandler _commands;
        private readonly GameServer _server;
        private readonly List<string> _replies = new();

        public Fixture(string dir)
        {
            string mapDir = Path.Combine(dir, "maps");
            Directory.CreateDirectory(mapDir);
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "maps"), "*.json"))
                File.Copy(file, Path.Combine(mapDir, Path.GetFileName(file)), overwrite: true);

            var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(mapDir), prefabs);
            Maps.Initialize();
            Composites = new CompositeService(Maps, prefabs, new CompositeRepository(Path.Combine(dir, "composites")));
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out _, out Lookup));

            var sessions = new SessionManager();
            _server = new GameServer(new StubUserRepository());
            var entity = World.Create(
                new PlayerComponent { ConnectionId = 1, Username = "builder", Role = UserRole.Dev },
                EntityType.Player,
                new Transform { Position = Feet, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" });
            Maps.IndexEntity(MapId, entity);
            Session = new UserSession { ConnectionId = 1, Username = "builder", Role = UserRole.Dev, Entity = entity, CurrentMapId = MapId };
            sessions.AddSession(1, Session);

            _commands = new CommandHandler(sessions, Maps, _server, Composites, new OccupancyService(Maps));
        }

        /// <summary>
        /// One typed command, run the whole way a telnet line is run: dispatched, buffered, and
        /// executed on the tick thread. Going through the real path is the point — arithmetic being
        /// right is worth nothing if the words a player types do not reach it.
        /// </summary>
        public void Run(string command, params string[] args)
        {
            _replies.Clear();
            _commands.HandleTextCommand(Session.ConnectionId,
                new TextCommand { Command = command, Args = args },
                m => { if (m is TextEvent t) _replies.Add(t.Text); });
            _server.DrainCommandBuffer();
            LastReply = string.Join(" ", _replies);
        }

        private sealed class StubUserRepository : IUserRepository
        {
            public UserData? GetUser(string username) => null;
            public bool AddUser(string username, string password, UserRole role) => true;
            public bool VerifyPassword(string username, string password) => false;
        }

        public IEnumerable<Entity> PlacedEntities()
            => Session.Build.Placed_Entities.Where(id => Lookup.ContainsKey(id)).Select(id => Lookup[id]);

        public Vector3 PrefabSize(string prefabId)
            => Composites.Prefabs.Prefabs[prefabId].ColliderSize ?? Vector3.One;

        /// <summary>
        /// Walls round the origin, built the way a player would: put the cursor at a corner, say
        /// which way you are running, and run. Called again with more sides it adds only the sides
        /// it has not already built, so a test can watch a shell being closed one wall at a time.
        /// </summary>
        public void BuildShell(int sides)
        {
            if (!Session.Build.Placed) Run("origin");
            const float half = 4f;
            var runs = new (float X, float Y, float Z, string Along, float Turn)[]
            {
                (-half + 1f, 1.5f,  half,     "right",   0f),
                (-half + 1f, 1.5f, -half,     "right",   0f),
                ( half,      1.5f, -half + 1f, "forward", 90f),
                (-half,      1.5f, -half + 1f, "forward", 90f),
            };

            for (int i = _sidesBuilt; i < Math.Min(sides, runs.Length); i++)
            {
                var (x, y, z, along, turn) = runs[i];
                Run("at", x.ToString(), y.ToString(), z.ToString());
                Run("at", along, "0");
                Run("put", "concrete_wall", "turn", turn.ToString(), "run", "4");
            }
            _sidesBuilt = Math.Max(_sidesBuilt, sides);
        }

        private int _sidesBuilt;

        public void BuildRoof()
        {
            Run("at", "0", "3", "0");
            Run("put", "concrete_floor");
        }
    }
}
