using System;
using System.Collections.Generic;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Walk round the rooms map's wood room with the megaphone running, through the real provider, and
/// write the mix to a WAV so "it pops when I walk" can be measured rather than argued about.
///
/// This exists because a five-minute capture of a live session showed one waveform discontinuity in
/// three hundred seconds and none at all inside the rooms, while the report from the chair was "lots
/// of popping and clicking when I walk around this room". Both are true at once only if what pops is
/// not a broken sample stream but something the engine does on purpose, per step or per frame — a
/// reverb unit restated, a filter stepped, a copy started. Each of those is a mechanism that can be
/// switched off here, one at a time, and the capture compared.
///
/// Everything the game thread sends the provider each frame is sent here too, from the same models:
/// the listener, the megaphone's attributes and acoustic path, the enclosure survey that drives the
/// reverb, and the near-field boundary probes. Nothing is a stub except the world, which is the wood
/// room's own boxes copied from <c>maps/default.json</c>.
///
/// Run from the lab's bin dir:
///   AudioLab --room-walk [seconds=30] [cone=off] [reverb=off] [boundary=off] [steps=off]
///                        [megaphone=off] [still] [out=/tmp/openfps-roomwalk.wav]
/// </summary>
public static class RoomWalkSpike
{
    private const int RoomId = 310;
    private const int MegaphoneId = 320;
    private static readonly Vector3 RoomCentre = new(7f, 2f, 15f);
    private static readonly Vector3 MegaphonePos = new(7f, 1.5f, 15f);
    private static readonly Vector3 Doorway = new(7f, 1f, 10f);

