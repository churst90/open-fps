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

/// <summary>
/// Firing, reloading, wounds and death.
///
/// A gun holds what its magazine holds and fires one round per press of the trigger, no faster than
/// its action can be worked; empty, it clicks. A reload is the routine of a pair of hands
/// (<see cref="WeaponHandling"/>), and the gun is not ready until that routine is over: the same
/// routine is the sound everybody near hears, so the click of the bolt going home and the moment you
/// can fire again are one moment.
///
/// A shot that lands on a person takes health, and the shooter alone is told, by a
/// <see cref="HitConfirm"/> that their client plays as a chime. A person at no health dies: they fall,
/// which everyone near hears; a player gets up again at the spawn after
/// <see cref="PlayerRespawnSeconds"/>, and a person in the street lies there for
/// <see cref="BodySeconds"/> and is then taken away, and somebody else comes walking along instead.
/// </summary>
public sealed class CombatService
{
    private readonly MapManager _maps;
    private readonly GameServer _server;
    private readonly SessionManager _sessions;

    /// <summary>Seconds, monotonic. A test sets its own.</summary>
    public Func<double> Clock { get; set; } = () => AudioClock.Now;

    /// <summary>Puts somebody new walking in a dead pedestrian's place, and returns them; null where
    /// nobody walks (a test, a map without traffic). The server hands this to the vehicle system.</summary>
    public Func<string, World, Entity, Entity>? ReplaceWalker { get; set; }

    public const double PlayerRespawnSeconds = 5;
    public const double BodySeconds = 10;

    /// <summary>Where a gun is held, in the body's frame: a reload is heard from the hands.</summary>
    private static readonly Vector3 Hands = new(0f, 1.25f, 0.35f);

    private sealed class Reloading
    {
        public required string MapId;
        public required int ItemId;
        public required WeaponDefinition Weapon;
        public required int Rounds;
        public required double DoneAt;
    }

    private readonly Dictionary<UserSession, Reloading> _reloads = new();
    private readonly Dictionary<UserSession, double> _lastShot = new();

    public CombatService(MapManager maps, GameServer server, SessionManager sessions)
    {
        _maps = maps;
        _server = server;
        _sessions = sessions;
    }

    private static void Say(Action<IMessage> reply, string text) => reply(new TextEvent { Text = text });

    private bool TryGetBody(UserSession session, Action<IMessage> reply, out World world,
                            out SpatialGrid<Entity> grid, out Dictionary<int, Entity> lookup, out Vector3 position)
    {
        position = default;
        if (!_maps.TryGetMap(session.CurrentMapId, out world, out _, out grid, out lookup))
        {
            Say(reply, $"Map '{session.CurrentMapId}' is not loaded.");
            return false;
        }
        if (session.Entity == Entity.Null || !world.IsAlive(session.Entity) || !world.Has<Transform>(session.Entity))
        {
            Say(reply, "You are not in the world yet. Type 'ready' to enter it first.");
            return false;
        }
        position = world.Get<Transform>(session.Entity).Position;
        return true;
    }

    /// <summary>Whether a player is reloading now. Their client is told nothing of it; this is for
    /// the server's own refusals and for tests.</summary>
    public bool IsReloading(UserSession session) => _reloads.ContainsKey(session);

