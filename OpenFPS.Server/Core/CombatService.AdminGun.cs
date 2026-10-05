using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Systems;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>Where a gun's fire selector sits, on the gun's own entity. Server only: the client is told
/// the setting in words when it changes. Absent means the gun's default (<see cref="FireSelector.Default"/>).</summary>
public struct FireSelectorComponent
{
    public FireMode Mode;
}

/// <summary>How an admin gun is set, on the gun's own entity. Server only.</summary>
public struct AdminGunComponent
{
    public AdminGunMode Mode;
    /// <summary>The <see cref="WeaponRegistry"/> weapon whose ballistics and report it fires.</summary>
    public string Calibre;
    /// <summary>Which report variant it is heard with (<see cref="AdminGun.DescribeReport"/>).</summary>
    public int Report;
}

/// <summary>Somebody or something the admin gun froze, and until when (seconds on the combat clock).
/// The movement of players, of people walking, and of vehicles each stop while it is on them.</summary>
public struct FrozenComponent
{
    public double Until;
}

/// <summary>
/// The fire selector (X), automatic fire, and the admin gun: its modes, its calibre, and what its round
/// does where it lands. See <see cref="AdminGun"/> for the sounds.
/// </summary>
public sealed partial class CombatService
{
    // ── The fire selector ───────────────────────────────────────────────────────────────────────

    /// <summary>Where a gun's selector sits now.</summary>
    internal static FireMode SelectorOf(World world, Entity item, WeaponDefinition weapon)
    {
        if (!FireSelector.Has(weapon)) return FireMode.Semi;
        if (world.IsAlive(item) && world.Has<FireSelectorComponent>(item)) return world.Get<FireSelectorComponent>(item).Mode;
        return FireSelector.Default(weapon);
    }

