using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Who goes first at a junction (JunctionRules): who is in each junction and coming to it this tick, the
/// ways that meet, by map, and who is waiting and has been let go.
/// </summary>
public sealed partial class VehicleSystem
{
    private sealed class AtJunction
    {
        public readonly List<(DemoVehicle V, Movement M)> Inside = new();
        public readonly List<(DemoVehicle V, Movement M, float ToLine)> Coming = new();
        /// <summary>The one driver the deadlock breaker has let go, until it is into the junction.</summary>
        public DemoVehicle? LetGo;
    }

    private readonly Dictionary<(string Map, string Junction), AtJunction> _atJunction = new();
    /// <summary>Whether two ways through a junction meet, by map and lane segment indices: every map's
    /// roads number their segments from zero.</summary>
    private readonly Dictionary<(string Map, int, int, int, int), bool> _conflicts = new();

    /// <summary>Where each vehicle on a route is with respect to the junctions on its way, this tick.</summary>
    private void IndexJunctions(string mapId)
    {
        foreach (var a in _atJunction.Values) { a.Inside.Clear(); a.Coming.Clear(); }
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || v.Route == null || v.Line == null || !InLane(v)) continue;
            var legs = v.Route.Legs;
            var (leg, along) = WhereOnLane(v);
            if (along < 0f && !ShortOfTheLine(v, ref leg, ref along))
            {
                // In the junction between the leg before and this one.
                var before = legs[(leg - 1 + legs.Count) % legs.Count].Segment;
                if (before.To != null) Slot(mapId, before.To).Inside.Add((v, new Movement(before, legs[leg].Segment)));
                continue;
            }
            // Every junction within the look, not only the next: listing only the next hid a motorbike
            // at 13 m/s from a car giving way until it was 36 m off, too late.
            float toLine = legs[leg].Segment.LengthMetres - along;
            for (int k = 0; k < legs.Count && toLine <= JunctionRules.LookMetres; k++)
            {
                int li = (leg + k) % legs.Count;
                var seg = legs[li].Segment;
                if (seg.To != null)
                    Slot(mapId, seg.To).Coming.Add((v, new Movement(seg, legs[(li + 1) % legs.Count].Segment), toLine));
                int next = (li + 1) % legs.Count;
                toLine += (legs[next].From - legs[li].To + v.Route.Length) % v.Route.Length + legs[next].Segment.LengthMetres;
            }
        }
    }

    private AtJunction Slot(string mapId, JunctionData j)
    {
        if (!_atJunction.TryGetValue((mapId, j.Id), out var a)) _atJunction[(mapId, j.Id)] = a = new AtJunction();
        return a;
    }

    /// <summary>
    /// How far a vehicle may still go before it must be standing at a junction's line, metres, or
    /// MaxValue if nothing makes it wait; and the speed it arrives at the line to look, m/s (a give-way
    /// approach), or MaxValue.
    /// </summary>
    private (float ToHold, float LookSpeed) JunctionHold(DemoVehicle v, float dt)
    {
        if (v.Route == null || !_streetLife.TryGetValue(v.MapId, out var life)) return (float.MaxValue, float.MaxValue);
        var legs = v.Route.Legs;
        var (leg, along) = WhereOnLane(v);
        if (along < 0f && !ShortOfTheLine(v, ref leg, ref along))
        { v.WaitingAt = null; v.Holding = false; return (float.MaxValue, float.MaxValue); }   // already in it: keep going
        var seg = legs[leg].Segment;
        var j = seg.To;
        float toLine = seg.LengthMetres - along;
        if (j == null || toLine > JunctionRules.LookMetres) return (float.MaxValue, float.MaxValue);
        var mine = new Movement(seg, legs[(leg + 1) % legs.Count].Segment);
        bool givesWay = j.GivesWay(seg.Road);
        float look = givesWay ? life.GiveWayApproachKmh / 3.6f : float.MaxValue;

        // Only decide once it is near enough to be committing; further out it simply drives.
        // It stands with its front bumper at the line (half a car back from its middle), or short of a
        // crossing that lies before the line.
        float atLine = MathF.Max(0f, toLine - 1f - 0.5f * v.LengthMetres);
        float holdAt = MathF.Min(atLine, ShortOfCrosswalk(v, toLine));
        float stopping = v.Speed * v.Speed / (2f * MathF.Max(0.1f, v.Brake)) + 6f;
        if (toLine - (atLine - holdAt) > stopping) return (float.MaxValue, look);

        var here = Slot(v.MapId, j);
        bool wait = false;
        foreach (var (o, m) in here.Inside)
            if (o != v && Conflict(v.MapId, mine, m)) { wait = true; break; }
        // ...and so does one already committed to it, or two drivers who each decided before the other
        // entered both go, and meet.
        if (!wait)
            foreach (var (o, m, oToLine) in here.Coming)
                if (o != v && m.In != mine.In && !o.Holding && JunctionRules.Committed(o.Speed, o.Brake, oToLine) && Conflict(v.MapId, mine, m)
                    && (!JunctionRules.Committed(v.Speed, v.Brake, toLine) || oToLine < toLine || (oToLine == toLine && o.Entity.Id < v.Entity.Id)))
                { wait = true; break; }
        if (!wait)
            foreach (var (o, m, oToLine) in here.Coming)
            {
                if (o == v || m.In == mine.In || !Conflict(v.MapId, mine, m)) continue;
                if (!JunctionRules.GivesWayTo(j, mine, toLine, v.Entity.Id, m, oToLine, o.Entity.Id)) continue;
                if (JunctionRules.GapTooShort(life, mine, givesWay, toLine, v.Speed, look, oToLine, o.Speed)) { wait = true; break; }
            }

        // Everybody waiting for somebody: after a while one goes, and only one until it is into the
        // junction; two let go together met in the middle (docs/MUTATION_2026-10-01.md, item 10).
        if (here.LetGo is { } went && !here.Coming.Any(x => x.V == went && x.ToLine < 6f + 0.5f * went.LengthMetres))
            here.LetGo = null;
        if (wait && v.Speed < 0.3f && toLine < 6f + 0.5f * v.LengthMetres)   // short of a crossing too
        {
            if (v.WaitingAt != j.Id) { v.WaitingAt = j.Id; v.WaitedSeconds = 0f; }
            v.WaitedSeconds += dt;
            // ...but not in front of somebody about to arrive: that is not a deadlock, it is traffic.
            if (v.WaitedSeconds > life.GiveWayPatienceSeconds && (here.LetGo == null || here.LetGo == v)
                && !here.Inside.Any(x => x.V != v && Conflict(v.MapId, mine, x.M))
                && !here.Coming.Any(x => x.V != v && x.M.In != mine.In && x.V.Speed > 0.5f
                                         && x.ToLine / x.V.Speed < JunctionRules.ImminentSeconds && Conflict(v.MapId, mine, x.M)))
            {
                wait = false;
                here.LetGo = v;
            }
        }
        else if (!wait) v.WaitingAt = null;

        v.Holding = wait;
        return (wait ? holdAt : float.MaxValue, look);
    }

    /// <summary>
    /// Whether a vehicle the lap puts in a junction is in fact still on the lane before it, short of the
    /// line; if so, which leg and how far along. The smoothed line drifts metres from the lanes round
    /// corners: a truck holding at the line was taken to be in the junction and drove on (traced 2026-09-28).
    /// </summary>
    private static bool ShortOfTheLine(DemoVehicle v, ref int leg, ref float along)
        => RouteRules.ShortOfTheLine(v.Route!, v.Line!, v.Lap, ref leg, ref along);

    /// <summary>Two ways through the same junction meet (JunctionRules.Meet), remembered by map.</summary>
    private bool Conflict(string mapId, Movement a, Movement b)
    {
        if (a.In == b.In || a.Out == b.Out) return JunctionRules.Meet(a, b);   // decided without the paths
        var key = (mapId, a.In.Index, a.Out.Index, b.In.Index, b.Out.Index);
        if (_conflicts.TryGetValue(key, out bool c)) return c;
        c = JunctionRules.Meet(a, b);
        _conflicts[key] = c;
        _conflicts[(mapId, b.In.Index, b.Out.Index, a.In.Index, a.Out.Index)] = c;
        return c;
    }
}
