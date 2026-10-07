using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// The people driving, out loud. Every car on the street has a driver with a voice, and a driver yells
/// when something goes wrong: a hard stop, a car coming across while they wait to give way, somebody
/// standing in the road, a car pulling out in front of them, a train that will not clear the crossing.
/// Each is said from the driver's window and moves with the car.
/// </summary>
public sealed partial class VehicleSystem
{
    /// <summary>A driver who has just yelled does not yell again for this long.</summary>
    private const double YellAgainSeconds = 12;
    /// <summary>How likely a driver is to yell after standing on the brakes.</summary>
    private const double YellAfterHardBrake = 0.7;
    /// <summary>After an ordinary honk.</summary>
    private const double YellAfterHonk = 0.3;
    /// <summary>Waiting to give way while a car goes across in front, within this many metres.</summary>
    private const float CrossTrafficMetres = 14f;
    private const double YellAtCrossTraffic = 0.25;
    /// <summary>Held at a level crossing this long before they start on it.</summary>
    private const double CrossingPatienceSeconds = 25;
    /// <summary>Somebody in the road: this far to either side of the car's line counts as in its way.</summary>
    private const float InTheWayHalfWidth = 1.6f;
    private const double YellAtParker = 0.4;

    /// <summary>Where a driver's mouth is: the left seat, at head height, at the open window.</summary>
    private static Vector3 DriverWindow(in Transform t)
    {
        var forward = Vector3.Transform(Vector3.UnitZ, t.Rotation);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        return t.Position + new Vector3(0f, 1.2f, 0f) - right * 0.9f + forward * 0.3f;
    }