    /// <summary>
    /// /selector [next|back|SETTING]: X and Shift+X. The gun in your hands, one detent on or back, or to
    /// the setting named; the lever is heard by everybody near and the setting is said to you. On the
    /// admin gun the selector is its mode.
    /// </summary>
    public void Selector(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var lookup, out var position)) return;
        string word = args.Length > 0 ? args[0].ToLowerInvariant() : "next";
        int direction = word is "back" or "previous" or "prev" ? -1 : 1;
        var forward = Vector3.Transform(new Vector3(0, 0, 1), world.Get<Transform>(session.Entity).Rotation);

        if (HoldsAdminGun(world, session.Entity, lookup, out var gun))
        {
            if (!session.Can(Permissions.AdminGun)) { Say(reply, NotYours); return; }
            ref var state = ref AdminStateOf(world, gun);
            if (word is "next" or "back" or "previous" or "prev") state.Mode = AdminGun.Step(state.Mode, direction);
            else if (AdminGun.TryParseMode(word, out var m)) state.Mode = m;
            else { Say(reply, "The admin gun's modes: kill, vaporize, freeze, inspect."); return; }
            _server.EmitWorldAudio(session.CurrentMapId, session.Entity.Id, "admin gun mode", new[]
            {
                new TransientSound
                {
                    Character = SoundCharacter.Knock, Position = position + new Vector3(0, Hands.Y, 0) + forward * Hands.Z,
                    OnBody = true, BodyOffset = Hands, LevelDb = AdminGun.ModeLevelDb, SynthKey = AdminGun.ModeKey(state.Mode),
                    DecaySeconds = 0.25f, Hz = AdminGun.ModeHz(state.Mode),
                },
            });
            Say(reply, Capital(AdminGun.Spoken(state.Mode)) + ".");
            Log.Information("{User}'s admin gun is on {Mode}.", session.Username, AdminGun.Spoken(state.Mode));
            return;
        }

        if (!HandsService.TryGetHeldWeapon(world, session.Entity, lookup, out var weapon, out var item))
        { Say(reply, "You are not holding a gun."); return; }
        if (!FireSelector.Has(weapon)) { Say(reply, FireSelector.NoSelector); return; }
        FireMode now = SelectorOf(world, item, weapon), next;
        if (word is "next" or "back" or "previous" or "prev") next = FireSelector.Step(weapon, now, direction);
        else if (!FireSelector.TryParse(weapon, word, out next))
        {
            Say(reply, $"The {weapon.DisplayName}'s settings: {string.Join(", ", weapon.Selector.Select(m => FireSelector.Spoken(weapon, m)))}.");
            return;
        }
        if (world.Has<FireSelectorComponent>(item)) world.Get<FireSelectorComponent>(item).Mode = next;
        else world.Add(item, new FireSelectorComponent { Mode = next });
        if (next != FireMode.Auto) _automatic.Remove(session);
        _server.EmitWorldAudio(session.CurrentMapId, session.Entity.Id, "a selector", new[]
        {
            HandSound(position, forward, WeaponHandling.SelectorKey(weapon), new HandlingSpec(weapon.Id, false, 0, false, IsSelector: true)),
        });
        Say(reply, Capital(FireSelector.Spoken(weapon, next)) + ".");
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ── Automatic fire ──────────────────────────────────────────────────────────────────────────

    /// <summary>A trigger held down on automatic.</summary>
    private sealed class Automatic
    {
        public required int ItemId;
        public required string MapId;
        public required double NextAt;
        public required double Since;
    }

    private readonly Dictionary<UserSession, Automatic> _automatic = new();

    /// <summary>The longest a trigger is taken to be held without being let go: past this the gun stops,
    /// in case the client's "cease" never came. Longer than any magazine here lasts at its rate.</summary>
    public const double MaxTriggerSeconds = 6.0;

    /// <summary>Whether a player's trigger is held down on automatic now. For tests.</summary>
    public bool IsFiringAutomatic(UserSession session) => _automatic.ContainsKey(session);

    private void StartAutomatic(UserSession session, Entity item, WeaponDefinition weapon)
    {
        double now = Clock();
        _automatic[session] = new Automatic { ItemId = item.Id, MapId = session.CurrentMapId, NextAt = now + weapon.SecondsBetweenShots, Since = now };
    }

    /// <summary>/cease: the trigger let go. The client sends it when Enter comes up.</summary>
    public void Cease(UserSession session) => _automatic.Remove(session);

    /// <summary>Every held trigger on this map fires its next round when the action has cycled.</summary>
    private void UpdateAutomatic(string mapId, World world)
    {
        if (_automatic.Count == 0) return;
        double now = Clock();
        foreach (var (session, auto) in _automatic.Where(kv => kv.Value.MapId == mapId && now >= kv.Value.NextAt).ToList())
        {
            void Stop() => _automatic.Remove(session);
            if (now - auto.Since > MaxTriggerSeconds) { Stop(); continue; }
            if (!_maps.TryGetMap(mapId, out _, out _, out var grid, out var lookup)
                || session.CurrentMapId != mapId || session.Entity == Entity.Null || !world.IsAlive(session.Entity)
                || world.Has<DeadComponent>(session.Entity) || world.Has<FrozenComponent>(session.Entity))
            { Stop(); continue; }
            if (!HandsService.TryGetHeldWeapon(world, session.Entity, lookup, out var weapon, out var item)
                || item.Id != auto.ItemId || SelectorOf(world, item, weapon) != FireMode.Auto)
            { Stop(); continue; }
            var position = world.Get<Transform>(session.Entity).Position;
            var forward = Vector3.Transform(new Vector3(0, 0, 1), world.Get<Transform>(session.Entity).Rotation);
            var muzzle = position + new Vector3(0, HipHeight, 0) + forward * 0.5f;
            if (!SpendRound(session, world, weapon, item, position, forward, m => _server.SendToSession(session, m))) { Stop(); continue; }
            Shoot(session, world, grid, weapon, position, forward, muzzle, cycle: true);
            auto.NextAt = now + weapon.SecondsBetweenShots;
        }
    }

    // ── The admin gun ───────────────────────────────────────────────────────────────────────────

    private const string NotYours = "The admin gun is the admin's alone.";

    /// <summary>Health a kill round takes: everybody's, whatever they have.</summary>
    internal const int AdminKillDamage = 1_000_000;

    /// <summary>What the client is told is in the admin gun: it never runs dry.</summary>
    internal const int AdminGunRounds = 999;

    /// <summary>Whether an item is an admin gun.</summary>
    public static bool IsAdminGun(World world, Entity item)
        => world.IsAlive(item) && world.Has<ItemComponent>(item)
           && string.Equals(world.Get<ItemComponent>(item).WeaponId, AdminGun.WeaponId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The admin gun in a player's hands, if that is what they are holding.</summary>
    public static bool HoldsAdminGun(World world, Entity player, Dictionary<int, Entity> lookup, out Entity gun)
    {
        gun = Entity.Null;
        if (!world.IsAlive(player)) return false;
        var (right, left) = HandsService.Holding(world, player, lookup);
        foreach (var candidate in new[] { right, left })
            if (candidate != null && IsAdminGun(world, candidate.Value)) { gun = candidate.Value; return true; }
        return false;
    }

    private static ref AdminGunComponent AdminStateOf(World world, Entity gun)
    {
        if (!world.Has<AdminGunComponent>(gun))
            world.Add(gun, new AdminGunComponent { Mode = AdminGunMode.Kill, Calibre = AdminGun.DefaultCalibre, Report = AdminGun.DefaultReport });
        return ref world.Get<AdminGunComponent>(gun);
    }

    /// <summary>
    /// The admin gun's trigger: a round of its calibre, flown like any other, heard as the admin gun's
    /// report, and what it does where it lands is its mode's. Never empty and never reloaded; no faster
    /// than its calibre's action.
    /// </summary>
    private void FireAdminGun(UserSession session, World world, SpatialGrid<Entity> grid, Entity gun, Vector3 position, Action<IMessage> reply)
    {
        if (!session.Can(Permissions.AdminGun)) { Say(reply, NotYours); return; }
        var state = AdminStateOf(world, gun);
        var weapon = WeaponRegistry.Get(state.Calibre) ?? WeaponRegistry.Ar15;
        double now = Clock();
        if (_lastShot.TryGetValue(session, out double last) && now - last < weapon.SecondsBetweenShots - 1e-6) return;
        _lastShot[session] = now;
        var forward = Vector3.Transform(new Vector3(0, 0, 1), world.Get<Transform>(session.Entity).Rotation);
        var muzzle = position + new Vector3(0, HipHeight, 0) + forward * 0.5f;
        // Kill and freeze are aimed at people, and the assist helps them as it helps any shot. Vaporize
        // and inspect are aimed at a THING: no assist (it would turn the gun onto somebody walking past
        // the wall you meant), and no scatter, so the round goes exactly where the body faces.
        bool atPeople = state.Mode is AdminGunMode.Kill or AdminGunMode.Freeze;
        Shoot(session, world, grid, weapon, position, forward, muzzle, cycle: false,
              reportKey: AdminGun.ReportKey(weapon.Id, state.Report), admin: state.Mode,
              assist: atPeople, dispersion: atPeople ? null : 0f);
        if (session.IsTextClient) Say(reply, $"You fire the admin gun, on {AdminGun.Spoken(state.Mode)}, with {weapon.DisplayName} rounds.");
    }

    /// <summary>/calibre [NAME|next|back]: Y and Shift+Y. Which weapon's rounds and report the admin gun fires.</summary>
    public void Calibre(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var lookup, out _)) return;
        if (!HoldsAdminGun(world, session.Entity, lookup, out var gun)) { Say(reply, "Only the admin gun changes calibre. Hold it first."); return; }
        ref var state = ref AdminStateOf(world, gun);
        string word = args.Length > 0 ? string.Join(" ", args).Trim() : "next";
        if (word.Equals("next", StringComparison.OrdinalIgnoreCase)) state.Calibre = AdminGun.StepCalibre(state.Calibre, 1);
        else if (word is "back" or "previous" or "prev") state.Calibre = AdminGun.StepCalibre(state.Calibre, -1);
        else if (WeaponRegistry.TryGet(word, out var named)) state.Calibre = named.Id;
        else if (WeaponRegistry.All.FirstOrDefault(w => w.Cartridge.Equals(word, StringComparison.OrdinalIgnoreCase)
                     || (Ammunition.TryFind(word, out var a) && a.Id == w.AmmoId)) is { } byRound) state.Calibre = byRound.Id;
        else { Say(reply, $"No calibre called {word}. Try: {string.Join(", ", WeaponRegistry.All.Select(w => w.Id).OrderBy(x => x))}."); return; }
        var w = WeaponRegistry.Get(state.Calibre)!;
        Say(reply, $"{w.DisplayName}, {w.Cartridge}.");
    }

    /// <summary>/admingun [report N]: how the admin gun is set, or which report variant it is heard with.</summary>
    public void AdminGunCommand(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var lookup, out _)) return;
        if (!HoldsAdminGun(world, session.Entity, lookup, out var gun)) { Say(reply, "You are not holding the admin gun."); return; }
        ref var state = ref AdminStateOf(world, gun);
        if (args.Length >= 2 && args[0].Equals("report", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(args[1], out int v) || v < 0 || v > AdminGun.ReportVariants)
            { Say(reply, $"Say a report from 1 to {AdminGun.ReportVariants}, or 0 for the calibre's own."); return; }
            state.Report = v;
            Say(reply, $"Report {v}: {AdminGun.DescribeReport(v)}.");
            return;
        }
        var w = WeaponRegistry.Get(state.Calibre) ?? WeaponRegistry.Ar15;
        Say(reply, $"Admin gun on {AdminGun.Spoken(state.Mode)}, {w.DisplayName} rounds, report {state.Report}: {AdminGun.DescribeReport(state.Report)}. "
                 + "X changes the mode, Y the calibre, /admingun report N the sound.");
    }

    /// <summary>Puts somebody new walking in a vaporized pedestrian's place is <see cref="ReplaceWalker"/>;
    /// this forgets a vehicle the vehicle system was moving, before it is removed. The server hands it over.</summary>
    public Func<string, int, bool>? ForgetVehicle { get; set; }

    /// <summary>Whether something is moved by the vehicle system (traffic, people walking), and so can be
    /// frozen. The server hands it over; without it only people and driven vehicles are.</summary>
    public Func<int, bool>? MovedByTraffic { get; set; }

    /// <summary>What an admin round does where it lands, for vaporize, freeze and inspect. Always ends
    /// the round.</summary>
    private bool AdminStrike(Flight f, AdminGunMode mode, World world, SpatialGrid<Entity> grid, Dictionary<int, Entity> lookup,
                             Entity hit, bool body, Vector3 at, float metres)
    {
        var shooter = f.Shooter;
        void Tell(string text) => _server.SendToSession(shooter, new TextEvent { Text = text });
        f.Called = true;
        string mapId = f.MapId;
        switch (mode)
        {
            case AdminGunMode.Inspect:
                _server.EmitWorldAudio(mapId, hit.Id, "a scan", new[] { AdminGun.HitSound(mode, at) });
                Tell(InspectWords(world, lookup, hit, metres));
                Log.Information("{User} inspected {What} ({Id}).", shooter.Username, NameOf(world, hit), hit.Id);
                return true;

            case AdminGunMode.Freeze:
            {
                var target = body ? hit : RootOf(world, lookup, hit);
                if (!CanFreeze(world, target))
                {
                    Tell($"Nothing to freeze: {ThingName(world, hit)} does not move.");
                    return true;
                }
                Freeze(mapId, world, lookup, target);
                Tell($"Froze {Whom(world, target)} for {AdminGun.FreezeSeconds:F0} seconds.");
                return true;
            }

            default:
                Vaporize(f, world, grid, lookup, hit, body, at, metres, Tell);
                return true;
        }
    }

    // ── Freezing ────────────────────────────────────────────────────────────────────────────────

    private bool CanFreeze(World world, Entity e)
        => world.IsAlive(e) && !world.Has<DeadComponent>(e)
           && (IsPerson(world, e) || world.Has<DriveComponent>(e) || (MovedByTraffic?.Invoke(e.Id) ?? false));

    /// <summary>Holds something still for <see cref="AdminGun.FreezeSeconds"/>: a player is told, and the
    /// stun gun's crackle is heard on them. A driver frozen freezes what they are driving.</summary>
    internal void Freeze(string mapId, World world, Dictionary<int, Entity> lookup, Entity target)
    {
        double until = Clock() + AdminGun.FreezeSeconds;
        void Hold(Entity e)
        {
            if (world.Has<FrozenComponent>(e)) world.Get<FrozenComponent>(e).Until = until;
            else world.Add(e, new FrozenComponent { Until = until });
            if (world.Has<Velocity>(e)) world.Get<Velocity>(e).Linear = Vector3.Zero;
        }
        Hold(target);
        if (world.Has<OccupantComponent>(target) && world.Get<OccupantComponent>(target).Controls
            && lookup.TryGetValue(world.Get<OccupantComponent>(target).RootEntityId, out var ride) && world.IsAlive(ride))
            Hold(ride);
        var victim = SessionOf(world, target);
        if (victim != null)
        {
            _automatic.Remove(victim);
            _server.SendToSession(victim, new TextEvent { Text = $"You are frozen for {AdminGun.FreezeSeconds:F0} seconds." });
        }
        var t = world.Get<Transform>(target);
        var sound = AdminGun.HitSound(AdminGunMode.Freeze, t.Position + new Vector3(0f, 1.2f, 0f));
        sound.OnBody = IsPerson(world, target);
        sound.BodyOffset = new Vector3(0f, 1.2f, 0f);
        _server.EmitWorldAudio(mapId, target.Id, "a stun", new[] { sound });
        Log.Information("{Target} ({Id}) frozen until {Until:F1}.", NameOf(world, target), target.Id, until);
    }

    /// <summary>Lets go of everything on this map whose time is up; a player is told.</summary>
    private void Thaw(string mapId, World world)
    {
        double now = Clock();
        List<Entity>? due = null;
        world.Query(new QueryDescription().WithAll<FrozenComponent>(), (Entity e, ref FrozenComponent fz) =>
        {
            if (now >= fz.Until) (due ??= new List<Entity>()).Add(e);
        });
        if (due == null) return;
        foreach (var e in due)
        {
            if (!world.IsAlive(e)) continue;
            world.Remove<FrozenComponent>(e);
            var s = SessionOf(world, e);
            if (s != null) _server.SendToSession(s, new TextEvent { Text = "You can move again." });
        }
    }

    // ── Vaporizing ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes what the round hit from the world, and logs it. A player is not removed: they are killed,
    /// and told so. Somebody walking is taken away and somebody else comes along. A vehicle goes whole,
    /// unless somebody is in it. A part of a building goes on its own. The floor, the ground and a
    /// building's foundation are refused: the map stands on them. What came from the map file is gone
    /// only until the server restarts, unless the map is saved after.
    /// </summary>
    private void Vaporize(Flight f, World world, SpatialGrid<Entity> grid, Dictionary<int, Entity> lookup,
                          Entity hit, bool body, Vector3 at, float metres, Action<string> tell)
    {
        string mapId = f.MapId;
        var shooter = f.Shooter;
        string name = ThingName(world, hit);
        if (body && world.Has<PlayerComponent>(hit))
        {
            _server.EmitWorldAudio(mapId, hit.Id, "vaporize", new[] { AdminGun.HitSound(AdminGunMode.Vaporize, at) });
            bool killed = Wound(shooter, world, grid, hit, AdminKillDamage, f.Weapon, m => _server.SendToSession(shooter, m));
            tell($"Players are not vaporized: {Whom(world, hit)} is {(killed ? "killed" : "hit")} instead.");
            Log.Warning("ADMIN GUN: {User} vaporized player {Whom}: killed instead.", shooter.Username, Whom(world, hit));
            return;
        }
        if (body)
        {
            string who = Whom(world, hit);
            var where = world.Get<Transform>(hit).Position;
            _server.EmitWorldAudio(mapId, -1, "vaporize", new[] { AdminGun.HitSound(AdminGunMode.Vaporize, at) });
            Log.Warning("ADMIN GUN: {User} vaporized {Whom} ({Id}) at {At} on {Map}.", shooter.Username, who, hit.Id, PlayerCoordinates.Format(where), mapId);
            int id = hit.Id;
            // A walker is taken off the street and somebody else walks their walk at once (no body: it
            // is vaporized). RetireWalker removes it and tells the clients; anybody else is ours to remove.
            bool walker = RetireWalker != null && world.Has<Pedestrian>(hit) && RetireWalker(mapId, world, hit);
            if (walker) ReplaceWalker?.Invoke(mapId, world, id);
            else if (world.IsAlive(hit)) { ForgetVehicle?.Invoke(mapId, id); _maps.DestroyEntity(mapId, hit); _server.BroadcastRemoval(mapId, id); }
            tell($"Vaporized {who}, id {id}.");
            return;
        }
        if (IsGroundLike(world, hit))
        {
            tell($"Not vaporized: {name} is floor or ground, and the map stands on it.");
            return;
        }
        var root = RootOf(world, lookup, hit);
        bool vehicle = root != hit || world.Has<VehicleComponent>(hit) || world.Has<DriveComponent>(hit)
            ? world.Has<VehicleComponent>(root) || world.Has<DriveComponent>(root) : false;
        var whole = vehicle ? root : hit;
        if (vehicle && Occupied(world, whole))
        {
            tell($"Not vaporized: somebody is in the {ThingName(world, whole)}. Freeze it, or get them out first.");
            return;
        }
        var gone = new List<Entity> { whole };
        if (vehicle || world.Has<CompositeComponent>(whole))
            world.Query(new QueryDescription().WithAll<ParentComponent>(), (Entity e, ref ParentComponent p) =>
            {
                if (p.ParentEntityId == whole.Id) gone.Add(e);
            });
        string what = ThingName(world, whole);
        string prefab = world.Has<IdentityComponent>(whole) ? world.Get<IdentityComponent>(whole).PrefabId ?? "" : "";
        var position = world.Has<Transform>(whole) ? world.Get<Transform>(whole).Position : at;
        _server.EmitWorldAudio(mapId, -1, "vaporize", new[] { AdminGun.HitSound(AdminGunMode.Vaporize, at) });
        if (vehicle) ForgetVehicle?.Invoke(mapId, whole.Id);
        foreach (var e in gone)
        {
            int id = e.Id;
            if (!world.IsAlive(e)) continue;
            _maps.DestroyEntity(mapId, e);
            _server.BroadcastRemoval(mapId, id);
        }
        _maps.RefreshGrid(mapId);
        Log.Warning("ADMIN GUN: {User} vaporized {What} (id {Id}, prefab {Prefab}, {Parts} entities) at {At} on {Map}. Gone until restart unless the map is saved.",
                    shooter.Username, what, whole.Id, prefab, gone.Count, PlayerCoordinates.Format(position), mapId);
        tell($"Vaporized {what}, id {whole.Id}{(gone.Count > 1 ? $", {gone.Count} parts" : "")}. Gone until the server restarts, unless the map is saved.");
    }

    /// <summary>Whether a part is something the map stands on: its name or prefab says floor, ground,
    /// terrain, foundation, road or pavement, or it is a wide flat slab.</summary>
    internal static bool IsGroundLike(World world, Entity e)
    {
        string name = (world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).Name + " " + world.Get<IdentityComponent>(e).PrefabId : "")
                    + " " + (world.Has<NameComponent>(e) ? world.Get<NameComponent>(e).Name : "");
        name = name.ToLowerInvariant();
        foreach (var word in new[] { "floor", "ground", "terrain", "foundation", "road", "pavement", "sidewalk", "street", "slab", "asphalt", "grass" })
            if (name.Contains(word)) return true;
        if (world.Has<ColliderComponent>(e))
        {
            var c = world.Get<ColliderComponent>(e);
            if (c.Shape == ColliderShape.Box && c.Size.Y <= 0.6f && c.Size.X >= 3f && c.Size.Z >= 3f) return true;
        }
        return false;
    }

    private static bool Occupied(World world, Entity root)
    {
        bool any = false;
        world.Query(new QueryDescription().WithAll<OccupantComponent>(), (ref OccupantComponent o) => { if (o.RootEntityId == root.Id) any = true; });
        return any;
    }

    /// <summary>The composite a part belongs to, or the thing itself.</summary>
    private static Entity RootOf(World world, Dictionary<int, Entity> lookup, Entity e)
    {
        for (int i = 0; i < 4 && world.Has<ParentComponent>(e); i++)
        {
            int parent = world.Get<ParentComponent>(e).ParentEntityId;
            if (parent < 0 || !lookup.TryGetValue(parent, out var up) || !world.IsAlive(up)) break;
            e = up;
        }
        return e;
    }

    /// <summary>A player by name, anybody else by what they are called.</summary>
    private static string Whom(World world, Entity e)
        => world.Has<PlayerComponent>(e) ? world.Get<PlayerComponent>(e).Username
         : world.Has<Pedestrian>(e) ? "pedestrian " + NameOf(world, e)
         : ThingName(world, e);

    // ── Inspecting ──────────────────────────────────────────────────────────────────────────────

    /// <summary>"Kestrel House north wall: part of Kestrel House, owned by cody. Id 412, prefab brick_wall,
    /// brick, at 150.0, 27.0, 3.2, 22 metres."</summary>
    internal static string InspectWords(World world, Dictionary<int, Entity> lookup, Entity e, float metres)
    {
        var root = RootOf(world, lookup, e);
        string name = world.Has<PlayerComponent>(e) ? world.Get<PlayerComponent>(e).Username : ThingName(world, e);
        string kind;
        string owner = "owned by nobody";
        if (world.Has<PlayerComponent>(e)) { kind = "a player"; owner = "their own"; }
        else if (world.Has<Pedestrian>(e)) kind = "somebody walking";
        else if (world.Has<VehicleComponent>(root) || world.Has<DriveComponent>(root))
        {
            string type = world.Has<VehicleComponent>(root) ? world.Get<VehicleComponent>(root).VehicleType : "";
            kind = root == e ? $"a vehicle{(type.Length > 0 ? ", " + type : "")}" : $"part of {ThingName(world, root)}, a vehicle{(type.Length > 0 ? ", " + type : "")}";
        }
        else if (world.Has<ItemComponent>(e)) kind = "an item";
        else if (world.Has<DoorComponent>(e)) kind = "a door";
        else if (root != e) kind = $"part of {ThingName(world, root)}";
        else if (world.Has<CompositeComponent>(e)) kind = "a group";
        else kind = "a fixed part of the map";
        if (world.Has<CompositeComponent>(root) && !string.IsNullOrWhiteSpace(world.Get<CompositeComponent>(root).Owner))
            owner = "owned by " + world.Get<CompositeComponent>(root).Owner;
        if (world.Has<HeldComponent>(e) && world.Get<HeldComponent>(e).HolderEntityId >= 0
            && lookup.TryGetValue(world.Get<HeldComponent>(e).HolderEntityId, out var holder) && world.IsAlive(holder))
            owner = $"carried by {Whom(world, holder)}";
        string prefab = world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).PrefabId)
            ? $", prefab {world.Get<IdentityComponent>(e).PrefabId}" : "";
        string material = world.Has<MaterialComponent>(e) ? (world.Get<MaterialComponent>(e).Material ?? "Generic").ToLowerInvariant() : "no material";
        string where = world.Has<Transform>(e) ? PlayerCoordinates.Format(world.Get<Transform>(e).Position) : "nowhere";
        string health = IsPerson(world, e) && world.Has<HealthComponent>(e) ? $", health {world.Get<HealthComponent>(e).Current}" : "";
        string frozen = world.Has<FrozenComponent>(e) ? ", frozen" : "";
        return $"{name}: {kind}, {owner}. "
             + $"Id {e.Id}{prefab}, {material}{health}{frozen}, at {where}, {MathF.Round(metres):F0} metres.";
    }
}
