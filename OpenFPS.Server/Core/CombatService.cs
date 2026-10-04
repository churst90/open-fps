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
        var muzzle = position + new Vector3(0, HipHeight, 0) + forward * 0.5f;

        if (!fromTheAir && !SpendRound(session, world, weapon, item, position, forward, reply)) return;

        // 1. The shot itself. Named rather than described, because a gunshot is a blast wave, a body
        //    resonance, a brightness sweep and the action working — and a model for that already
        //    exists and is better than four numbers.
        EmitReport(session, weapon, position, forward, muzzle, cycle: !fromTheAir);

        // 2. The round, flown like every other: along the aim, from the body's own axis at the height
        //    the gun is held, so a wall between you and your muzzle is the first thing it meets. What it
        //    hits is said when it gets there (FlyBullets). This was a hit-scan: the nearest entity by its
        //    CENTRE inside a flat 14-degree cone, a body taking the hit and a solid stopping it. A long
        //    wall's centre is metres away along it, so from the second floor of Brandt Court the floor's
        //    own east wall (88 m long, its centre 40 m off) was never in the cone and the pedestrian
        //    below in the street was (Cody, 2026-10-04).
        //
        //    Aim assistance first, if the player has it on (/aimassist, on by default): somebody near the
        //    aim and in plain view turns the gun onto them. Then the hip's own scatter, and the round is
        //    flown as ever, so what it does on the way is still the world's to decide.
        Vector3 from = position + new Vector3(0f, HipHeight, 0f);
        Vector3 aim = forward;
        if (session.AimAssist && Assist(session, world, grid, weapon, from, forward, out var onto, out var whom))
        {
            aim = onto;
            Log.Information("{User}'s aim assisted onto {Whom} ({Id}), {Degrees:F1} degrees off.", session.Username,
                            NameOf(world, whom), whom.Id, MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(forward), onto), -1f, 1f)) * 180f / MathF.PI);
        }
        aim = Disperse(aim, HipDispersionRadians);
        Launch(session, world, grid, weapon, from, aim * weapon.MuzzleVelocity, from, forward, targetId: -1);
        if (session.IsTextClient) Say(reply, $"You fire the {weapon.DisplayName}.");
    }

    // ── Aim assistance ──────────────────────────────────────────────────────────────────────────

    /// <summary>The widest the assist reaches, either side of the aim, measured flat: the old hit-scan
    /// cone's 14 degrees (a dot of 0.97), so close in it forgives what that forgave.</summary>
    public static readonly float AssistHalfAngle = MathF.Acos(0.97f);

    /// <summary>
    /// Past about 16 m the cone stops widening: the assist reaches this far either side of the aim line
    /// and no further, about nine torso widths. At 37 m that is 6 degrees (a body is 0.7 degrees
    /// across there and a fine turn is 1), at 100 m 2.3, at 300 m 0.8: a person standing a street's
    /// width from where you point is not "near the aim" however far off they are.
    /// </summary>
    public const float AssistHalfWidthMetres = 4f;

    /// <summary>Nobody further than this is assisted onto: past it a shot from the hip is a guess.</summary>
    public const float AssistRangeMetres = 300f;

    /// <summary>Centre mass, above the feet: the middle of the chest.</summary>
    public const float CentreMassHeight = 1.2f;
    /// <summary>The middle of the head, above the feet.</summary>
    public const float HeadHeight = 1.67f;

    /// <summary>
    /// Console-style aim assistance for a shot from the hip. Of the living people (players and people in
    /// the street) inside the assist cone of the aim, measured flat as the old cone was, and in plain
    /// view of the muzzle (the round's own collider test from the muzzle to their chest: a wall, a car or
    /// another person in the way means no; glass does not, it is see-through), the one nearest the aim
    /// line, and of those equally near the nearest in distance. The gun is turned onto their chest, or
    /// their head if the aim already passed above their shoulders, led for their walk and held up for
    /// the drop over the time the round takes to get there (still air: the wind is the shooter's
    /// problem). Nothing about the round is changed: it is flown from there like any other.
    /// </summary>
    private bool Assist(UserSession session, World world, SpatialGrid<Entity> grid, WeaponDefinition weapon,
                        Vector3 from, Vector3 forward, out Vector3 aim, out Entity target)
    {
        aim = forward; target = Entity.Null;
        Vector3 flat = new(forward.X, 0f, forward.Z);
        if (flat.LengthSquared() < 1e-6f) return false;
        flat = Vector3.Normalize(flat);
        Vector3 f = Vector3.Normalize(forward);
        int self = session.Entity.Id;
        int ride = world.Has<OccupantComponent>(session.Entity) ? world.Get<OccupantComponent>(session.Entity).RootEntityId : -1;

        var candidates = new List<(Entity E, float Angle, float Distance, Vector3 Point)>();
        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (e.Id == self || !IsPerson(world, e) || world.Has<DeadComponent>(e)) return;
            Vector3 toward = t.Position - from;
            Vector3 across = new(toward.X, 0f, toward.Z);
            float d = across.Length();
            if (d < 0.3f || d > AssistRangeMetres) return;
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(across / d, flat), -1f, 1f));
            float reach = MathF.Min(AssistHalfAngle, MathF.Atan(AssistHalfWidthMetres / d));
            if (angle > reach) return;
            // Where the aim passes them, in height: above their shoulders means the head.
            float aimHeight = from.Y + f.Y / MathF.Max(1e-4f, new Vector2(f.X, f.Z).Length()) * d - t.Position.Y;
            float height = aimHeight >= ExternalBallistics.HeadFrom ? HeadHeight : CentreMassHeight;
            candidates.Add((e, angle, d, t.Position + new Vector3(0f, height, 0f)));
        });
        // Nearest the aim line first, a quarter of a degree being as near as makes no difference; then nearest.
        foreach (var c in candidates.OrderBy(c => MathF.Round(c.Angle / (0.25f * MathF.PI / 180f))).ThenBy(c => c.Distance))
        {
            if (!InPlainView(self, ride, world, grid, from, c.Point, c.E)) continue;
            target = c.E;
            Vector3 vel = world.Has<Velocity>(c.E) ? world.Get<Velocity>(c.E).Linear : Vector3.Zero;
            aim = Onto(weapon, from, c.Point, vel, AirOn(session.CurrentMapId).Air);
            return true;
        }
        return false;
    }

    /// <summary>Whether a round from <paramref name="from"/> would reach <paramref name="target"/> at
    /// <paramref name="point"/> with nothing but glass in the way: the flight's own test, in 8 m pieces.</summary>
    private bool InPlainView(int self, int ride, World world, SpatialGrid<Entity> grid, Vector3 from, Vector3 point, Entity target)
    {
        var passed = new HashSet<int>();
        Vector3 dir = point - from;
        float length = dir.Length();
        if (length < 1e-4f) return true;
        dir /= length;
        double now = Clock();
        for (float s = 0f; s < length; s += 8f)
        {
            Vector3 a = from + dir * s, b = from + dir * MathF.Min(length, s + 8f);
            while (FirstHit(self, ride, world, grid, a, b, 0f, now, now, passed, out var hit, out _, out _, out _))
            {
                if (hit == target) return true;
                if (!IsGlass(world, hit)) return false;
                passed.Add(hit.Id);
            }
        }
        return true;
    }

    /// <summary>
    /// The direction that puts a round from <paramref name="from"/> onto <paramref name="point"/> on a
    /// body walking at <paramref name="velocity"/>: aimed at where they will be when it arrives, and above
    /// it by what it drops on the way. Three passes of flying it and moving the aim by the miss.
    /// </summary>
    internal static Vector3 Onto(WeaponDefinition weapon, Vector3 from, Vector3 point, Vector3 velocity, Air air)
    {
        float bc = ExternalBallistics.CoefficientOf(weapon);
        Vector3 aimAt = point;
        for (int pass = 0; pass < 3; pass++)
        {
            Vector3 dir = Vector3.Normalize(aimAt - from);
            var s = new BulletState { Position = from, Velocity = dir * weapon.MuzzleVelocity };
            Vector3 flat = Vector3.Normalize(new Vector3(dir.X, 0f, dir.Z) + new Vector3(0f, 0f, 1e-6f));
            float range = Vector3.Dot(new Vector3(point.X - from.X, 0f, point.Z - from.Z), flat);
            BulletState before = s;
            while (Vector3.Dot(s.Position - from, flat) < range && s.Seconds < MaxFlightSeconds)
            {
                before = s;
                ExternalBallistics.Advance(ref s, 0.002f, bc, air, Vector3.Zero);
            }
            float da = Vector3.Dot(before.Position - from, flat), db = Vector3.Dot(s.Position - from, flat);
            float u = Math.Clamp((range - da) / MathF.Max(1e-6f, db - da), 0f, 1f);
            Vector3 there = Vector3.Lerp(before.Position, s.Position, u);
            float seconds = before.Seconds + u * (s.Seconds - before.Seconds);
            Vector3 target = point + velocity * seconds;
            aimAt += target - there;
        }
        return Vector3.Normalize(aimAt - from);
    }

    /// <summary>How high above the feet a gun fired from the hip is held: where the report has always
    /// come from.</summary>
    public const float HipHeight = 1.5f;

    /// <summary>
    /// How far a shot from the hip strays from where the body points, radians, one standard deviation on
    /// each axis. Point shooting (unsighted, the gun held where the body faces) is trained to an 8-inch
    /// group at 7 yards, nearly all inside 10 cm of the point of aim at 6.4 m; a round Gaussian puts 90 %
    /// inside 2.15 σ, so σ = 0.10 / 2.15 / 6.4 = 7 mrad, 0.4°. The gun's own precision (2-4 MOA, about
    /// 1 mrad) is lost under it. A test sets its own.
    /// </summary>
    public float HipDispersionRadians { get; set; } = 0.007f;

    /// <summary>The scatter's dice. A test sets its own.</summary>
    public Random Scatter { get; set; } = new();

    /// <summary>A direction turned off by a Gaussian error of <paramref name="sigma"/> radians on each
    /// of its two cross axes.</summary>
    private Vector3 Disperse(Vector3 direction, float sigma) => Disperse(direction, sigma, Scatter);

    internal static Vector3 Disperse(Vector3 direction, float sigma, Random scatter)
    {
        direction = Vector3.Normalize(direction);
        if (sigma <= 0f) return direction;
        Vector3 right = Vector3.Cross(Vector3.UnitY, direction);
        right = right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);
        Vector3 up = Vector3.Cross(direction, right);
        float Gauss() => (float)(Math.Sqrt(-2 * Math.Log(1 - scatter.NextDouble())) * Math.Cos(2 * Math.PI * scatter.NextDouble()));
        return Vector3.Normalize(direction + right * (sigma * Gauss()) + up * (sigma * Gauss()));
    }

    /// <summary>
    /// The trigger, for a gun in the hands: refused while reloading or faster than the action can be
    /// worked, a dry click when empty, and otherwise one round spent. False when no shot is fired.
    /// </summary>
    private bool SpendRound(UserSession session, World world, WeaponDefinition weapon, Entity item,
                            Vector3 position, Vector3 forward, Action<IMessage> reply)
    {
        double now = Clock();
        if (_reloads.TryGetValue(session, out var pending))
        {
            if (now < pending.DoneAt) { Say(reply, $"Still reloading the {weapon.DisplayName}."); return false; }
            FinishReload(session, pending, reply);
        }
        // A trigger pressed faster than the action can be worked does nothing: there is no round
        // in the chamber yet. Said nothing about, because the missing shot is the answer.
        if (_lastShot.TryGetValue(session, out double last) && now - last < weapon.SecondsBetweenShots - 1e-6) return false;
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
            return false;
        }
        ammo.Rounds--;
        return true;
    }

    /// <summary>
    /// The report at the muzzle, and for a gun worked by hand the bolt worked after it: the sound of
    /// the next round going in is part of a bolt gun's shot, and the gap before the next is that long.
    /// </summary>
    private void EmitReport(UserSession session, WeaponDefinition weapon, Vector3 feet, Vector3 forward, Vector3 muzzle, bool cycle)
    {
        var sounds = new List<TransientSound>
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock,
                Position = muzzle,
                // At the muzzle on the shooter's body as it is when heard, as a clap is: placed where the
                // server had the body, a shot fired while walking came from a step behind you (Cody,
                // 2026-10-04: "my gun shots are lagging behind me when I move").
                OnBody = true,
                BodyOffset = new Vector3(0f, 1.5f, 0.5f),
                LevelDb = Loudness.MuzzleBlastDb(weapon),
                SynthKey = "weapon:" + weapon.Id,
                DecaySeconds = 0.6f,
            },
        };
        if (cycle && WeaponHandling.CyclesByHand(weapon))
        {
            var spec = new HandlingSpec(weapon.Id, IsReload: false, 0, false, IsCycle: true);
            var bolt = HandSound(feet, forward, WeaponHandling.CycleKey(weapon), spec);
            bolt.DelaySeconds = WeaponHandling.CycleAfterShotSeconds;
            sounds.Add(bolt);
        }
        _server.EmitWorldAudio(session.CurrentMapId, session.Entity.Id, weapon.DisplayName, sounds);
    }

    // ── A flown bullet ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The air and the wind on a map now: temperature (°C), pressure (mb), the sustained wind and its
    /// gustiness. The server hands this over from its weather; a test sets its own. Null is a still,
    /// standard day.
    /// </summary>
    public Func<string, (float TemperatureC, float PressureMb, Vector3 Wind, float Gustiness)>? Weather { get; set; }

    /// <summary>How far off the server's own heading a scoped shot's aim may be, radians, before the
    /// server keeps its own: a few degrees, the lag of the look inputs plus the sway, and no more.</summary>
    public const float MaxAimDisagreement = 5f * MathF.PI / 180f;

    /// <summary>How long a bullet is followed, seconds: well past 1500 m for a .308.</summary>
    public const float MaxFlightSeconds = 4f;

    /// <summary>The segment a bullet is tested over, seconds: 8 m of a .308's flight, in which a
    /// walking person moves a centimetre.</summary>
    public const float SegmentSeconds = 0.01f;

    /// <summary>The eye above the feet when looking through a scope: the client's eye height.</summary>
    public const float EyeHeight = 1.7f;

    private sealed class Flight
    {
        public required string MapId;
        public required UserSession Shooter;
        public required int ShooterId;
        public required WeaponDefinition Weapon;
        public BulletState State;
        public required Vector3 Eye;            // where it is measured from: the scope's eye, or the hip
        public required Vector3 Sight;          // the line of sight, unit
        public required double At;              // server time the state is true at
        public int TargetId = -1;               // whom the crosshair was nearest, for the spotter's call
        public int RideId = -1;                 // what the shooter is riding in, which the round leaves
        public bool Called;
        public readonly HashSet<int> BrokenGlass = new();
        /// <summary>What it is now: the bullet, or after a ricochet a tumbling slug.</summary>
        public Slug Slug;
        /// <summary>How many times it has ricocheted.</summary>
        public int Bounces;
        /// <summary>The dice for its ricochets, so the flight flown ahead and the real one agree.</summary>
        public int Seed;
        /// <summary>Its energy at the muzzle, J: what a ricochet's slug is weighed against when it hits.</summary>
        public float MuzzleJoules;
    }

    /// <summary>How many times one round may ricochet: a slug that has skipped twice has little left.</summary>
    public const int MaxBounces = 2;

    private readonly List<Flight> _flights = new();
    private readonly List<Entity> _near = new();
    private readonly HashSet<Entity> _nearSeen = new();

    /// <summary>Bullets in the air now. For tests.</summary>
    public int BulletsInFlight => _flights.Count;

    /// <summary>
    /// A shot through a scope: the round is FLOWN, not hit-scanned. It leaves the barrel raised over
    /// the line of sight by the rifle's zero and the turret, slows by its ballistic coefficient, falls
    /// at 9.81 m/s² and is carried by the wind, and whatever it meets first is hit when the bullet gets
    /// there — 0.8 s later at 600 m, by which time a walking target has moved a metre. Leading the
    /// target and dialling the drop are the shooter's to do; nothing here helps with either.
    /// </summary>
    public void FireScoped(UserSession session, ScopedShot shot, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var lookup, out var position)) return;
        if (world.Has<DeadComponent>(session.Entity)) { Say(reply, "You are dead."); return; }
        if (!HandsService.TryGetHeldWeapon(world, session.Entity, lookup, out var weapon, out var item))
        { Say(reply, "You are not holding anything you can fire."); return; }
        if (!ScopeRegistry.TryGet(ScopeOf(weapon), out var scope))
        {
            // No scope on it: an ordinary shot from the hip.
            Fire(session, Array.Empty<string>(), reply, canNameAny: false);
            return;
        }

        // The aim: the client's, unless it is further from the server's own than lag and sway explain.
        float yaw = shot.Yaw, pitch = shot.Pitch;
        if (world.Has<PlayerComponent>(session.Entity))
        {
            var p = world.Get<PlayerComponent>(session.Entity);
            bool finite = float.IsFinite(yaw) && float.IsFinite(pitch);
            if (!finite || MathF.Abs(MathHelper.WrapAngle(yaw - p.Yaw)) > MaxAimDisagreement
                        || MathF.Abs(pitch - p.Pitch) > MaxAimDisagreement)
            {
                Log.Warning("{User}'s scoped aim {Yaw:F4},{Pitch:F4} is too far from the server's {SYaw:F4},{SPitch:F4}; using the server's.",
                            session.Username, yaw, pitch, p.Yaw, p.Pitch);
                yaw = p.Yaw; pitch = p.Pitch;
            }
        }
        Vector3 sight = ScopeMath.Forward(yaw, pitch);
        Vector3 flatForward = Vector3.Normalize(new Vector3(sight.X, 0f, sight.Z) + new Vector3(0f, 0f, 1e-6f));
        if (!SpendRound(session, world, weapon, item, position, flatForward, reply)) return;

        Vector3 eye = position + new Vector3(0f, EyeHeight, 0f);
        float elevation = Math.Clamp(float.IsFinite(shot.ElevationMil) ? shot.ElevationMil : 0f, 0f, scope.MaxElevationMil);
        float angle = ExternalBallistics.ZeroAngle(weapon, scope.BaseZeroMetres, scope.SightHeightMetres) + elevation / 1000f;
        Vector3 barrel = ExternalBallistics.Raise(sight, angle);
        // The bore is under the scope, and the muzzle most of a metre ahead of the eye.
        Vector3 down = -Vector3.Normalize(Vector3.Cross(sight, Vector3.Normalize(Vector3.Cross(Vector3.UnitY, sight) + new Vector3(1e-6f, 0f, 0f))));
        Vector3 bore = eye + down * scope.SightHeightMetres;

        EmitReport(session, weapon, position, flatForward, bore + sight * 0.7f, cycle: true);

        _maps.TryGetMap(session.CurrentMapId, out _, out _, out SpatialGrid<Entity> grid, out _);
        Launch(session, world, grid, weapon, bore, barrel * weapon.MuzzleVelocity, eye, sight,
               NearestToCrosshair(world, session.Entity.Id, eye, yaw, pitch, scope.FieldOfViewDegrees(shot.Magnification)));
        Log.Information("{User} fires the {Weapon} through the scope at {Mag}x, {Mil:F1} mil up; aim {Yaw:F4},{Pitch:F4}.",
                        session.Username, weapon.DisplayName, shot.Magnification, elevation, yaw, pitch);
    }

    /// <summary>
    /// A round leaves the gun: it is put in the air to be flown a tick at a time (FlyBullets), and
    /// everybody else near where it is going is sent what they will hear of it go by.
    /// </summary>
    private void Launch(UserSession session, World world, SpatialGrid<Entity> grid, WeaponDefinition weapon,
                        Vector3 from, Vector3 velocity, Vector3 eye, Vector3 sight, int targetId)
    {
        var flight = new Flight
        {
            MapId = session.CurrentMapId,
            Shooter = session,
            ShooterId = session.Entity.Id,
            Weapon = weapon,
            State = new BulletState { Position = from, Velocity = velocity },
            Eye = eye,
            Sight = sight,
            At = Clock(),
            TargetId = targetId,
            RideId = world.Has<OccupantComponent>(session.Entity) ? world.Get<OccupantComponent>(session.Entity).RootEntityId : -1,
            Slug = Slug.Of(weapon),
            Seed = Scatter.Next(),
        };
        flight.MuzzleJoules = 0.5f * flight.Slug.MassKg * velocity.LengthSquared();
        _flights.Add(flight);
        PassingSounds(flight, world, grid);
    }

    // ── What the others hear go by ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The crack or the whizz of a round going by, for every other player near its path, each sent
    /// only to the one it is for and all of them NOW, with the trigger: the round is flown ahead here
    /// against the world as it stands (<see cref="FlyAhead"/>), and each sound carries its own delay
    /// from the shot (<see cref="BulletFlyby"/>). Sent with the report, the gap between the two is
    /// exactly the physics; sent when the bullet got there, it would be a tick late and the report
    /// would not be. A round that the real flight stops sooner than this one did (somebody stepped into
    /// it) has already been heard further on: rare, and a crack that did not happen is the lesser fault.
    /// </summary>
    private void PassingSounds(Flight f, World world, SpatialGrid<Entity> grid)
    {
        List<UserSession>? listeners = null;
        foreach (var s in _sessions.GetSessionsInMap(f.MapId))
        {
            if (s.IsTextClient || s.Entity == Entity.Null || !world.IsAlive(s.Entity) || !world.Has<Transform>(s.Entity)) continue;
            (listeners ??= new()).Add(s);
        }
        if (listeners == null) return;

        var (air, wind) = AirOn(f.MapId);
        var legs = FlyAhead(f, world, grid, air, wind);
        foreach (var s in listeners)
        {
            Vector3 ear = world.Get<Transform>(s.Entity).Position + new Vector3(0f, EyeHeight, 0f);
            // The round from the muzzle goes by everybody but the shooter; a ricochet's slug flies off
            // somewhere new, and the shooter may be anywhere near that.
            var sounds = s == f.Shooter ? new List<TransientSound>() : BulletFlyby.Sounds(legs[0].Path, ear, f.Weapon, air);
            for (int i = 1; i < legs.Count; i++) sounds.AddRange(RicochetSounds(legs[i], ear, air));
            if (sounds.Count == 0) continue;
            _server.SendToSession(s, new WorldAudioEvent
            {
                SourceEntityId = -1,
                Label = "a bullet going by",
                Sounds = sounds,
                Seed = _passSeed++,
            });
            Log.Information("{User} hears the {Weapon}'s round go by: {Keys}.", s.Username, f.Weapon.DisplayName,
                            string.Join(", ", sounds.Select(x => $"{x.SynthKey} {x.LevelDb:F0} dB at +{x.DelaySeconds:F3} s")));
        }
    }

    private int _passSeed = 1;

    /// <summary>The air and the wind on a map now.</summary>
    private (Air Air, Vector3 Wind) AirOn(string mapId)
    {
        var weather = Weather?.Invoke(mapId) ?? (15f, 1013.25f, Vector3.Zero, 0f);
        // Out in the open, where a long shot is taken: the full wind, gusts and all.
        return (new Air(weather.TemperatureC, weather.PressureMb), WindModel.Felt(weather.Wind, weather.Gustiness, Clock()));
    }

    /// <summary>
    /// A round's whole flight, flown ahead of time against the world as it stands now (people carried on
    /// at the pace they are walking), to where it stops or its time runs out: through glass, as the real
    /// one goes, and with nothing broken or hurt.
    /// </summary>
    /// <summary>One stretch of a round's flight between ricochets: its samples (times from the shot),
    /// and for a stretch after a ricochet the slug it is, where it left the face, how and when.</summary>
    private sealed record Leg(List<FlightSample> Path, Slug Slug, Vector3 From, Vector3 Velocity, float Seconds);

    private List<Leg> FlyAhead(Flight f, World world, SpatialGrid<Entity> grid, Air air, Vector3 wind)
    {
        var state = f.State;
        float t0 = state.Seconds;
        var slug = f.Slug;
        int bounces = f.Bounces;
        var legs = new List<Leg> { new(new List<FlightSample> { new(0f, state.Position, state.Velocity) }, slug, state.Position, state.Velocity, 0f) };
        var path = legs[0].Path;
        var glass = new HashSet<int>();
        double now = f.At;
        while (state.Seconds - t0 < MaxFlightSeconds && state.Position.Y >= PhysicsConstants.MapMinimumY)
        {
            var before = state;
            Fly(ref state, slug, f.Weapon, SegmentSeconds, air, wind);
            double segStart = now + (before.Seconds - t0);
            if (FirstHit(f, world, grid, before.Position, state.Position, SegmentSeconds, segStart, now, glass,
                         out var hit, out float along, out bool body, out _))
            {
                float length = Vector3.Distance(before.Position, state.Position);
                float u = along / MathF.Max(1e-6f, length);
                var at = new FlightSample(before.Seconds - t0 + u * SegmentSeconds,
                                          Vector3.Lerp(before.Position, state.Position, u), Vector3.Lerp(before.Velocity, state.Velocity, u));
                if (!body && !IsGlass(world, hit) && TryRicochet(f.Seed, bounces, slug, world, hit, at.Position, at.Velocity, out var o, out var normal, out _))
                {
                    path.Add(at);
                    slug = o.Slug;
                    bounces++;
                    state = new BulletState { Position = at.Position + normal * 0.002f, Velocity = o.Velocity,
                                              Seconds = t0 + at.Seconds, Travelled = before.Travelled + along };
                    var leg = new Leg(new List<FlightSample> { new(at.Seconds, state.Position, state.Velocity) }, slug, state.Position, state.Velocity, at.Seconds);
                    legs.Add(leg);
                    path = leg.Path;
                    continue;
                }
                if (body || !IsGlass(world, hit)) { path.Add(at); return legs; }
                glass.Add(hit.Id);
                state.Velocity *= 0.9f;
            }
            path.Add(new FlightSample(state.Seconds - t0, state.Position, state.Velocity));
            if (slug.Tumbling && state.Velocity.LengthSquared() < MinSlugSpeed * MinSlugSpeed) return legs;
        }
        return legs;
    }

    /// <summary>A tumbling slug slower than this is followed no further: it is falling, not flying.</summary>
    public const float MinSlugSpeed = 15f;

    /// <summary>Flies a round on: a bullet by its ballistic coefficient, a tumbling slug by its own drag.</summary>
    private static void Fly(ref BulletState state, Slug slug, WeaponDefinition weapon, float seconds, Air air, Vector3 wind)
    {
        if (slug.Tumbling) Ricochet.Advance(ref state, seconds, slug, air, wind);
        else ExternalBallistics.Advance(ref state, seconds, ExternalBallistics.CoefficientOf(weapon), air, wind);
    }

    /// <summary>What one listener hears of a ricochet's slug: its crack while it is still faster than
    /// sound (Whitham's law on the flattened slug, an estimate for a blunt body), and its whine.</summary>
    private static List<TransientSound> RicochetSounds(Leg leg, Vector3 ear, Air air)
    {
        var sounds = BulletFlyby.CrackSounds(leg.Path, ear, leg.Slug.Across, leg.Slug.Length, air);
        float range = 0f;
        for (int i = 1; i < leg.Path.Count; i++) range += Vector3.Distance(leg.Path[i - 1].Position, leg.Path[i].Position);
        sounds.AddRange(Ricochet.WhineSounds(leg.From, leg.Velocity, leg.Slug, range, leg.Seconds, ear, air));
        return sounds;
    }

    /// <summary>
    /// Whether a round meeting <paramref name="hit"/> at <paramref name="at"/> ricochets off it: by the
    /// face's material and the grazing angle (<see cref="Ricochet.TryBounce"/>), with dice that are the
    /// flight's own for this bounce, so the flight flown ahead and the real one bounce alike.
    /// </summary>
    private static bool TryRicochet(int seed, int bounces, Slug slug, World world, Entity hit, Vector3 at, Vector3 velocity,
                                    out Ricochet.Outcome outcome, out Vector3 normal, out Vector3 face)
    {
        outcome = default;
        Face(world, hit, at, velocity, out normal, out face);
        if (bounces >= MaxBounces || IsPerson(world, hit)) return false;
        var dice = new Random((int)(BulletFlyby.Mix((uint)seed, (uint)bounces + 1u) & 0x7fffffff));
        return Ricochet.TryBounce(MaterialOf(world, hit), velocity, normal, slug, dice, out outcome);
    }

    private static string MaterialOf(World world, Entity e)
        => world.Has<MaterialComponent>(e) ? world.Get<MaterialComponent>(e).Material ?? "Generic" : "Generic";

    /// <summary>
    /// The face of a part a round met at <paramref name="at"/>: its outward normal (against the round's
    /// way), and its size as width, height and thickness, the thickness being the part's extent along
    /// the normal (a wall's face is its length by its height, a floor's its length by its depth).
    /// </summary>
    internal static void Face(World world, Entity e, Vector3 at, Vector3 velocity, out Vector3 normal, out Vector3 size)
    {
        normal = -Vector3.Normalize(velocity + new Vector3(0f, 0f, 1e-9f));
        size = new Vector3(1f, 1f, 0.1f);
        if (!world.Has<Transform>(e) || !world.Has<ColliderComponent>(e)) return;
        var t = world.Get<Transform>(e);
        var c = world.Get<ColliderComponent>(e);
        Vector3 rel = at - t.Position;
        if (c.Shape == ColliderShape.Box)
        {
            var inverse = Quaternion.Inverse(t.Rotation);
            Vector3 local = Vector3.Transform(rel, inverse);
            Vector3 half = Vector3.Max(c.Size * 0.5f, new Vector3(1e-4f));
            float ex = MathF.Abs(local.X) / half.X, ey = MathF.Abs(local.Y) / half.Y, ez = MathF.Abs(local.Z) / half.Z;
            Vector3 axis;
            if (ex >= ey && ex >= ez) { axis = new Vector3(MathF.Sign(local.X), 0f, 0f); size = new Vector3(c.Size.Z, c.Size.Y, c.Size.X); }
            else if (ey >= ez) { axis = new Vector3(0f, MathF.Sign(local.Y), 0f); size = new Vector3(c.Size.X, c.Size.Z, c.Size.Y); }
            else { axis = new Vector3(0f, 0f, MathF.Sign(local.Z)); size = new Vector3(c.Size.X, c.Size.Y, c.Size.Z); }
            if (axis == Vector3.Zero) axis = Vector3.UnitY;
            normal = Vector3.Normalize(Vector3.Transform(axis, t.Rotation));
        }
        else if (c.Shape is ColliderShape.Cylinder or ColliderShape.Cone)
        {
            float halfHeight = c.Size.Y * 0.5f;
            Vector3 radial = new(rel.X, 0f, rel.Z);
            if (MathF.Abs(rel.Y) >= halfHeight - 1e-3f || radial.LengthSquared() < 1e-8f)
            { normal = new Vector3(0f, MathF.Sign(rel.Y) == 0 ? 1f : MathF.Sign(rel.Y), 0f); size = new Vector3(c.Size.X, c.Size.X, c.Size.Y); }
            else { normal = Vector3.Normalize(radial); size = new Vector3(MathF.PI * c.Size.X * 0.5f, c.Size.Y, c.Size.X); }
        }
        else
        {
            if (rel.LengthSquared() > 1e-8f) normal = Vector3.Normalize(rel);
            size = new Vector3(c.Size.X, c.Size.X, c.Size.X);
        }
        if (Vector3.Dot(normal, velocity) > 0f) normal = -normal;
    }

    private static bool IsGlass(World world, Entity e)
        => world.Has<MaterialComponent>(e)
           && string.Equals(world.Get<MaterialComponent>(e).Material, "Glass", StringComparison.OrdinalIgnoreCase);

    /// <summary>The scope on a weapon: its own, for now. An attachment would be looked up here.</summary>
    public static string ScopeOf(WeaponDefinition weapon) => weapon.ScopeId;

    /// <summary>The person or player nearest the crosshair inside the scope's view, or -1.</summary>
    private static int NearestToCrosshair(World world, int self, Vector3 eye, float yaw, float pitch, float fovDegrees)
    {
        int best = -1;
        float bestAngle = float.MaxValue;
        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (e.Id == self || !IsPerson(world, e) || world.Has<DeadComponent>(e)) return;
            var (r, u, ahead) = ScopeMath.Offset(eye, yaw, pitch, t.Position + new Vector3(0f, 1.2f, 0f));
            if (ahead > 2000f || !ScopeMath.InView(r, u, ahead, MathF.Max(1f, fovDegrees))) return;
            float a = r * r + u * u;
            if (a < bestAngle) { bestAngle = a; best = e.Id; }
        });
        return best;
    }

    /// <summary>Flies every bullet on this map on to now, and resolves what each meets.</summary>
    private void FlyBullets(string mapId)
    {
        if (_flights.Count == 0) return;
        if (!_maps.TryGetMap(mapId, out var world, out _, out var grid, out var lookup)) return;
        double now = Clock();
        var (air, wind) = AirOn(mapId);

        for (int i = _flights.Count - 1; i >= 0; i--)
        {
            var f = _flights[i];
            if (f.MapId != mapId) continue;
            bool done = false;
            while (!done && f.At < now - 1e-6)
            {
                float dt = (float)Math.Min(SegmentSeconds, now - f.At);
                var before = f.State;
                Fly(ref f.State, f.Slug, f.Weapon, dt, air, wind);
                double segStart = f.At;
                f.At += dt;
                done = Segment(f, world, grid, lookup, before, f.State, segStart, dt, now);
                if (!done && (f.State.Seconds > MaxFlightSeconds || f.State.Position.Y < PhysicsConstants.MapMinimumY
                              || (f.Slug.Tumbling && f.State.Velocity.LengthSquared() < MinSlugSpeed * MinSlugSpeed)))
                    done = true;
            }
            // A round that flew its whole time, or out of the bottom of the map, without meeting
            // anything says nothing: the silence after the shot is the answer, and every round that
            // ends in something has already said what.
            if (done) _flights.RemoveAt(i);
        }
    }

    /// <summary>
    /// One stretch of a bullet's flight: what it hits first along it, if anything. True when the
    /// bullet has stopped. Glass is broken and passed through; the bullet goes on.
    /// </summary>
    private bool Segment(Flight f, World world, SpatialGrid<Entity> grid, Dictionary<int, Entity> lookup,
                         BulletState before, BulletState after, double segStart, float dt, double now)
    {
        Vector3 a = before.Position, b = after.Position;
        Vector3 path = b - a;
        float length = path.Length();
        if (length < 1e-5f) return false;
        Vector3 dir = path / length;

        bool struck = FirstHit(f, world, grid, a, b, dt, segStart, now, f.BrokenGlass,
                               out var hitEntity, out float nearest, out bool hitBody, out float bodyHeight);
        // The spotter watches where it passes the target, over as much of this stretch as it flew
        // before it struck anything. A hit on the target itself is answered by the chime instead.
        if (!f.Called && f.TargetId >= 0 && !(hitBody && hitEntity.Id == f.TargetId)
            && lookup.TryGetValue(f.TargetId, out var target) && world.IsAlive(target))
        {
            float flown = !struck ? 1f : nearest / length;
            WatchPass(f, world, target, a, a + path * flown, segStart, dt * flown, now);
        }
        if (!struck) return false;

        Vector3 at = a + dir * nearest;
        float metres = Vector3.Distance(f.Eye, at);
        float seconds = before.Seconds + dt * nearest / length;
        if (hitBody)
        {
            bool head = bodyHeight >= ExternalBallistics.HeadFrom;
            int damage = f.Weapon.DamageAt(metres) * (head ? 2 : 1);
            if (f.Bounces > 0)
            {
                // A ricochet's slug: flattened, tumbling and slower, it does what its energy does
                // against the round's at the muzzle, and a deformed, yawing body wounds less than a
                // bullet arriving point first (taken as seven tenths).
                float speedNow = MathHelper.Lerp(before.Velocity.Length(), after.Velocity.Length(), nearest / length);
                float joules = 0.5f * f.Slug.MassKg * speedNow * speedNow;
                damage = Math.Max(1, (int)MathF.Round(damage * 0.7f * MathF.Min(1f, joules / MathF.Max(1f, f.MuzzleJoules))));
            }
            string whom = NameOf(world, hitEntity);
            var shooter = f.Shooter;
            // The chime and the words when the bullet gets there, not when the trigger breaks: a person
            // 400 m off is hit more than half a second after the shot.
            bool killed = Wound(shooter, world, grid, hitEntity, damage, f.Weapon,
                                m => _server.SendToSession(shooter, m), head);
            _server.SendToSession(shooter, new TextEvent { Text = HitWords(world, hitEntity, killed, head, metres) });
            f.Called = true;   // the chime is the call
            Log.Information("{User}'s {Weapon} round hit {Whom} at {Metres:F0} m after {Seconds:F2} s{Head}.",
                            f.Shooter.Username, f.Weapon.DisplayName, whom, metres, seconds, head ? ", in the head" : "");
            return true;
        }

        var tr = world.Get<Transform>(hitEntity);
        var col = world.Get<ColliderComponent>(hitEntity);
        string material = world.Has<MaterialComponent>(hitEntity)
            ? world.Get<MaterialComponent>(hitEntity).Material ?? "Generic" : "Generic";
        if (material.Equals("Glass", StringComparison.OrdinalIgnoreCase))
        {
            // A rifle bullet goes through a window and on, a little slower; the window does not.
            f.BrokenGlass.Add(hitEntity.Id);
            BreakGlass(f.Shooter, world, grid, lookup, hitEntity, tr, col, f.Weapon, f.State.Velocity.Length());
            f.State.Velocity *= 0.9f;
            return false;
        }
        Vector3 velocity = Vector3.Lerp(before.Velocity, after.Velocity, nearest / length);
        float speed = velocity.Length();
        float frac = nearest / length;
        if (TryRicochet(f.Seed, f.Bounces, f.Slug, world, hitEntity, at, velocity, out var bounce, out var normal, out var face))
        {
            // It skips: the strike is heard at the face with the energy it left there, and the slug
            // flies on from just off the face, tumbling, to be met by whatever is in its new way.
            EmitStrike(f, world, grid, hitEntity, material, at, normal, face, speed, bounce.GrazeRadians, bounce.Kept, f.Slug);
            f.Slug = bounce.Slug;
            f.Bounces++;
            f.State = new BulletState
            {
                Position = at + normal * 0.002f,
                Velocity = bounce.Velocity,
                Seconds = before.Seconds + dt * frac,
                Travelled = before.Travelled + nearest,
            };
            f.At = segStart + dt * frac;
            f.Called = true;
            _server.SendToSession(f.Shooter, new TextEvent { Text = $"Ricochet off {ThingName(world, hitEntity)}." });
            Log.Information("{User}'s {Weapon} round ricocheted off {What} ({Material}) at {Graze:F1} degrees, {Speed:F0} to {Out:F0} m/s.",
                            f.Shooter.Username, f.Weapon.DisplayName, ThingName(world, hitEntity), material,
                            bounce.GrazeRadians * 180f / MathF.PI, speed, bounce.Velocity.Length());
            return false;
        }
        float graze = MathF.Asin(Math.Clamp(-Vector3.Dot(Vector3.Normalize(velocity), normal), 0f, 1f));
        EmitStrike(f, world, grid, hitEntity, material, at, normal, face, speed, graze, 0f, f.Slug);
        // What it ended in, for the shooter alone: the ground, a roof, a wall, a car, by the name the
        // map gives it ("Hit Kestrel House north wall at 22 metres"). This used to be the material,
        // which is how a sofa came to be "audience": that is its acoustic material, soft and absorbent,
        // the one a grandstand full of people is built of (Cody, 2026-10-04).
        f.Called = true;
        _server.SendToSession(f.Shooter, new TextEvent { Text = StruckWords(world, hitEntity, metres) });
        Log.Information("{User}'s {Weapon} round hit {What} ({Id}) at {Metres:F0} m after {Seconds:F2} s.",
                        f.Shooter.Username, f.Weapon.DisplayName, ThingName(world, hitEntity), hitEntity.Id, metres, seconds);
        return true;
    }

    /// <summary>
    /// The sound of a round striking a part (<see cref="BulletImpact"/>): the strike at the face, from
    /// the part's material and the size of the face it met, and what it throws off landing on the floor
    /// below. <paramref name="kept"/> is the share of its speed it went on with: nought for one that
    /// stopped there.
    /// </summary>
    private void EmitStrike(Flight f, World world, SpatialGrid<Entity> grid, Entity hit, string material, Vector3 at,
                            Vector3 normal, Vector3 face, float speed, float graze, float kept, Slug slug)
    {
        // Where what it throws off comes down: the floor under a point just off the face.
        Vector3 off = at + normal * 0.3f;
        float ground = PhysicsUtils.GetGroundHeight(world, grid, off + new Vector3(0f, 0.05f, 0f), out string floor);
        if (!float.IsFinite(ground) || ground > at.Y + 0.05f || ground < at.Y - 60f) { ground = at.Y; floor = material; }
        var strike = BulletImpact.From(material, speed, f.Weapon, graze, kept, face, string.IsNullOrEmpty(floor) ? material : floor,
                                       at.Y - ground, slug.MassKg, slug.Across, slug.Length);
        var foot = new Vector3(off.X, ground, off.Z);
        _server.EmitWorldAudio(f.MapId, hit.Id, kept > 0f ? "a ricochet" : "a bullet striking", BulletImpact.Sounds(strike, at, foot));
    }

    /// <summary>
    /// What a stretch of a round's flight meets first, if anything: a person, where the path enters the
    /// upright body (in the body's own frame, so one who is walking has to be led), or anything solid,
    /// where the path enters its box, cylinder or sphere. Nearest along the path wins, whatever the
    /// distance between centres. Not the shooter, not what they are riding in, not a carried thing, not
    /// a body already down, and not a pane this round has already gone through.
    /// </summary>
    private bool FirstHit(Flight f, World world, SpatialGrid<Entity> grid, Vector3 a, Vector3 b, float dt,
                          double segStart, double now, HashSet<int> passed,
                          out Entity hit, out float nearest, out bool hitBody, out float bodyHeight)
        => FirstHit(f.ShooterId, f.RideId, world, grid, a, b, dt, segStart, now, passed, out hit, out nearest, out hitBody, out bodyHeight);

    private bool FirstHit(int shooterId, int rideId, World world, SpatialGrid<Entity> grid, Vector3 a, Vector3 b, float dt,
                          double segStart, double now, HashSet<int> passed,
                          out Entity hit, out float nearest, out bool hitBody, out float bodyHeight)
    {
        hit = Entity.Null; nearest = float.MaxValue; hitBody = false; bodyHeight = 0f;
        Vector3 path = b - a;
        float length = path.Length();
        if (length < 1e-5f) return false;
        Vector3 dir = path / length;
        grid.CollectInRadius(a + path * 0.5f, length * 0.5f + 3f, _near, _nearSeen);
        foreach (var e in _near)
        {
            if (e.Id == shooterId || !world.IsAlive(e) || !world.Has<Transform>(e)) continue;
            if (rideId >= 0 && PartOf(world, e, rideId)) continue;
            var t = world.Get<Transform>(e);
            if (IsPerson(world, e))
            {
                if (world.Has<DeadComponent>(e)) continue;
                Vector3 vel = world.Has<Velocity>(e) ? world.Get<Velocity>(e).Linear : Vector3.Zero;
                // Where they stood when this stretch began: they have moved on to here by now (or, flown
                // ahead, will have got there by then).
                Vector3 feet = t.Position - vel * (float)(now - segStart);
                if (ExternalBallistics.SegmentHitsBody(a, b, dt, feet, vel, ExternalBallistics.BodyRadius,
                                                       ExternalBallistics.BodyHeight, out float frac, out float h)
                    && frac * length < nearest)
                {
                    nearest = frac * length; hit = e; hitBody = true; bodyHeight = h;
                }
                continue;
            }
            if (!world.Has<ColliderComponent>(e) || world.Has<ItemComponent>(e)) continue;
            var c = world.Get<ColliderComponent>(e);
            if (!c.IsSolid || passed.Contains(e.Id)) continue;
            bool crossed; float d;
            if (c.Shape == ColliderShape.Box)
                crossed = GeometryUtils.RayIntersectsOBB(a, dir, t.Position, c.Size, t.Rotation, out d);
            else if (c.Shape is ColliderShape.Cylinder or ColliderShape.Cone)
                crossed = GeometryUtils.RayIntersectsCylinder(a, dir, t.Position, c.Size.X * 0.5f, c.Size.Y, out d);
            else
                crossed = GeometryUtils.RayIntersectsSphere(a, dir, t.Position, c.Size.X * 0.5f, out d, out _);
            if (crossed && d >= 0f && d <= length && d < nearest) { nearest = d; hit = e; hitBody = false; bodyHeight = 0f; }
        }
        return hit != Entity.Null;
    }

    /// <summary>Whether an entity is a composite or one of its parts.</summary>
    private static bool PartOf(World world, Entity e, int rootId)
        => e.Id == rootId || (world.Has<ParentComponent>(e) && world.Get<ParentComponent>(e).ParentEntityId == rootId);

    /// <summary>
    /// Watches the bullet pass the target's distance, and calls where it went against the target's
    /// chest: the spotter's call, which is what a shooter who cannot see the strike needs to correct.
    /// </summary>
    private void WatchPass(Flight f, World world, Entity target, Vector3 a, Vector3 b, double segStart, float dt, double now)
    {
        var t = world.Get<Transform>(target);
        Vector3 vel = world.Has<Velocity>(target) ? world.Get<Velocity>(target).Linear : Vector3.Zero;
        Vector3 chestStart = t.Position - vel * (float)(now - segStart) + new Vector3(0f, 1.2f, 0f);
        Vector3 chestEnd = chestStart + vel * dt;
        float alongA = Vector3.Dot(a - f.Eye, f.Sight) - Vector3.Dot(chestStart - f.Eye, f.Sight);
        float alongB = Vector3.Dot(b - f.Eye, f.Sight) - Vector3.Dot(chestEnd - f.Eye, f.Sight);
        if (alongA > 0f || alongB < 0f) return;
        float s = alongA / MathF.Min(-1e-6f, alongA - alongB);
        Vector3 bullet = Vector3.Lerp(a, b, s);
        Vector3 chest = Vector3.Lerp(chestStart, chestEnd, s);
        f.Called = true;
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, f.Sight) + new Vector3(1e-6f, 0f, 0f));
        Vector3 up = Vector3.Cross(f.Sight, right);
        Vector3 off = bullet - chest;
        _server.SendToSession(f.Shooter, new TextEvent { Text = SpotterCall(Vector3.Dot(off, right), Vector3.Dot(off, up)) });
    }


    /// <summary>"Miss. 40 centimetres low, 20 left." Each part only when it is more than a few
    /// centimetres, and in metres past one.</summary>
    public static string SpotterCall(float right, float up)
    {
        static string Size(float m)
        {
            float a = MathF.Abs(m);
            return a >= 1f ? $"{a:0.#} metres" : $"{(int)MathF.Round(a * 100f / 5f) * 5} centimetres";
        }
        var parts = new List<string>();
        if (MathF.Abs(up) >= 0.05f) parts.Add($"{Size(up)} {(up > 0f ? "high" : "low")}");
        if (MathF.Abs(right) >= 0.05f) parts.Add($"{Size(right)} {(right > 0f ? "right" : "left")}");
        return parts.Count == 0 ? "Miss, just past the edge." : "Miss. " + string.Join(", ", parts) + ".";
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
    private void BreakGlass(UserSession session, World world, SpatialGrid<Entity> grid, Dictionary<int, Entity> lookup,
                            Entity pane, Transform t, ColliderComponent collider, WeaponDefinition weapon, float speed)
    {
        // The pane is the box's two larger sides, and it faces along the thinnest: a car's side glass is a box
        // thin across the car, a windscreen thin along it.
        Vector3 box = collider.Size;
        Vector3 axis = box.X <= box.Y && box.X <= box.Z ? Vector3.UnitX : box.Y <= box.Z ? Vector3.UnitY : Vector3.UnitZ;
        Vector2 face = axis == Vector3.UnitX ? new Vector2(box.Z, box.Y) : axis == Vector3.UnitY ? new Vector2(box.X, box.Z) : new Vector2(box.X, box.Y);
        var glass = new GlassPane(
            Centre: t.Position,
            Size: new Vector2(MathF.Max(0.3f, face.X), MathF.Max(0.3f, face.Y)),
            Normal: Vector3.Transform(axis, t.Rotation),
            // What kind of glass it is, from what the map says the part is (GlassKind): a house window
            // annealed, a shop front, a door, a shelter or a car's side window tempered, a windscreen laminated.
            Type: GlassKind.Of(GlazedPartOf(world, lookup, pane, box)),
            HeightAboveGround: MathF.Max(0f, t.Position.Y - collider.Size.Y * 0.5f));

        Span<GlassEvent> buffer = stackalloc GlassEvent[48];
        int count = GlassBreak.Resolve(glass, t.Position, weapon, pane.Id, buffer);
        if (count == 0) return;

        var events = new List<GlassEvent>(count);
        for (int i = 0; i < count; i++) events.Add(buffer[i]);

        // The pane's own thickness where the prefab says how it is built (a glazing box is two leaves with
        // the depth of the box between them), else the box's depth; and what the glass lands on.
        float thickness = world.Has<AcousticComponent>(pane) && world.Get<AcousticComponent>(pane).LeafMetres > 0f
            ? world.Get<AcousticComponent>(pane).LeafMetres : MathF.Max(0.003f, collider.Size.Z);
        Vector3 foot = t.Position - new Vector3(0f, glass.HeightAboveGround + collider.Size.Y * 0.5f, 0f) + glass.Normal * 0.8f;
        PhysicsUtils.GetGroundHeight(world, grid, foot + new Vector3(0f, 0.5f, 0f), new[] { pane }, out string ground);
        _server.EmitWorldAudio(session.CurrentMapId, pane.Id, "glass",
                               GlassSound.From(events, glass, weapon, thickness,
                                               string.IsNullOrEmpty(ground) ? "Concrete" : ground, pane.Id, speed));
        // A pane that only took a hole stays where it is: a laminated windscreen cracks round the hole and
        // holds, and a fast rifle round drills annealed glass. Only a pane that fails comes out of its frame.
        if (!GlassBreak.Shatters(glass.Type, weapon)) return;
        _maps.DestroyEntity(session.CurrentMapId, pane);
        _server.BroadcastRemoval(session.CurrentMapId, pane.Id);
    }

    /// <summary>A glass entity as the glass kind needs it: its prefab and name, and, when it is a part of a
    /// composite, the composite's template and where the part sits in it.</summary>
    private static GlazedPart GlazedPartOf(World world, Dictionary<int, Entity> lookup, Entity pane, Vector3 size)
    {
        string prefab = world.Has<IdentityComponent>(pane) ? world.Get<IdentityComponent>(pane).PrefabId ?? "" : "";
        string owner = "";
        Vector3 local = Vector3.Zero;
        if (world.Has<ParentComponent>(pane))
        {
            var parent = world.Get<ParentComponent>(pane);
            local = parent.LocalPosition;
            if (lookup.TryGetValue(parent.ParentEntityId, out var whole) && world.Has<CompositeComponent>(whole))
                owner = world.Get<CompositeComponent>(whole).TemplateId ?? "";
        }
        return new GlazedPart(prefab, NameOf(world, pane), owner, local, size);
    }

    // ── Wounds and death ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes health from a person, tells the shooter (and nobody else) by a <see cref="HitConfirm"/>,
    /// and kills them at none. A player who is hit is told so, with what they have left, because
    /// otherwise they cannot know: there is no red edge to the screen.
    /// </summary>
    public bool Wound(UserSession shooter, World world, SpatialGrid<Entity> grid, Entity target, int damage,
                      WeaponDefinition weapon, Action<IMessage> replyToShooter, bool headshot = false)
    {
        if (!world.Has<HealthComponent>(target)) world.Add(target, new HealthComponent { Current = 100, Max = 100 });
        ref var health = ref world.Get<HealthComponent>(target);
        if (health.Max <= 0) health.Max = 100;
        int before = health.Current;
        health.Current = Math.Max(0, health.Current - Math.Max(1, damage));
        bool killed = health.Current == 0;
        int left = health.Current, max = health.Max;

        replyToShooter(new HitConfirm { TargetEntityId = target.Id, Killed = killed, Headshot = headshot });
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
        FlyBullets(mapId);
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

    /// <summary>"Hit pedestrian at 17 metres.", "Killed sean in the head at 340 metres.": what the shot
    /// struck, by name for a player and by what they are for anyone else, and how far.</summary>
    internal static string HitWords(World world, Entity e, bool killed, bool head, float metres)
    {
        string whom = world.Has<PlayerComponent>(e) ? world.Get<PlayerComponent>(e).Username
                    : world.Has<Pedestrian>(e) ? "pedestrian"
                    : NameOf(world, e);
        return $"{(killed ? "Killed" : "Hit")} {whom}{(head ? " in the head" : "")} at {MathF.Round(metres):F0} metres.";
    }

    /// <summary>"Hit the ground at 120 metres.", "Hit Brandt Court roof at 40 metres.": what a round
    /// ended in that is not a person, by the name the map gives it.</summary>
    internal static string StruckWords(World world, Entity e, float metres)
        => $"Hit {ThingName(world, e)} at {MathF.Round(metres):F0} metres.";

    /// <summary>
    /// A thing by its name: the map's name for the part ("Kestrel House north wall"), or its prefab's
    /// ("Brick Wall"), and "the ground" for the ground. Never its material, which is a property of the
    /// thing and not what it is: a sofa's material is "Audience", and a bullet in one was "Hit audience".
    /// </summary>
    internal static string ThingName(World world, Entity e)
    {
        string? name = world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
            ? world.Get<IdentityComponent>(e).Name
            : world.Has<NameComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<NameComponent>(e).Name)
                ? world.Get<NameComponent>(e).Name : null;
        if (name == null) return "something";
        return name.Trim().Equals("Ground", StringComparison.OrdinalIgnoreCase) ? "the ground" : name.Trim();
    }

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
        _flights.RemoveAll(f => f.Shooter == session);
    }

    /// <summary>The weapon in a player's hands, its rounds and its scope, for the client's keys:
    /// ("", -1, "") with none.</summary>
    public static (string WeaponId, int Rounds, string ScopeId) Held(World world, Entity player, Dictionary<int, Entity> lookup)
        => HandsService.TryGetHeldWeapon(world, player, lookup, out var weapon, out var item)
            ? (weapon.Id, Arms.Ammo(world, item, weapon).Rounds, ScopeOf(weapon))
            : ("", -1, "");
}