    /// <summary>The driver says one of these, after a delay, if they have not just said something.</summary>
    private void Yell(string mapId, World world, DemoVehicle v, IReadOnlyList<string> offered, float delay, double clock)
    {
        if (Heard == null || v.DriverVoice.Length == 0 || clock - v.LastYellAt < YellAgainSeconds) return;
        var lines = offered.Where(l => Speech.Find(v.DriverVoice, l) != null && l != v.DriverLastLine).ToList();
        if (lines.Count == 0) return;
        var take = Speech.Find(v.DriverVoice, lines[_streetRng.Next(lines.Count)])!;
        v.LastYellAt = clock;
        v.DriverLastLine = take.Line;
        var t = world.Get<Transform>(v.Entity);
        Log.Information("Street: the driver of {Name} ({Voice}) yells \"{Text}\"", v.DisplayName, v.DriverVoice, take.Text);
        Heard(mapId, v.Entity.Id, "speech: " + take.Text, new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Hiss,
                DelaySeconds = delay,
                Position = DriverWindow(t),
                // At the driver's window on the car as it is when heard, and riding with it: the car is
                // metres on by then (see WorldAudioPlayer's following). x right, y up, z forward.
                OnBody = true,
                BodyOffset = new Vector3(-0.9f, 1.2f, 0.3f),
                LevelDb = Speech.LevelDb(Speech.ShoutDb),
                DecaySeconds = take.Seconds,
                Noisiness = 0.5f,
                SynthKey = Speech.Key(v.DriverVoice, take.Line),
            },
        });
    }

    /// <summary>Each tick: the things drivers react to that are not events of their own.</summary>
    private void UpdateDrivers(string mapId, World world, double clock)
    {
        var players = new List<Vector3>();
        world.Query(new QueryDescription().WithAll<Transform, PlayerComponent>(),
            (ref Transform t, ref PlayerComponent pc) => { if (!pc.IsInVehicle) players.Add(t.Position); });

        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || !v.OnStreet || v.DriverVoice.Length == 0 || !world.IsAlive(v.Entity)) continue;
            var t = world.Get<Transform>(v.Entity);
            var forward = Vector3.Transform(Vector3.UnitZ, t.Rotation);
            forward.Y = 0f;
            if (forward.LengthSquared() < 1e-6f) continue;
            forward = Vector3.Normalize(forward);
            var right = new Vector3(forward.Z, 0f, -forward.X);

            // Somebody standing in the road ahead: brake, lean on the horn, and tell them.
            if (v.Speed > 3f && v.Park == null && clock - v.LastInTheWayAt > YellAgainSeconds)
            {
                float reach = 4f + v.Speed * 1.5f;
                foreach (var p in players)
                {
                    var d = p - t.Position;
                    if (MathF.Abs(d.Y) > 2.5f) continue;
                    float ahead = Vector3.Dot(d, forward), side = Vector3.Dot(d, right);
                    if (ahead < 2f || ahead > reach || MathF.Abs(side) > InTheWayHalfWidth) continue;
                    v.LastInTheWayAt = clock;
                    if (v.HardBrakeLeft <= 0f) BrakeHard(v, MathF.Min(v.Speed, 1.5f));
                    Honk(mapId, world, v, OpenFPS.Common.Honk.Startled(_streetRng));
                    Yell(mapId, world, v, StreetLines.AtSomebodyInTheRoad, 0.5f, clock);
                    break;
                }
            }

            // Waiting to give way, and a car comes across in front.
            bool givingWay = v.DwellLeft > 0f && v.Stops.Length > 0
                             && string.Equals(v.Stops[v.NextStop].Kind, "give_way", StringComparison.OrdinalIgnoreCase);
            if (givingWay && clock - v.LastYellAt > YellAgainSeconds)
                foreach (var o in _vehicles)
                {
                    if (o == v || o.MapId != mapId || !o.OnStreet || o.Speed < 5f || !world.IsAlive(o.Entity)) continue;
                    if (Vector3.Distance(world.Get<Transform>(o.Entity).Position, t.Position) > CrossTrafficMetres) continue;
                    if (_streetRng.NextDouble() < YellAtCrossTraffic)
                        Yell(mapId, world, v, StreetLines.Startled, 0.2f, clock);
                    else
                        v.LastYellAt = clock;    // let it go this time, and do not ask again every tick
                    break;
                }

            // Held at a level crossing.
            bool atCrossing = v.DwellLeft > 0f && v.Stops.Length > 0
                              && string.Equals(v.Stops[v.NextStop].Kind, "crossing", StringComparison.OrdinalIgnoreCase);
            if (!atCrossing) v.HeldSince = double.NaN;
            else
            {
                if (double.IsNaN(v.HeldSince)) v.HeldSince = clock;
                if (clock - v.HeldSince > CrossingPatienceSeconds && !v.YelledThisHold)
                {
                    v.YelledThisHold = true;
                    Yell(mapId, world, v, StreetLines.Waiting, 0f, clock);
                }
            }
            if (!atCrossing) v.YelledThisHold = false;
        }
    }

    /// <summary>A car pulled out from the kerb: the nearest driver coming up behind may have words.</summary>
    private void PulledOut(string mapId, World world, DemoVehicle parker, double clock)
    {
        if (!world.IsAlive(parker.Entity)) return;
        var at = world.Get<Transform>(parker.Entity).Position;
        DemoVehicle? behind = null;
        float nearest = 30f;
        foreach (var o in _vehicles)
        {
            if (o == parker || o.MapId != mapId || !o.OnStreet || o.Speed < 3f || !world.IsAlive(o.Entity)) continue;
            var ot = world.Get<Transform>(o.Entity);
            var toParker = at - ot.Position;
            float d = toParker.Length();
            if (d >= nearest || Vector3.Dot(toParker, Vector3.Transform(Vector3.UnitZ, ot.Rotation)) <= 0f) continue;
            nearest = d; behind = o;
        }
        if (behind != null && _streetRng.NextDouble() < YellAtParker)
            Yell(mapId, world, behind, StreetLines.AtAParker, 0.3f, clock);
    }
}