    // ── Firing ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// /fire [weapon] — fires what is in your hands, at whatever is in front of you.
    ///
    /// Hands join a gun to a player, without an `EquippedWeaponComponent`: the weapon is the
    /// `ItemComponent.WeaponId` of the thing you are holding, so equipping a gun and picking one up
    /// are the same act, and putting it down disarms you with no bookkeeping anywhere.
    ///
    /// Naming a weapon out of the air is for those with the fire-any permission only — useful for
    /// hearing a model without first building a world to find a gun in — and spends no rounds, because
    /// there is no gun to spend them from.
    ///
    /// It also happens to be the only thing in the game that can currently break a window, which is
    /// why the glass path hangs off it too.
    /// </summary>
    public void Fire(UserSession session, string[] args, Action<IMessage> reply, bool canNameAny)
    {
        if (!TryGetBody(session, reply, out var world, out var grid, out var lookup, out var position)) return;
        if (world.Has<DeadComponent>(session.Entity)) { Say(reply, "You are dead."); return; }

        bool armed = HandsService.TryGetHeldWeapon(world, session.Entity, lookup, out var weapon, out var item);
        bool fromTheAir = false;

        // Naming one overrides what you are holding, and only a dev may do that. Everyone else fires
        // the thing in their hands or nothing.
        //
        // NAMING ONE IS THE WHOLE OF THE EXEMPTION. An admin who fires with EMPTY HANDS must not be
        // handed a weapon out of the air: a dev convenience that arms you silently is the same shape
        // of fault as a trigger on a screen reader's key — the sound happens and the player cannot
        // tell why. `/fire akm` is what the convenience is for.
        if (canNameAny && args.Length > 0)
        {
            string id = args[0];
            if (!WeaponRegistry.TryGet(id, out weapon))
            {
                Say(reply, $"No weapon called '{id}'. Try: {string.Join(", ", WeaponRegistry.All.Select(w => w.Id))}");
                return;
            }
            fromTheAir = true;
        }
        else if (!armed)
        {
            Say(reply, "You are not holding anything you can fire.");
            return;
        }

        var rotation = world.Get<Transform>(session.Entity).Rotation;
        var forward = Vector3.Transform(new Vector3(0, 0, 1), rotation);
        var muzzle = position + new Vector3(0, 1.5f, 0) + forward * 0.5f;

        if (!fromTheAir)
        {
            double now = Clock();
            if (_reloads.TryGetValue(session, out var pending))
            {
                if (now < pending.DoneAt) { Say(reply, $"Still reloading the {weapon.DisplayName}."); return; }
                FinishReload(session, pending, reply);
            }
            // A trigger pressed faster than the action can be worked does nothing: there is no round
            // in the chamber yet. Said nothing about, because the missing shot is the answer.
            if (_lastShot.TryGetValue(session, out double last) && now - last < weapon.SecondsBetweenShots - 1e-6) return;
            _lastShot[session] = now;

            ref var ammo = ref Arms.Ammo(world, item, weapon);
            if (ammo.Rounds <= 0)
            {
                // The hammer falls on nothing. Heard by everyone near, as it would be: an empty click
                // is the most important sound in a firefight to the person who is NOT holding the gun.
                _server.EmitWorldAudio(session.CurrentMapId, session.Entity.Id, "dry fire", new[]
                {
                    HandSound(position, forward, WeaponHandling.DryFireKey(weapon),
                              new HandlingSpec(weapon.Id, IsReload: false, 0, false)),
                });
                Say(reply, "Empty. R to reload.");
                return;
            }
            ammo.Rounds--;
        }

        // 1. The shot itself. Named rather than described, because a gunshot is a blast wave, a body
        //    resonance, a brightness sweep and the action working — and a model for that already
        //    exists and is better than four numbers.
        _server.EmitWorldAudio(session.CurrentMapId, session.Entity.Id, weapon.DisplayName, new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock,
                Position = muzzle,
                LevelDb = Loudness.MuzzleBlastDb(weapon),
                SynthKey = "weapon:" + weapon.Id,
                DecaySeconds = 0.6f,
            },
        });

        // 2. What it hit, if anything: the nearest thing in front of the muzzle that stops a round.
        //    A person is in front of you if they are within the cone measured flat, across the
        //    ground: the round comes from chest height and a person's position is their feet, so a
        //    cone in three dimensions missed anybody close enough to touch.
        string hit = "nothing in the first hundred metres";
        foreach (var candidate in grid.GetItemsInRadius(position, 100f).OrderBy(
                     e => Vector3.Distance(position, world.Get<Transform>(e).Position)))
        {
            if (candidate.Id == session.Entity.Id || !world.IsAlive(candidate) || !world.Has<ColliderComponent>(candidate)) continue;
            var t = world.Get<Transform>(candidate);
            var c = world.Get<ColliderComponent>(candidate);
            bool body = IsPerson(world, candidate);
            if (body && world.Has<DeadComponent>(candidate)) continue;   // a body on the ground stops nothing
            if (!c.IsSolid && !body) continue;

            var toTarget = t.Position - muzzle;
            if (body) toTarget.Y = 0f;
            float distance = toTarget.Length();
            if (distance < 0.1f) { if (!body) continue; toTarget = forward; distance = 0.1f; }
            var flatForward = body ? Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z)) : forward;
            if (Vector3.Dot(Vector3.Normalize(toTarget), flatForward) < 0.97f) continue;

            if (body)
            {
                string whom = NameOf(world, candidate);
                bool killed = Wound(session, world, grid, candidate, weapon.DamageAt(distance), weapon, reply);
                // The chime is the answer to a hit. A text player has no chime, so is told in words.
                if (session.IsTextClient) Say(reply, $"You fire the {weapon.DisplayName} and {(killed ? "kill" : "hit")} {whom}.");
                return;
            }

            string material = world.Has<MaterialComponent>(candidate)
                ? world.Get<MaterialComponent>(candidate).Material ?? "Generic" : "Generic";
            hit = $"{material} at {distance:F0} metres";

            if (material.Equals("Glass", StringComparison.OrdinalIgnoreCase))
                BreakGlass(session, world, candidate, t, c, weapon);
            else
                _server.EmitWorldAudio(session.CurrentMapId, candidate.Id, "impact",
                    ImpactAcoustics.Between(AcousticRegistry.GetProperties("Metal"),
                                            AcousticRegistry.GetProperties(material),
                                            t.Position, weapon.MuzzleVelocity * 0.02f,
                                            0.01f, 500f, c.Size.X, c.Size.Y, MathF.Max(0.02f, c.Size.Z)));
            break;
        }
        Say(reply, $"You fire the {weapon.DisplayName}. It hits {hit}.");
    }

    /// <summary>A person: a player, or somebody walking in the street. Things with health that are not
    /// people (a destructible crate) are not wounded or killed by this.</summary>
    public static bool IsPerson(World world, Entity e)
        => world.Has<PlayerComponent>(e) || world.Has<Pedestrian>(e);

    /// <summary>
    /// A window going out, which is two sounds most of two seconds apart and from two different places.
    ///
    /// The whole reason `GlassBreak` was worth writing: the break is up at the window, then nothing,
    /// then the glass arrives at the FOOT of the wall — and the gap between them is sqrt(2h/g), a
    /// direct readout of which floor the shot was on. One crash sample throws that away, and a sighted
    /// game would never notice it was gone.
    /// </summary>
    private void BreakGlass(UserSession session, World world, Entity pane, Transform t,
                            ColliderComponent collider, WeaponDefinition weapon)
    {
        var glass = new GlassPane(
            Centre: t.Position,
            Size: new Vector2(MathF.Max(0.3f, collider.Size.X), MathF.Max(0.3f, collider.Size.Y)),
            Normal: Vector3.Transform(new Vector3(0, 0, 1), t.Rotation),
            // Tempered: what modern glazing is, and the type that always fails completely rather
            // than taking a neat hole. Held in compression, so there is no such thing as a tidy
            // bullet hole in it.
            Type: GlassType.Tempered,
            HeightAboveGround: MathF.Max(0f, t.Position.Y - collider.Size.Y * 0.5f));

        Span<GlassEvent> buffer = stackalloc GlassEvent[48];
        int count = GlassBreak.Resolve(glass, t.Position, weapon, pane.Id, buffer);
        if (count == 0) return;

        var events = new List<GlassEvent>(count);
        for (int i = 0; i < count; i++) events.Add(buffer[i]);

        _server.EmitWorldAudio(session.CurrentMapId, pane.Id, "glass",
                               GlassSound.From(events, glass.Type, glass.Size,
                                               MathF.Max(0.003f, collider.Size.Z)));
        _maps.DestroyEntity(session.CurrentMapId, pane);
        _server.BroadcastRemoval(session.CurrentMapId, pane.Id);
    }

    // ── Wounds and death ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes health from a person, tells the shooter (and nobody else) by a <see cref="HitConfirm"/>,
    /// and kills them at none. A player who is hit is told so, with what they have left, because
    /// otherwise they cannot know: there is no red edge to the screen.
    /// </summary>
    public bool Wound(UserSession shooter, World world, SpatialGrid<Entity> grid, Entity target, int damage,
                      WeaponDefinition weapon, Action<IMessage> replyToShooter)
    {
        if (!world.Has<HealthComponent>(target)) world.Add(target, new HealthComponent { Current = 100, Max = 100 });
        ref var health = ref world.Get<HealthComponent>(target);
        if (health.Max <= 0) health.Max = 100;
        int before = health.Current;
        health.Current = Math.Max(0, health.Current - Math.Max(1, damage));
        bool killed = health.Current == 0;
        int left = health.Current, max = health.Max;

        replyToShooter(new HitConfirm { TargetEntityId = target.Id, Killed = killed });
        Log.Information("{Shooter} hit {Target} ({Id}) with the {Weapon} for {Damage}: {Before} to {After}.",
                        shooter.Username, NameOf(world, target), target.Id, weapon.DisplayName, damage, before, left);

        var victim = SessionOf(world, target);
        if (victim != null && !killed)
            _server.SendToSession(victim, new TextEvent { Text = $"You are hit. {left * 100 / Math.Max(1, max)} percent." });
        if (killed) Kill(shooter.CurrentMapId, world, grid, target, victim);
        return killed;
    }

    private void Kill(string mapId, World world, SpatialGrid<Entity> grid, Entity target, UserSession? victim)
    {
        world.Add(target, new DeadComponent { DiedAt = Clock() });
        if (world.Has<Velocity>(target)) world.Get<Velocity>(target).Linear = Vector3.Zero;
        if (victim != null)
        {
            _reloads.Remove(victim);
            _server.SendToSession(victim, new TextEvent { Text = "You died." });
        }
        Log.Information("{Target} ({Id}) died.", NameOf(world, target), target.Id);
        _server.EmitWorldAudio(mapId, target.Id, "a body falling", BodyFall(world, grid, world.Get<Transform>(target).Position));
    }

    /// <summary>
    /// A person going down: the knees and hips first, from about half a metre, and then the trunk
    /// from about a metre, nearly half a second later, onto whatever the floor is. The impact model
    /// that every dropped thing uses, with a body's softness and mass, so a fall on a wooden floor and
    /// one on wet grass are as different as they should be.
    /// </summary>
    public static List<TransientSound> BodyFall(World world, SpatialGrid<Entity> grid, Vector3 at)
    {
        float ground = PhysicsUtils.GetGroundHeight(world, grid, at + new Vector3(0, 0.5f, 0), out string floor);
        // No floor found under them (the probe says so with a height far below the world): they
        // fall where they stand.
        if (!float.IsFinite(ground) || ground < at.Y - 3f) { ground = at.Y; floor = "Generic"; }
        var where = new Vector3(at.X, ground, at.Z);
        var body = AcousticRegistry.GetProperties("Skin");
        var under = AcousticRegistry.GetProperties(string.IsNullOrEmpty(floor) ? "Generic" : floor);
        var sounds = new List<TransientSound>();
        sounds.AddRange(ImpactAcoustics.Between(body, under, where, MathF.Sqrt(2f * PhysicsConstants.Gravity * 0.5f),
                                                35f, 5000f, 2f, 2f, 0.2f));
        foreach (var s in ImpactAcoustics.Between(body, under, where + new Vector3(0, 0, 0.6f),
                                                  MathF.Sqrt(2f * PhysicsConstants.Gravity * 1.0f), 40f, 5000f, 2f, 2f, 0.2f))
        {
            var later = s;
            later.DelaySeconds += 0.45f;
            sounds.Add(later);
        }
        return sounds;
    }

    // ── Reloading ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// /reload — fills the gun in your hands from what you carry spare.
    ///
    /// Takes as long as the hands' routine takes (<see cref="WeaponHandling"/>), during which the gun
    /// cannot be fired; a magazine swap that leaves a round in the chamber is quicker than one from
    /// empty, which needs the bolt sent home as well. The rounds move when the routine ends, so a
    /// reload abandoned by putting the gun away moves none.
    /// </summary>
    public void Reload(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var lookup, out var position)) return;
        if (world.Has<DeadComponent>(session.Entity)) { Say(reply, "You are dead."); return; }
        if (!HandsService.TryGetHeldWeapon(world, session.Entity, lookup, out var weapon, out var item))
        { Say(reply, "You are not holding anything to reload."); return; }

        double now = Clock();
        if (_reloads.TryGetValue(session, out var pending))
        {
            if (now < pending.DoneAt) { Say(reply, $"You are already reloading the {pending.Weapon.DisplayName}."); return; }
            FinishReload(session, pending, reply);
        }

        var ammo = Arms.Ammo(world, item, weapon);
        var kind = Ammunition.Get(weapon.AmmoId);
        string spoken = kind?.SpokenName ?? weapon.AmmoId;
        if (ammo.Rounds >= ammo.Capacity)
        { Say(reply, $"The {weapon.DisplayName} is full, {Arms.RoundsWords(weapon, ammo.Rounds)}."); return; }
        int reserve = Arms.Reserve(world, session.Entity, weapon.AmmoId);
        if (reserve <= 0)
        { Say(reply, $"You have no {spoken} {kind?.Unit ?? "rounds"} left."); return; }

        int load = Math.Min(ammo.Capacity - ammo.Rounds, reserve);
        bool fromEmpty = ammo.Rounds == 0;
        float seconds = WeaponHandling.ReloadSeconds(weapon, load, fromEmpty);
        _reloads[session] = new Reloading
        {
            MapId = session.CurrentMapId, ItemId = item.Id, Weapon = weapon, Rounds = load, DoneAt = now + seconds,
        };

        var forward = Vector3.Transform(new Vector3(0, 0, 1), world.Get<Transform>(session.Entity).Rotation);
        var key = WeaponHandling.ReloadKey(weapon, load, fromEmpty);
        _server.EmitWorldAudio(session.CurrentMapId, session.Entity.Id, "reload", new[]
        {
            HandSound(position, forward, key, new HandlingSpec(weapon.Id, IsReload: true, load, fromEmpty)),
        });
        // The routine is the answer: its last click is the gun being ready. A text player hears none
        // of it, so is told it has begun.
        if (session.IsTextClient) Say(reply, $"You start reloading the {weapon.DisplayName}.");
    }

    /// <summary>A handling sound at the hands, following the body while it plays.</summary>
    private static TransientSound HandSound(Vector3 feet, Vector3 forward, string key, HandlingSpec spec) => new()
    {
        Character = SoundCharacter.Knock,
        Position = feet + new Vector3(0, Hands.Y, 0) + forward * Hands.Z,
        OnBody = true,
        BodyOffset = Hands,
        LevelDb = WeaponHandling.LevelDb(spec),
        SynthKey = key,
        DecaySeconds = WeaponHandling.Seconds(spec) + 0.25f,
        Noisiness = 1f,
    };

    /// <summary>
    /// The end of a reload: the rounds move from the reserve into the gun, as many as are still there
    /// and still fit. Abandoned if the gun is no longer in the player's hands.
    /// </summary>
    private void FinishReload(UserSession session, Reloading r, Action<IMessage>? reply)
    {
        _reloads.Remove(session);
        void Tell(string text)
        {
            if (reply != null) Say(reply, text);
            else _server.SendToSession(session, new TextEvent { Text = text });
        }
        if (!_maps.TryGetMap(r.MapId, out var world, out _, out _, out var lookup)) return;
        if (session.CurrentMapId != r.MapId || session.Entity == Entity.Null || !world.IsAlive(session.Entity)) return;
        if (!HandsService.TryGetHeldWeapon(world, session.Entity, lookup, out _, out var item) || item.Id != r.ItemId)
        { Tell($"You stop reloading the {r.Weapon.DisplayName}."); return; }

        ref var ammo = ref Arms.Ammo(world, item, r.Weapon);
        int reserve = Arms.Reserve(world, session.Entity, r.Weapon.AmmoId);
        int moved = Math.Min(r.Rounds, Math.Min(reserve, ammo.Capacity - ammo.Rounds));
        ammo.Rounds += Math.Max(0, moved);
        int rounds = ammo.Rounds;
        Arms.AddReserve(world, session.Entity, r.Weapon.AmmoId, -Math.Max(0, moved));
        int spare = Arms.Reserve(world, session.Entity, r.Weapon.AmmoId);
        Tell($"{Arms.RoundsWords(r.Weapon, rounds)}, {spare} spare.");
    }

    /// <summary>/ammo — what is in the gun in your hands, and what you carry spare.</summary>
    public string AmmoReadout(UserSession session)
    {
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out var lookup)
            || session.Entity == Entity.Null || !world.IsAlive(session.Entity))
            return "You are not in the world yet.";
        string spare = Arms.ReserveWords(world, session.Entity);
        string carried = spare.Length == 0 ? "You carry no spare ammunition." : $"You carry {spare}.";
        if (!HandsService.TryGetHeldWeapon(world, session.Entity, lookup, out var weapon, out var item))
            return $"You are not holding a gun. {carried}";
        int rounds = Arms.Ammo(world, item, weapon).Rounds;
        string reloading = _reloads.TryGetValue(session, out var r) && r.ItemId == item.Id ? " You are reloading it." : "";
        return $"The {weapon.DisplayName} has {(rounds <= 0 ? "nothing in it" : Arms.RoundsWords(weapon, rounds))}.{reloading} {carried}";
    }

    // ── Ammunition given by staff ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads "/give [NAME] [ammo] KIND [COUNT]": "9mm 60", "ammo 9mm 60", "sean 12 gauge 25". The
    /// count defaults to a box. False when the words do not name ammunition, so the caller can try
    /// them as an item instead.
    /// </summary>
    public static bool TryParseAmmo(IReadOnlyList<string> words, out AmmoType ammo, out int count)
    {
        ammo = null!; count = 0;
        var rest = words.Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        if (rest.Count > 0 && rest[0].ToLowerInvariant() is "ammo" or "ammunition" or "rounds") rest.RemoveAt(0);
        int? n = null;
        if (rest.Count >= 2 && int.TryParse(rest[^1], out int parsed)) { n = parsed; rest.RemoveAt(rest.Count - 1); }
        if (rest.Count == 0) return false;
        if (!Ammunition.TryFind(string.Join(" ", rest), out ammo) && !Ammunition.TryFind(string.Concat(rest), out ammo)) return false;
        count = n ?? ammo.BoxRounds;
        return true;
    }

    /// <summary>
    /// Gives a player spare rounds. Says "You gave sean 60 rounds of 9mm." to the giver and
    /// "cody gave you 60 rounds of 9mm." to the receiver, the same words as an item given.
    /// </summary>
    public bool GiveAmmo(UserSession giver, UserSession receiver, AmmoType ammo, int count, out string message)
    {
        message = "";
        if (count < 1 || count > 10000) { message = "Give between 1 and 10000 rounds."; return false; }
        if (!_maps.TryGetMap(receiver.CurrentMapId, out var world, out _, out _, out _)
            || receiver.Entity == Entity.Null || !world.IsAlive(receiver.Entity))
        { message = $"{receiver.Username} is not in the world just now."; return false; }
        Arms.AddReserve(world, receiver.Entity, ammo.Id, count);
        string what = Ammunition.Count(ammo, count);
        Log.Information("{Giver} gave {Receiver} {What}.", giver.Username, receiver.Username, what);
        if (receiver == giver) { message = $"You gave yourself {what}."; return true; }
        _server.SendToSession(receiver, new TextEvent { Text = $"{giver.Username} gave you {what}." });
        message = $"You gave {receiver.Username} {what}.";
        return true;
    }

    // ── Every tick ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finishes the reloads that are due on a map, gets dead players up again and takes dead
    /// pedestrians away. Run on the tick thread, once per map per tick.
    /// </summary>
    public void Update(string mapId, World world)
    {
        double now = Clock();
        if (_reloads.Count > 0)
            foreach (var (session, r) in _reloads.Where(kv => kv.Value.MapId == mapId && now >= kv.Value.DoneAt).ToList())
                FinishReload(session, r, null);

        List<Entity>? due = null;
        world.Query(new QueryDescription().WithAll<DeadComponent>(), (Entity e, ref DeadComponent dead) =>
        {
            double wait = world.Has<PlayerComponent>(e) ? PlayerRespawnSeconds : BodySeconds;
            if (now - dead.DiedAt >= wait) (due ??= new List<Entity>()).Add(e);
        });
        if (due == null) return;
        foreach (var e in due)
        {
            if (!world.IsAlive(e)) continue;
            if (world.Has<PlayerComponent>(e)) Respawn(mapId, world, e);
            else TakeAway(mapId, world, e);
        }
    }

    /// <summary>A dead player up again at the spawn, whole.</summary>
    private void Respawn(string mapId, World world, Entity e)
    {
        world.Remove<DeadComponent>(e);
        if (world.Has<HealthComponent>(e))
        {
            ref var h = ref world.Get<HealthComponent>(e);
            h.Current = h.Max > 0 ? h.Max : 100;
        }
        if (world.Has<OccupantComponent>(e)) CompositeService.Disembark(world, e);
        ref var t = ref world.Get<Transform>(e);
        t.Position = _maps.GetSpawnPoint(mapId).Position;
        t.IsDirty = true;
        var session = SessionOf(world, e);
        if (session == null) return;
        _server.SendToSession(session, new PlayerSpawned { EntityId = e.Id, SpawnTransform = t });
        _server.SendToSession(session, new TextEvent { Text = "You are back on your feet at the start." });
        Log.Information("{User} respawned.", session.Username);
    }

    /// <summary>A dead pedestrian taken away, and somebody else walking in their place.</summary>
    private void TakeAway(string mapId, World world, Entity e)
    {
        if (ReplaceWalker != null && world.Has<Pedestrian>(e))
        {
            ReplaceWalker(mapId, world, e);
            return;
        }
        _maps.DestroyEntity(mapId, e);
        _server.BroadcastRemoval(mapId, e.Id);
    }

    private UserSession? SessionOf(World world, Entity e)
        => world.Has<PlayerComponent>(e) && _sessions.TryGetSession(world.Get<PlayerComponent>(e).ConnectionId, out var s) ? s : null;

    private static string NameOf(World world, Entity e)
        => world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
            ? world.Get<IdentityComponent>(e).Name
            : world.Has<NameComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<NameComponent>(e).Name)
                ? world.Get<NameComponent>(e).Name
                : world.Has<PlayerComponent>(e) ? world.Get<PlayerComponent>(e).Username : "someone";

    /// <summary>Forgets a player who has gone: their reload and their last shot.</summary>
    public void Forget(UserSession session)
    {
        _reloads.Remove(session);
        _lastShot.Remove(session);
    }

    /// <summary>The weapon in a player's hands and its rounds, for the client's keys: ("", -1) with none.</summary>
    public static (string WeaponId, int Rounds) Held(World world, Entity player, Dictionary<int, Entity> lookup)
        => HandsService.TryGetHeldWeapon(world, player, lookup, out var weapon, out var item)
            ? (weapon.Id, Arms.Ammo(world, item, weapon).Rounds)
            : ("", -1);
}