    public static int Run(string[] args)
    {
        float seconds = Num(args, "seconds", 30f);
        bool cone = !Off(args, "cone");
        bool reverb = !Off(args, "reverb");
        bool boundary = !Off(args, "boundary");
        bool steps = !Off(args, "steps");
        bool megaphone = !Off(args, "megaphone");
        bool still = Array.Exists(args, a => a == "still");
        bool echo = Array.Exists(args, a => a == "echo=on");
        // Measurement knobs for the reverb unit's own early-reflection share and late delay.
        if (float.TryParse(Str(args, "earlylate"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float el))
            FmodAudioProvider.ReverbEarlyLateOverride = el;
        if (float.TryParse(Str(args, "latedelay"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ld))
            FmodAudioProvider.ReverbLateDelayOverride = ld;
        bool echoStarted = false;
        string outPath = Str(args, "out") ?? "/tmp/openfps-roomwalk.wav";

        AcousticRegistry.Initialize();
        if (Array.Exists(args, a => a == "clock")) return ClockCheck(outPath);

        // The lab's build carries the synthesised sound sets, not the recorded ones, and a run with
        // no megaphone file is thirty seconds of a reverb reverberating silence. Say so up front.
        string soundRoot = System.IO.Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS");
        if (!System.IO.File.Exists(System.IO.Path.Combine(soundRoot, "BEACONS", "megaphone.wav")))
        {
            Console.WriteLine($"  FAIL: no BEACONS/megaphone.wav under {soundRoot}.");
            Console.WriteLine("  Link the client's recorded sets in first, e.g.");
            Console.WriteLine($"    ln -s <repo>/OpenFPS.Client/ASSETS/SOUNDS/BEACONS {soundRoot}/BEACONS");
            Console.WriteLine($"    ln -s <repo>/OpenFPS.Client/ASSETS/SOUNDS/FOOTSTEPS {soundRoot}/FOOTSTEPS");
            return 1;
        }

        // The file writer REPLACES the sound card: exact, and silent, so a measurement run makes no noise.
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", outPath);

        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }
        try
        {
            provider.SetAcousticMap(BuildMap());
            var boxes = WoodRoomBoxes();
            var enclosureSolids = new List<Enclosure.Solid>();
            foreach (var b in boxes) enclosureSolids.Add(new Enclosure.Solid(b.Center, b.Size, Quaternion.Identity, b.Material));

            Console.WriteLine($"  wood room: {boxes.Count} boxes, megaphone {(megaphone ? "on" : "off")}, cone {(cone ? "on" : "off")}, "
                            + $"reverb survey {(reverb ? "on" : "off")}, boundary probes {(boundary ? "on" : "off")}, "
                            + $"footsteps {(steps ? "on" : "off")}, {(still ? "standing still" : "walking")}, {seconds:F0} s -> {outPath}");

            var mega = new SpatialEmitter
            {
                EntityId = MegaphoneId,
                SoundId = "BEACONS/megaphone",
                Mode = PlaybackMode.LoopOne,
                Type = EmitterType.WorldLocked,
                Position = MegaphonePos,
                ApparentPosition = MegaphonePos,
                Direction = new Vector3(0f, 0f, -1f),   // rotation (0,1,0,0): beams south, out of the door
                Volume = 1.0f,
                Range = 25f,
                MinDistance = 1f,
                Pitch = 1f,
                ConeInside = cone ? 40f : 360f,
                ConeOutside = cone ? 110f : 360f,
                ConeOutsideVolume = cone ? 0.05f : 1f,
                EqLow = 1f, EqMid = 1f, EqHigh = 1f,
                ApertureFactor = 1f,
                EnableReverb = true,
                TargetRegionId = RoomId,
            };
            if (megaphone) provider.PlaySpatialSound(mega);

            // The walk: a rectangle a metre and a half inside the walls, at walking pace, facing the
            // way it goes. Every corner passes the megaphone at a different angle to its beam.
            var corners = new[]
            {
                new Vector3(3.5f, 1.7f, 11.5f), new Vector3(10.5f, 1.7f, 11.5f),
                new Vector3(10.5f, 1.7f, 18.5f), new Vector3(3.5f, 1.7f, 18.5f),
            };
            const float speed = 1.4f;
            float perimeter = 0f;
            for (int i = 0; i < corners.Length; i++) perimeter += Vector3.Distance(corners[i], corners[(i + 1) % corners.Length]);

            var probeDirs = BoundaryModel.ProbeDirections;
            var probes = new BoundaryProbe[probeDirs.Length];
            int footstepIndex = 0;
            double nextStepAt = 0.6;
            int frames = 0, surveys = 0;
            float lastDecayMs = 0f, lastEnclosure = 0f;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double lastFrame = 0;
            Vector3 lastPos = corners[0];
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                double t = sw.Elapsed.TotalSeconds;
                float dt = (float)Math.Max(1e-3, t - lastFrame);
                lastFrame = t;

                Vector3 eye = still ? corners[0] with { X = 5f, Z = 13f } : Along(corners, (float)(t * speed) % perimeter);
                Vector3 vel = (eye - lastPos) / dt;
                lastPos = eye;
                Vector3 heading = vel.LengthSquared() > 1e-4f ? Vector3.Normalize(vel with { Y = 0f }) : new Vector3(0, 0, 1);
                var rot = Yaw(heading);

                provider.UpdateListener(eye, rot, vel, RoomId);

                if (megaphone)
                {
                    provider.UpdateSpatialAttributes(mega);
                    float dist = Vector3.Distance(eye, MegaphonePos);
                    provider.SetAcousticPath(MegaphoneId, new AcousticPathData
                    {
                        Occlusion = 0f, EqLow = 1f, EqMid = 1f, EqHigh = 1f,
                        ApparentPosition = MegaphonePos, EffectiveDistance = dist,
                        ApertureFactor = 1f, RoomGain = 1f, TransmissionBleed = 0f,
                        AirAbsorption = 0f, RegionId = RoomId, IsReflection = false,
                    });
                }

                // The room, surveyed from where the listener stands — every sixth frame, as the worker does.
                if (reverb && frames % 6 == 0)
                {
                    var survey = Enclosure.Look(eye, enclosureSolids);
                    var (low, mid, high) = Enclosure.DecaySeconds(survey);
                    lastDecayMs = Math.Clamp(mid * 1000f, AcousticConstants.MinReverbDecayMs, AcousticConstants.MaxReverbDecayMs);
                    lastEnclosure = survey.Enclosure;
                    provider.SetSimulatedReverbDecay(lastDecayMs, survey.Enclosure,
                        Math.Clamp(high / MathF.Max(0.01f, mid), 0.1f, 2f),
                        Math.Clamp(low / MathF.Max(0.01f, mid), 0.1f, 4f));
                    provider.SetListenerReverbField(survey.ReturnDirection, survey.Anisotropy, survey.MeanFreePathMetres);
                    surveys++;
                }

                // Near-field surfaces round the head, the way ClientAudioSystem.UpdateBoundaryProbes finds them.
                if (boundary)
                {
                    for (int i = 0; i < probeDirs.Length; i++)
                    {
                        Vector3 dir = Vector3.Transform(probeDirs[i], rot);
                        float best = BoundaryModel.MaxDistance; string mat = "Generic";
                        foreach (var b in boxes)
                        {
                            if (GeometryUtils.RayIntersectsOBB(eye, dir, b.Center, b.Size, Quaternion.Identity, out float d)
                                && d > 0f && d < best) { best = d; mat = b.Material; }
                        }
                        probes[i] = new BoundaryProbe(probeDirs[i], best, mat);
                    }
                    provider.UpdateBoundaries(probes);
                }

                // echo=on: three seconds in, a wall starts answering — one image-source voice of the
                // megaphone, a hundred milliseconds late, the way ClientAudioSystem starts one. What is
                // measured afterwards is that the copy joins mid-file without a step and lags by its
                // delay rather than being the announcement again from the top.
                if (megaphone && echo && !echoStarted && t >= 3.0)
                {
                    echoStarted = true;
                    var image = new Vector3(7f, 1.5f, 25f);   // mirrored through the north wall
                    provider.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = -30000 - (MegaphoneId * 5),
                        SoundId = "BEACONS/megaphone",
                        Mode = PlaybackMode.LoopOne,
                        Type = EmitterType.WorldLocked,
                        Position = image, ApparentPosition = image,
                        Volume = 0.6f, Range = 20f, MinDistance = 1f, Pitch = 1f,
                        ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f,
                        EqLow = 0.9f, EqMid = 0.9f, EqHigh = 0.8f, ApertureFactor = 1f,
                        IsReflection = true, ReflectionOf = MegaphoneId, DelayMs = 100f,
                        TargetRegionId = RoomId,
                    });
                }

                // A footstep every half second or so, at the feet, as OnPlayerFootstep submits it.
                if (steps && !still && t >= nextStepAt)
                {
                    nextStepAt = t + 0.55;
                    int id = -100 - (footstepIndex % 12);
                    string file = "FOOTSTEPS/Wood/Wood0/" + (1 + footstepIndex % 4);
                    footstepIndex++;
                    provider.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = id,
                        SoundId = file,
                        Mode = PlaybackMode.Single,
                        Type = EmitterType.WorldLocked,
                        Position = eye with { Y = 0.1f },
                        ApparentPosition = eye with { Y = 0.1f },
                        Volume = OpenFPS.Common.Loudness.Place(OpenFPS.Common.Loudness.FootstepDb).Gain, Range = 15f,
                        MinDistance = OpenFPS.Common.Loudness.Place(OpenFPS.Common.Loudness.FootstepDb).ReferenceDistance, Pitch = 1f,
                        ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f,
                        EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                        IsEvent = true, Essential = true,
                        EnableReverb = true, TargetRegionId = RoomId,
                    });
                }

                provider.Update();
                frames++;
                Thread.Sleep(16);
            }

            Console.WriteLine($"  {frames} frames, {surveys} surveys; last survey decay {lastDecayMs:F0} ms, enclosure {lastEnclosure:P0}; "
                            + $"reverb applied {provider.SimulatedReverbDecayMs:F0} ms at {provider.OutdoorReverbWetDb:F1} dB wet");
            if (megaphone) provider.StopSound(MegaphoneId);
            provider.Update();
            return 0;
        }
        finally
        {
            provider.Dispose();
            Console.WriteLine($"  mix written to {outPath}");
        }
    }

    /// <summary>
    /// Does a scheduled start (DelayMs) and the onset ramp actually land on FMOD's clock?
    ///
    /// Both are written against the clock the channel reports for ITSELF, before it has started. If
    /// that clock were not the parent's, every delay and every fade point would be in the past and
    /// silently ignored — a "delayed" reflection would start at once, and a one-shot's 6 ms ramp
    /// would never happen. Plays the same footstep at 1.0 s with no delay, at 2.0 s with 300 ms of
    /// delay, and at 3.0 s from the middle of the megaphone file with the ramp; the WAV says when
    /// each actually began and whether the third starts from silence.
    /// </summary>
    private static int ClockCheck(string outPath)
    {
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", outPath);
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }
        try
        {
            provider.UpdateListener(new Vector3(0, 1.7f, 0), Quaternion.Identity, Vector3.Zero, -1);
            SpatialEmitter Step(int id, float delayMs, string sound, bool reflection) => new()
            {
                EntityId = id, SoundId = sound, Mode = PlaybackMode.Single, Type = EmitterType.WorldLocked,
                Position = new Vector3(0, 0.1f, 1f), ApparentPosition = new Vector3(0, 0.1f, 1f),
                Volume = 1f, Range = 15f, MinDistance = 1f, Pitch = 1f,
                ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f,
                EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                IsEvent = !reflection, IsReflection = reflection, DelayMs = delayMs,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool a = false, b = false, c = false, d = false;
            while (sw.Elapsed.TotalSeconds < 4.5)
            {
                double t = sw.Elapsed.TotalSeconds;
                if (!a && t >= 1.0) { a = true; provider.PlaySpatialSound(Step(-1, 0f, "FOOTSTEPS/Wood/Wood0/1", false)); Console.WriteLine($"  {t:F3}s asked: footstep, no delay"); }
                if (!b && t >= 2.0) { b = true; provider.PlaySpatialSound(Step(-2, 300f, "FOOTSTEPS/Wood/Wood0/1", false)); Console.WriteLine($"  {t:F3}s asked: footstep, 300 ms delay"); }
                if (!c && t >= 3.0)
                {
                    c = true;
                    // A sustained source, then a copy of it started 200 ms later at its position: the
                    // copy joins mid-file, so without the ramp it is a step from silence.
                    var src = Step(-3, 0f, "BEACONS/megaphone", false); src.IsEvent = false; src.Mode = PlaybackMode.LoopOne;
                    provider.PlaySpatialSound(src);
                    Console.WriteLine($"  {t:F3}s asked: megaphone");
                }
                if (!d && t >= 3.5)
                {
                    d = true;
                    var copy = Step(-4, 200f, "BEACONS/megaphone", true); copy.Mode = PlaybackMode.LoopOne; copy.ReflectionOf = -3;
                    provider.PlaySpatialSound(copy);
                    Console.WriteLine($"  {t:F3}s asked: reflection of the megaphone, 200 ms delay, mid-file");
                }
                provider.Update();
                Thread.Sleep(8);
            }
            return 0;
        }
        finally { provider.Dispose(); Console.WriteLine($"  mix written to {outPath}"); }
    }

    private static Vector3 Along(Vector3[] corners, float s)
    {
        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 a = corners[i], b = corners[(i + 1) % corners.Length];
            float len = Vector3.Distance(a, b);
            if (s <= len) return Vector3.Lerp(a, b, s / len);
            s -= len;
        }
        return corners[0];
    }

    private static Quaternion Yaw(Vector3 forward)
    {
        float yaw = MathF.Atan2(forward.X, forward.Z);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    }

    /// <summary>A solid box of the wood room, as maps/default.json builds it: position is the centre,
    /// size is ColliderSize × Scale.</summary>
    public readonly record struct Box(Vector3 Center, Vector3 Size, string Material);

    public static List<Box> WoodRoomBoxes() => new()
    {
        // The concrete slab under the whole play area.
        new(new Vector3(0, 0, 10), new Vector3(40, 0.1f, 40), "Concrete"),
        // Room B: four concrete walls with a two-metre doorway in the south one, and a wood floor.
        new(new Vector3(7, 2, 20), new Vector3(10, 4, 0.5f), "Concrete"),      // north
        new(new Vector3(12, 2, 15), new Vector3(0.5f, 4, 10), "Concrete"),     // east
        new(new Vector3(2, 2, 15), new Vector3(0.5f, 4, 10), "Concrete"),      // west
        new(new Vector3(4, 2, 10), new Vector3(4, 4, 0.5f), "Concrete"),       // south-left
        new(new Vector3(10, 2, 10), new Vector3(4, 4, 0.5f), "Concrete"),      // south-right
        new(new Vector3(7, 0.05f, 15), new Vector3(10, 0.1f, 10), "Wood"),     // floor
    };

    private static AcousticMap BuildMap()
    {
        var map = new AcousticMap(new Vector3(100, 40, 100), new Vector3(-50, 0, -50))
        {
            GlobalEnvironmentId = AcousticConstants.GlobalRegionId
        };
        int wood = AcousticRegistry.GetProperties("Wood").ResonanceIndex;
        map.Regions[RoomId] = new RegionComponent
        {
            FriendlyName = "Wood Room",
            IsIndoor = true,
            RoomSize = new Vector3(10, 5, 10),
            ReverbTimeScale = 1.0f,
            Materials = new[] { wood, wood, wood, wood, wood, wood }
        };
        map.RegionPositions[RoomId] = RoomCentre;
        map.RegionRotations[RoomId] = Quaternion.Identity;
        map.Portals[311] = (new PortalComponent
        {
            RegionAId = AcousticConstants.GlobalRegionId,
            RegionBId = RoomId,
            ApertureSize = 2.0f
        }, Doorway);
        return map;
    }

    private static bool Off(string[] args, string key) => Array.Exists(args, a => a == key + "=off");
    private static string? Str(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=")) return a[(key.Length + 1)..];
        return null;
    }
    private static float Num(string[] args, string key, float fallback)
        => float.TryParse(Str(args, key), System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
}
