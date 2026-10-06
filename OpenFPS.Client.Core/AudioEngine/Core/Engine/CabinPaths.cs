using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// The ways a vehicle's own sound gets into its cabin, and where each one comes in.
///
/// WHY. Sitting in a car, the interior model (EngineVoiceState, "Sitting in it") worked out the
/// pressure at the ear as ONE signal and played it from one point a little ahead of the head and
/// below. Both ears then heard the same thing: interaural correlation about 1.0 in every band, which
/// is heard as mono, in the head (Cody, 2026-10-06: "the inside of the cab of the cars sounds mono").
/// A real cabin is nothing like that. The engine comes through the firewall and the dash in front of
/// you, each tyre through its own wheel arch and the floor at its corner, the exhaust along the floor
/// behind, the wind at the A-pillars and the mirrors either side of the windscreen. Each of those is
/// its own mechanism with its own noise, so the two ears get different mixes of them: a real cabin is
/// fairly diffuse above about 500 Hz, with the engine's low orders from the front.
///
/// WHAT. The same model, split into those paths (<see cref="Kind"/>), each rendered from its own
/// sources through its own panels (the mass law and the seals of the interior model, per path), and
/// played from where it comes in. Nothing is a copy of anything else: the tyres are a noise per
/// wheel, the wind a noise per side. The paths' powers add to what the single signal had, so the
/// level at the ear is the model's; only where it comes from changes.
///
/// WHERE. In the vehicle's own frame (x right, y up from the road, z forward from the middle of the
/// body), from the cabin's measured box (<see cref="VehicleCabin"/>) and the running gear's wheel
/// layout (<see cref="WheelDynamics"/>). Nothing here is authored per vehicle. A vehicle with no
/// cabin (a motorcycle, a formula car) has no layout and keeps the one interior voice.
///
/// OPENFPS_CABIN_PATHS=0 plays the interior from one point, as before 2026-10-06.
/// </summary>
public static class CabinPaths
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OPENFPS_CABIN_PATHS") != "0";

    public enum Kind
    {
        /// <summary>The engine bay through the firewall, the dash and the floor of the footwell: the
        /// block, the intake, the starter, and the cabin's own modes. Index 0, the voice's own ring.</summary>
        Bulkhead,
        /// <summary>The exhaust along the floor to the tailpipe: under the back of the cabin.</summary>
        Exhaust,
        /// <summary>One wheel's tyre, squeal and spray, through its arch and the floor at that corner.</summary>
        Wheel,
        /// <summary>The wind over the body at the A-pillar and the mirror on one side, and whatever
        /// comes in through that side's open windows.</summary>
        Wind,
        /// <summary>A bus's front door: the door beeper over it, the door engines in the step well, and
        /// the street through the doorway when it is open.</summary>
        Door,
    }

    public readonly record struct Path(Kind Kind, Vector3 At, int Wheel, int Side);

    public sealed class Layout
    {
        public readonly Path[] Paths;
        /// <summary>Per wheel in the server's order (WheelDynamics), the path it comes in by.</summary>
        public readonly int[] PathOfWheel;
        /// <summary>The paths of the wind on the left and right, and the door's, or -1.</summary>
        public readonly int WindLeft, WindRight, Door, Exhaust;
        /// <summary>Per wheel: its share of its axle group's rolling power (the tyres it carries over
        /// the tyres the group carries), so the wheels together roll as loud as the two axles did.</summary>
        public readonly float[] RollingShare;
        /// <summary>Per wheel: how many wheels of its group share the group's squeal when the server
        /// does not send the wheels (the axle's slip drives each).</summary>
        public readonly int[] GroupWheels;

        internal Layout(Path[] paths, int[] pathOfWheel, int windLeft, int windRight, int door, int exhaust,
                        float[] rollingShare, int[] groupWheels)
        {
            Paths = paths; PathOfWheel = pathOfWheel; WindLeft = windLeft; WindRight = windRight; Door = door;
            Exhaust = exhaust; RollingShare = rollingShare; GroupWheels = groupWheels;
        }

        public int Count => Paths.Length;
    }

    /// <summary>How far from the head every path is played, metres: where the one interior voice was
    /// (0.4 below and 0.6 ahead, 0.72 m). The HRTF takes only a direction; the distance sets the gain,
    /// the ear model's shelves and the room send, and the interior model has already worked out the
    /// pressure at the ear, so every path is placed where the single voice was and only turned.</summary>
    public const float PlacedMetres = 0.72f;

    private static readonly ConcurrentDictionary<string, Layout?> _layouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The paths into this vehicle's cabin, or null if it has no cabin (or the split is off).</summary>
    public static Layout? For(VehicleProfile v)
    {
        if (!Enabled || v == null) return null;
        return _layouts.GetOrAdd(v.Name ?? "", _ => Build(v));
    }

    internal static Layout? Build(VehicleProfile v)
    {
        if (VehicleCabin.Measure(v) is not { } g) return null;
        var paths = new List<Path>();
        float halfW = g.Wc * 0.5f;
        // The firewall and the dash: the end of the cabin the engine is at, between the floor and the
        // waist, where the footwell and the bulkhead are.
        float bulkZ = v.EngineAtRear ? g.Back : g.Front;
        float lowY = g.FloorTop + 0.3f * (g.Belt - g.FloorTop);
        paths.Add(new Path(Kind.Bulkhead, new Vector3(0f, lowY, bulkZ), -1, 0));

        // The exhaust runs under the floor to the tailpipe: it comes in where the cabin's floor is
        // nearest the pipe's end.
        var pipe = v.ExhaustSlot;
        int exhaust = paths.Count;
        paths.Add(new Path(Kind.Exhaust, new Vector3(Math.Clamp(pipe.X, -halfW, halfW), g.FloorTop,
                                                     Math.Clamp(pipe.Z, g.Back, g.Front)), -1, 0));

        // Every wheel at its corner, at floor height: the arch and the floor over it.
        var body = new WheelDynamics(v);
        var axles = v.Running.Axles;
        float cogZ = v.Running.CentreOfGravityZ;
        int n = body.Wheels.Length;
        var pathOfWheel = new int[n];
        var tyres = new float[n];
        var groupWheels = new int[n];
        float frontTyres = 0f, rearTyres = 0f;
        int frontCount = 0, rearCount = 0;
        for (int i = 0; i < n; i++)
        {
            var w = body.Wheels[i];
            int per = axles.Length > 0 ? Math.Max(1, axles[Math.Clamp(w.Axle, 0, axles.Length - 1)].TyresPerWheel) : 1;
            tyres[i] = per;
            if (w.Front) { frontTyres += per; frontCount++; } else { rearTyres += per; rearCount++; }
            pathOfWheel[i] = paths.Count;
            paths.Add(new Path(Kind.Wheel, new Vector3(Math.Clamp(w.Y, -halfW, halfW), g.FloorTop, w.X + cogZ), i, MathF.Sign(w.Y)));
        }
        var share = new float[n];
        for (int i = 0; i < n; i++)
        {
            bool front = body.Wheels[i].Front;
            share[i] = tyres[i] / MathF.Max(1f, front ? frontTyres : rearTyres);
            groupWheels[i] = Math.Max(1, front ? frontCount : rearCount);
        }

        // The wind: the A-pillar and the mirror either side of the windscreen, two-thirds of the way up
        // the glass.
        float windY = g.Belt + 0.6f * (g.RoofUnder - g.Belt);
        int windLeft = paths.Count;
        paths.Add(new Path(Kind.Wind, new Vector3(-halfW, windY, g.Front - 0.1f), -1, -1));
        int windRight = paths.Count;
        paths.Add(new Path(Kind.Wind, new Vector3(halfW, windY, g.Front - 0.1f), -1, 1));

        // A vehicle with an air system and a door beeper: the door, on the kerb side, where the air
        // system says it is.
        int door = -1;
        if (!string.IsNullOrEmpty(v.AirSystem) && v.DoorChime)
        {
            float along = 1f, side = 1f;
            try
            {
                var spec = ModelLibrary.Air(v.AirSystem);
                foreach (var p in spec.Ports)
                    if (string.Equals(p.Name, "door", StringComparison.OrdinalIgnoreCase))
                    {
                        along = p.AlongMetres;
                        if (p.LateralMetres != 0f) side = MathF.Sign(p.LateralMetres);
                        break;
                    }
            }
            catch { }
            float z = Math.Clamp(v.LengthMetres * 0.5f - along, g.Back, g.Front);
            door = paths.Count;
            paths.Add(new Path(Kind.Door, new Vector3(side * halfW, g.RoofUnder - 0.2f, z), -1, (int)side));
        }
        return new Layout(paths.ToArray(), pathOfWheel, windLeft, windRight, door, exhaust, share, groupWheels);
    }

    /// <summary>
    /// Where path <paramref name="path"/> is played from, relative to the head, in the vehicle's frame:
    /// the direction from the ear to where it comes in, at <see cref="PlacedMetres"/>. A path straight
    /// through the head (it cannot be: the head is never on a panel) is placed ahead.
    /// </summary>
    public static Vector3 Offset(Layout layout, int path, Vector3 ear)
    {
        var d = layout.Paths[path].At - ear;
        float len = d.Length();
        if (!(len > 1e-3f)) return new Vector3(0f, 0f, PlacedMetres);
        return d / len * PlacedMetres;
    }
}
