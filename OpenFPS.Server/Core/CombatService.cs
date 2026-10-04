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

        if (!fromTheAir && !SpendRound(session, world, weapon, item, position, forward, reply)) return;

        // 1. The shot itself. Named rather than described, because a gunshot is a blast wave, a body
        //    resonance, a brightness sweep and the action working — and a model for that already
        //    exists and is better than four numbers.
        EmitReport(session, weapon, position, forward, muzzle, cycle: !fromTheAir);

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
                bool killed = Wound(session, world, grid, candidate, weapon.DamageAt(distance), weapon, reply);
                // The chime says THAT you hit; the words say what and how far (Cody, 2026-10-04: "I'll hit
                // things but I won't know what I hit"). Short, because it is said in the middle of a fight.
                Say(reply, HitWords(world, candidate, killed, head: false, distance));
                return;
            }

            string material = world.Has<MaterialComponent>(candidate)
                ? world.Get<MaterialComponent>(candidate).Material ?? "Generic" : "Generic";
            hit = $"{material.ToLowerInvariant()} at {distance:F0} metres";

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
        Say(reply, hit.StartsWith("nothing") ? $"You fire the {weapon.DisplayName}. It hits {hit}." : $"Hit {hit}.");
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
        public required Vector3 Eye;
        public required Vector3 Sight;          // the line of sight, unit
        public required double At;              // server time the state is true at
        public int TargetId = -1;               // whom the crosshair was nearest, for the spotter's call
        public bool Called;
        public readonly HashSet<int> BrokenGlass = new();
    }

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

        var flight = new Flight
        {
            MapId = session.CurrentMapId,
            Shooter = session,
            ShooterId = session.Entity.Id,
            Weapon = weapon,
            State = new BulletState { Position = bore, Velocity = barrel * weapon.MuzzleVelocity },
            Eye = eye,
            Sight = sight,
            At = Clock(),
            TargetId = NearestToCrosshair(world, session.Entity.Id, eye, yaw, pitch, scope.FieldOfViewDegrees(shot.Magnification)),
        };
        _flights.Add(flight);
        Log.Information("{User} fires the {Weapon} through the scope at {Mag}x, {Mil:F1} mil up; aim {Yaw:F4},{Pitch:F4}.",
                        session.Username, weapon.DisplayName, shot.Magnification, elevation, yaw, pitch);
    }

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
        var weather = Weather?.Invoke(mapId) ?? (15f, 1013.25f, Vector3.Zero, 0f);
        var air = new Air(weather.TemperatureC, weather.PressureMb);
        // Out in the open, where a long shot is taken: the full wind, gusts and all.
        Vector3 wind = WindModel.Felt(weather.Wind, weather.Gustiness, now);

        for (int i = _flights.Count - 1; i >= 0; i--)
        {
            var f = _flights[i];
            if (f.MapId != mapId) continue;
            bool done = false;
            while (!done && f.At < now - 1e-6)
            {
                float dt = (float)Math.Min(SegmentSeconds, now - f.At);
                var before = f.State;
                ExternalBallistics.Advance(ref f.State, dt, ExternalBallistics.CoefficientOf(f.Weapon), air, wind);
                double segStart = f.At;
                f.At += dt;
                done = Segment(f, world, grid, lookup, before, f.State, segStart, dt, now);
                if (!done && (f.State.Seconds > MaxFlightSeconds || f.State.Position.Y < PhysicsConstants.MapMinimumY))
                    done = true;
            }
            if (done)
            {
                if (!f.Called) Spot(f, world, null);
                _flights.RemoveAt(i);
            }
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

        grid.CollectInRadius(a + path * 0.5f, length * 0.5f + 3f, _near, _nearSeen);
        float nearest = float.MaxValue;
        Entity hitEntity = Entity.Null;
        bool hitBody = false;
        float bodyHeight = 0f;
        foreach (var e in _near)
        {
            if (e.Id == f.ShooterId || !world.IsAlive(e) || !world.Has<Transform>(e)) continue;
            var t = world.Get<Transform>(e);
            if (IsPerson(world, e))
            {
                if (world.Has<DeadComponent>(e)) continue;
                Vector3 vel = world.Has<Velocity>(e) ? world.Get<Velocity>(e).Linear : Vector3.Zero;
                // Where they stood when this stretch began: they have moved on to here by now.
                Vector3 feet = t.Position - vel * (float)(now - segStart);
                if (ExternalBallistics.SegmentHitsBody(a, b, dt, feet, vel, ExternalBallistics.BodyRadius,
                                                       ExternalBallistics.BodyHeight, out float frac, out float h)
                    && frac * length < nearest)
                {
                    nearest = frac * length; hitEntity = e; hitBody = true; bodyHeight = h;
                }
                continue;
            }
            if (!world.Has<ColliderComponent>(e)) continue;
            var c = world.Get<ColliderComponent>(e);
            if (!c.IsSolid || f.BrokenGlass.Contains(e.Id)) continue;
            bool crossed; float d;
            if (c.Shape == ColliderShape.Box)
                crossed = GeometryUtils.RayIntersectsOBB(a, dir, t.Position, c.Size, t.Rotation, out d);
            else if (c.Shape is ColliderShape.Cylinder or ColliderShape.Cone)
                crossed = GeometryUtils.RayIntersectsCylinder(a, dir, t.Position, c.Size.X * 0.5f, c.Size.Y, out d);
            else
            {
                crossed = GeometryUtils.RayIntersectsSphere(a, dir, t.Position, c.Size.X * 0.5f, out d, out _);
            }
            if (crossed && d >= 0f && d <= length && d < nearest) { nearest = d; hitEntity = e; hitBody = false; }
        }
        // The spotter watches where it passes the target, over as much of this stretch as it flew
        // before it struck anything. A hit on the target itself is answered by the chime instead.
        if (!f.Called && f.TargetId >= 0 && !(hitBody && hitEntity.Id == f.TargetId)
            && lookup.TryGetValue(f.TargetId, out var target) && world.IsAlive(target))
        {
            float flown = hitEntity == Entity.Null ? 1f : nearest / length;
            WatchPass(f, world, target, a, a + path * flown, segStart, dt * flown, now);
        }
        if (hitEntity == Entity.Null) return false;

        Vector3 at = a + dir * nearest;
        float travelled = before.Travelled + nearest;
        if (hitBody)
        {
            bool head = bodyHeight >= ExternalBallistics.HeadFrom;
            int damage = f.Weapon.DamageAt(Vector3.Distance(f.Eye, at)) * (head ? 2 : 1);
            string whom = NameOf(world, hitEntity);
            var shooter = f.Shooter;
            bool killed = Wound(shooter, world, grid, hitEntity, damage, f.Weapon,
                                m => _server.SendToSession(shooter, m), head);
            _server.SendToSession(shooter, new TextEvent { Text = HitWords(world, hitEntity, killed, head, travelled) });
            f.Called = true;   // the chime is the call
            Log.Information("Scoped shot hit {Whom} at {Metres:F0} m after {Seconds:F2} s{Head}.", whom, travelled, after.Seconds, head ? ", in the head" : "");
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
            BreakGlass(f.Shooter, world, hitEntity, tr, col, f.Weapon);
            f.State.Velocity *= 0.9f;
            return false;
        }
        float speed = MathHelper.Lerp(before.Velocity.Length(), after.Velocity.Length(), nearest / length);
        _server.EmitWorldAudio(f.MapId, hitEntity.Id, "impact",
            ImpactAcoustics.Between(AcousticRegistry.GetProperties("Metal"),
                                    AcousticRegistry.GetProperties(material),
                                    at, speed * 0.02f,
                                    0.01f, 500f, col.Size.X, col.Size.Y, MathF.Max(0.02f, col.Size.Z)));
        if (!f.Called) Spot(f, world, at);
        return true;
    }

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

    /// <summary>
    /// The call for a bullet that stopped before it reached the target's distance (a wall in the way)
    /// or never had a target: nothing for the second, and "short" for the first.
    /// </summary>
    private void Spot(Flight f, World world, Vector3? stoppedAt)
    {
        f.Called = true;
        if (f.TargetId < 0 || stoppedAt == null) return;
        _server.SendToSession(f.Shooter, new TextEvent { Text = "Miss. It hit something short of the target." });
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
