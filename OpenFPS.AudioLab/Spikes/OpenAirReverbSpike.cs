using System;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Does NAMING a place put a roof over it?
///
/// It did. The speedway was given zones so that a blind player on two kilometres of identical asphalt
/// could be told whether they were on the front straight or in turn three — and from that moment the
/// whole map sounded like the inside of a building. Every open-air behaviour in the mixer was keyed on
/// "the listener is in the GLOBAL region id", which is not a question about the place at all; it is a
/// question about whether the map has bothered to name it. So the infield's bus was built with a
/// 500 ms decay at full wet (six faces of material "None" read as a sealed mirrored box, which
/// absorbed nothing, which fell through to the default room), and then the ray-traced RT60 was written
/// over the top of it uncapped and ungated, because the cap and the gate were behind the same id test.
///
/// This drives the real provider, the real FMOD graph and the real reverb DSPs, and asks four things:
///   1. Standing in a NAMED open region, the reverb bus is dry and passes no signal.
///   2. Standing in a SEALED room of the same engine, it is wet and rings for the Sabine time.
///   3. A named open region and the unnamed outdoors behave identically — the name changes nothing.
///   4. Hand the open region a ray-traced RT60 (a grandstand at your back, a street with sides) and
///      its bus opens in proportion and is capped, which is the one thing that SHOULD put reverb
///      outdoors — geometry, measured, rather than a region id.
/// </summary>
public static class OpenAirReverbSpike
{
    private const int InfieldRegionId = 20;      // a named stretch of open ground, like the speedway's
    private const int RoomRegionId = 21;         // a sealed concrete room, like the default map's
    private static readonly Vector3 InfieldCentre = new(0f, 1f, 0f);
    private static readonly Vector3 RoomCentre = new(300f, 2f, 0f);

    public static int Run()
    {
        AcousticRegistry.Initialize();

        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }

        try
        {
            provider.SetAcousticMap(BuildMap());

            // One continuous source in each place, so every bus has something to reverberate.
            var onTheInfield = Source(1, InfieldCentre with { Y = 1.5f }, InfieldRegionId);
            var inTheRoom = Source(2, RoomCentre with { Y = 1.5f }, RoomRegionId);
            provider.PlaySpatialSound(onTheInfield);
            provider.PlaySpatialSound(inTheRoom);

            var infield = Settle(provider, onTheInfield, InfieldCentre with { Y = 1.7f }, InfieldRegionId, InfieldRegionId);
            Report("standing in the named infield", infield);

            var outside = Settle(provider, onTheInfield, InfieldCentre with { Y = 1.7f },
                                 AcousticConstants.GlobalRegionId, AcousticConstants.GlobalRegionId);
            Report("standing on unnamed open ground", outside);

            var room = Settle(provider, inTheRoom, RoomCentre with { Y = 1.7f }, RoomRegionId, RoomRegionId);
            Report("standing in the concrete room", room);

            bool ok = true;
            ok &= Check("a named open region is built dry", infield.WetDb < -60f);
            // Absolute levels here are tiny — a reverb tail of a distant noise source metered at the
            // DSP's own output — so what is asserted is the difference between something and nothing.
            ok &= Check("and passes no reverberation: nothing comes out of its DSP",
                        infield.OutputPeak <= 0f);
            ok &= Check("a sealed room is wet", room.WetDb > -1f);
            ok &= Check("and rings for its Sabine time", room.DecayMs > 1000f);
            ok &= Check("and does pass reverberation", room.OutputPeak > 0f);
            ok &= Check("naming open ground changes nothing about it",
                        Math.Abs(infield.WetDb - outside.WetDb) < 0.01f);

            // 4. The rays find a boundary the region never declared — a grandstand deck at your back.
            // This is the only thing that may put reverberation on open ground, and it is measured.
            provider.SetSimulatedReverbDecay(900f, 0.62f, 0.9f, 1.1f);           // the canyon this spike exists to audition
            var withGeometry = Settle(provider, onTheInfield, InfieldCentre with { Y = 1.7f },
                                      InfieldRegionId, InfieldRegionId, warm: 200);
            Report("named infield, rays find 900 ms", withGeometry);

            ok &= Check("a ray-traced decay opens the open-air bus", withGeometry.WetDb > -60f);
            ok &= Check("and it is capped rather than let run",
                        withGeometry.DecayMs <= AcousticConstants.OutdoorMaxDecayMs + 1f);
            ok &= Check("and bounded in level", withGeometry.WetDb <= AcousticConstants.OutdoorMaxWetDb + 0.01f);

            Console.WriteLine(ok
                ? "RESULT: PASS — a region's boundary decides whether it reverberates, not its name."
                : "RESULT: FAIL — see the unmet conditions above.");
            return ok ? 0 : 1;
        }
        finally
        {
            provider.Dispose();
        }
    }

    private readonly record struct BusState(float DecayMs, float WetDb, float InputPeak, float OutputPeak);

    private static void Report(string where, BusState s) =>
        Console.WriteLine($"  {where,-34} decay={s.DecayMs,7:F0} ms  wet={s.WetDb,7:F1} dB  " +
                          $"dsp in={s.InputPeak:F6} out={s.OutputPeak:F6}");

    private static bool Check(string what, bool held)
    {
        Console.WriteLine($"  [{(held ? "PASS" : "FAIL")}] {what}");
        return held;
    }

    private static SpatialEmitter Source(int id, Vector3 position, int regionId) => new()
    {
        EntityId = id,
        Type = EmitterType.WorldLocked,
        IsSynth = true,
        SynthWave = SynthWaveType.Noise,
        SynthFrequency = 220f,
        SynthFilterCutoff = 1.0f,
        Volume = 1.0f,
        Position = position,
        Range = 400f,
        MinDistance = 2f,
        EnableReverb = true,
        TargetRegionId = regionId,
    };

    /// <summary>Runs the provider until the bus fades have settled, then meters the region's own DSP.</summary>
    private static BusState Settle(FmodAudioProvider provider, SpatialEmitter emitter, Vector3 listenerPos,
                                   int listenerRegionId, int measureRegionId, int warm = 140)
    {
        for (int i = 0; i < warm; i++)
        {
            provider.UpdateListener(listenerPos, Quaternion.Identity, Vector3.Zero, listenerRegionId);
            provider.UpdateSpatialAttributes(emitter);
            provider.Update();
            Thread.Sleep(5);
        }

        // Noise into a reverb is a poor thing to read once; take the loudest block over a stretch.
        float decayMs = 0f, wetDb = -80f, inPeak = 0f, outPeak = 0f;
        for (int i = 0; i < 120; i++)
        {
            provider.UpdateListener(listenerPos, Quaternion.Identity, Vector3.Zero, listenerRegionId);
            provider.UpdateSpatialAttributes(emitter);
            provider.Update();
            provider.TryGetReverbSettings(measureRegionId, out decayMs, out wetDb);
            if (provider.TryMeterReverbBus(measureRegionId, out float pin, out float pout))
            {
                inPeak = Math.Max(inPeak, pin);
                outPeak = Math.Max(outPeak, pout);
            }
            Thread.Sleep(5);
        }
        return new BusState(decayMs, wetDb, inPeak, outPeak);
    }

    /// <summary>A named patch of open ground and a sealed room, side by side on one map.</summary>
    private static AcousticMap BuildMap()
    {
        var map = new AcousticMap(new Vector3(800, 60, 500), new Vector3(-400, 0, -250))
        {
            GlobalEnvironmentId = AcousticConstants.GlobalRegionId
        };

        // The outdoors, as the generator builds it: a map-sized box with no surfaces on it.
        map.Regions[AcousticConstants.GlobalRegionId] = new RegionComponent
        {
            FriendlyName = "Outside",
            IsIndoor = false,
            RoomSize = new Vector3(800, 60, 500),
            ReverbTimeScale = 0f,
            Materials = new int[6],
        };
        map.RegionPositions[AcousticConstants.GlobalRegionId] = Vector3.Zero;

        // The speedway's infield: a named place, and nothing else different about it.
        map.Regions[InfieldRegionId] = new RegionComponent
        {
            FriendlyName = "Infield",
            IsIndoor = false,
            RoomSize = new Vector3(220, 6, 210),
            ReverbTimeScale = 1.0f,
            Materials = new int[6],
        };
        map.RegionPositions[InfieldRegionId] = InfieldCentre;
        map.RegionRotations[InfieldRegionId] = Quaternion.Identity;

        int concrete = AcousticRegistry.GetProperties("Concrete").ResonanceIndex;
        map.Regions[RoomRegionId] = new RegionComponent
        {
            FriendlyName = "Concrete Room",
            IsIndoor = true,
            RoomSize = new Vector3(10, 5, 10),
            ReverbTimeScale = 1.0f,
            Materials = new[] { concrete, concrete, concrete, concrete, concrete, concrete },
        };
        map.RegionPositions[RoomRegionId] = RoomCentre;
        map.RegionRotations[RoomRegionId] = Quaternion.Identity;

        return map;
    }
}
