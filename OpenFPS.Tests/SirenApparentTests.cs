using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// "Sirens where I'm standing are fluttering" (Cody, 2026-09-26). A siren voice had two writers of
/// where it is heard from, every frame: the car's acoustic path (SetAcousticPath, from the worker —
/// the exhaust point as of the last request, or the edge / probe a blocked source is redirected to)
/// and the siren's own attribute update (its grille, now). The audio thread applies whichever came
/// last, and the game frame between the two calls is tens of milliseconds, so the image swung
/// between two bearings at the frame rate.
/// </summary>
public class SirenApparentTests
{
    private readonly ITestOutputHelper _o;
    public SirenApparentTests(ITestOutputHelper o) => _o = o;

    /// <summary>A car id whose siren controller starts a call within a couple of seconds, so the
    /// test does not wait out a quiet phase of a minute or two.</summary>
    private static int PromptCallId()
    {
        for (int id = 1; id < 20000; id++)
        {
            var c = new SirenController(id);
            for (int k = 0; k < 400; k++)
                if (c.Update(17f, 1f / 240f) != SirenMode.Off) return id;
        }
        throw new InvalidOperationException("no car id starts a call promptly");
    }

    private static float Bearing(Vector3 ear, Vector3 p) => MathF.Atan2(p.X - ear.X, p.Z - ear.Z) * 180f / MathF.PI;

    /// <summary>
    /// A siren is placed by one writer, the registry's pass, every frame. With two (the siren's own update
    /// and the car's path, each with its own apparent position) the image swung between bearings every
    /// frame, 3.6 degrees for a car crossing 100 m out, and a far siren fluttered.
    /// </summary>
    [Fact]
    public void A_siren_is_placed_in_one_place_by_one_writer()
    {
        int id = PromptCallId();
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        var ear = new Vector3(0f, h.Player.EyeHeight, 0f);
        // A patrol car crossing in front, far enough out to be asked about every tenth frame.
        var at = new Vector3(-40f, 0f, 90f);
        var vel = new Vector3(17f, 0f, 0f);
        h.AddCar(id, "police_interceptor", at, vel);
        int siren = h.Voice(VoiceKind.Siren, id);
        Assert.True(h.TickUntil(() => h.Mixer.HasCarried(siren) && h.Mixer.Latest.ContainsKey(siren), 1200),
                    "the siren never sounded with a path");

        float worst = 0f, last = Bearing(ear, h.Mixer.Latest[siren].ApparentPosition);
        for (int f = 0; f < 60; f++)
        {
            at += vel / (float)ClientAudioSystem.UpdateHz;
            h.World.SyncState(new[] { new OpenFPS.Common.Networking.EntityState
            {
                EntityId = id,
                Transform = OpenFPS.Common.Networking.QuantizedTransform.FromTransform(new OpenFPS.Common.Components.Transform { Position = at, Rotation = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0f, 0f) }),
                LinearVelocity = vel,
            } });
            h.Tick();
            System.Threading.Thread.Sleep(2);
            float now = Bearing(ear, h.Mixer.Latest[siren].ApparentPosition);
            // From the second frame: the first puts the car back where this loop starts it.
            if (f > 0) worst = MathF.Max(worst, MathF.Abs(now - last));
            last = now;
        }
        _o.WriteLine($"worst step in bearing from one frame to the next: {worst:F2} degrees");
        Assert.False(h.Mixer.HasPath(siren), "the siren was handed a path by a second writer");
        // The car sweeps about 0.17 degrees a frame from here.
        Assert.True(worst < 0.5f, $"the siren's bearing stepped {worst:F1} degrees in one frame");
    }
}
