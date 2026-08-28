using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Phase 4d spike: probe whether Steam Audio's reflection simulation yields usable PARAMETRIC reverb
/// times (RT60 per band) so we can drive the engine's reverb from real geometry instead of Sabine
/// guesswork. Runs reflections for a source+listener inside a SEALED concrete room and, for comparison,
/// in the OPEN (floor only). PASS = the enclosed room reports a clearly longer reverb decay than open.
/// Headless. If RT60 doesn't come through parametrically here, 4d must instead go through the heavier
/// convolution path (iplReflectionEffect in the mixer) — this spike tells us which.
/// </summary>
public static class SimReflectSpike
{
    public static int Run()
    {
        AcousticRegistry.Initialize();

        var ctxS = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("ctx failed"); return 1; }

        var q = Quaternion.Identity;
        var sealedRoom = new[]
        {
            new SteamAudioScene.Box(new Vector3(5, -0.25f, 5), new Vector3(11, 0.5f, 11), q, "Concrete"), // floor
            new SteamAudioScene.Box(new Vector3(5, 4.25f, 5),  new Vector3(11, 0.5f, 11), q, "Concrete"), // ceiling
            new SteamAudioScene.Box(new Vector3(5, 2, 10.25f), new Vector3(11, 4, 0.5f), q, "Concrete"),  // north
            new SteamAudioScene.Box(new Vector3(5, 2, -0.25f), new Vector3(11, 4, 0.5f), q, "Concrete"),  // south
            new SteamAudioScene.Box(new Vector3(10.25f, 2, 5), new Vector3(0.5f, 4, 11), q, "Concrete"),  // east
            new SteamAudioScene.Box(new Vector3(-0.25f, 2, 5), new Vector3(0.5f, 4, 11), q, "Concrete"),  // west
        };
        var openSpace = new[]
        {
            new SteamAudioScene.Box(new Vector3(5, -0.25f, 5), new Vector3(40, 0.5f, 40), q, "Concrete"), // floor only
        };

        var src = new Vector3(5, 1.5f, 5);
        var listener = new Vector3(3, 1.5f, 3);

        var rtRoom = Reflect(ctx, sealedRoom, src, listener);
        var rtOpen = Reflect(ctx, openSpace, src, listener);

        float roomMax = MathF.Max(rtRoom.X, MathF.Max(rtRoom.Y, rtRoom.Z));
        float openMax = MathF.Max(rtOpen.X, MathF.Max(rtOpen.Y, rtOpen.Z));
        Console.WriteLine($"Sealed room RT60 (low,mid,high) = ({rtRoom.X:F2},{rtRoom.Y:F2},{rtRoom.Z:F2}) s   max={roomMax:F2}");
        Console.WriteLine($"Open space  RT60 (low,mid,high) = ({rtOpen.X:F2},{rtOpen.Y:F2},{rtOpen.Z:F2}) s   max={openMax:F2}");

        bool ok = roomMax > 0.1f && roomMax > openMax + 0.05f;
        Console.WriteLine(ok
            ? "RESULT: PASSED — parametric reflection RT60 works: the sealed room reverberates clearly longer than the open space. 4d can drive reverb from geometry."
            : "RESULT: FAILED — parametric RT60 not usable here (enclosed not clearly > open); 4d would need the convolution path.");

        Phonon.iplContextRelease(ref ctx);
        return ok ? 0 : 2;
    }

    // Exercises the real SteamAudioSimulator reflections path (enableReflections + GetReverb).
    private static Vector3 Reflect(IntPtr ctx, SteamAudioScene.Box[] boxes, Vector3 src, Vector3 listener)
    {
        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        if (!scene.IsBuilt) return Vector3.Zero;

        using var sim = new SteamAudioSimulator(ctx, maxSources: 4, enableReflections: true, enableDirect: false);
        if (!sim.IsValid) return Vector3.Zero;
        sim.SetScene(scene);

        IntPtr source = sim.AcquireSource();
        if (source == IntPtr.Zero) return Vector3.Zero;

        sim.SetSourceInputs(source, src);
        sim.SetListener(listener);
        sim.Run();
        var r = sim.GetReverb(source);
        return new Vector3(r.Rt60Low, r.Rt60Mid, r.Rt60High);
    }
}
