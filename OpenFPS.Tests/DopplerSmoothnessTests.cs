using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A car passing CLOSE must glide in pitch, not step.
///
/// Doppler is applied as a channel pitch, and a channel pitch changes the instant it is set. The
/// attribute loop runs at 250 Hz precisely so a pass-by is a glide — but the SOURCE POSITION it
/// computes from only changes when the game thread resubmits the emitter, measured at 22 ms. So the
/// pitch was a 45 Hz staircase written 250 times a second.
///
/// The size of each step goes as v^2/d, so this is a near-field fault and nothing else: a car at
/// fifty metres steps under a per cent, and the same car at five metres steps by nearly eight —
/// well over a semitone, forty-five times a second. Reported as the cars "doing that weird auto-tune
/// thing, high pitch, low pitch", and only for the close ones.
/// </summary>
public class DopplerSmoothnessTests
{
    /// <summary>The worst pitch step over a pass, as a fraction, at a given update rate.</summary>
    private static float WorstStep(float missDistance, float speed, float updateHz, bool deadReckon)
    {
        var listener = new Vector3(0, 0, 0);
        const float attributeHz = 250f;
        float attributeDt = 1f / attributeHz;
        float updateDt = 1f / updateHz;

        // The car runs along x at `missDistance` in z, from well before to well after the listener.
        float t = -1.5f;
        float lastKnownT = t;
        float worst = 0f, prev = float.NaN;
        var vel = new Vector3(speed, 0, 0);

        while (t < 1.5f)
        {
            // The game thread resubmits at updateHz; between those the provider holds what it has.
            if (t - lastKnownT >= updateDt) lastKnownT = t;

            float knownX = speed * lastKnownT;
            var known = new Vector3(knownX, 0, missDistance);
            double age = deadReckon ? Math.Min(t - lastKnownT, 0.05) : 0.0;
            var used = known + vel * (float)age;

            float f = AudioPhysics.DopplerFactor(listener, Vector3.Zero, used, vel);
            if (!float.IsNaN(prev))
            {
                float step = MathF.Abs(f - prev) / prev;
                if (step > worst) worst = step;
            }
            prev = f;
            t += attributeDt;
        }
        return worst;
    }

    [Fact]
    public void AClosePassGlidesInPitchInsteadOfStepping()
    {
        const float speed = 78f;      // 280 km/h
        const float updateHz = 45f;   // what the emitter refresh actually measures at

        float nearBefore = WorstStep(5f, speed, updateHz, deadReckon: false);
        float nearAfter  = WorstStep(5f, speed, updateHz, deadReckon: true);
        float farBefore  = WorstStep(50f, speed, updateHz, deadReckon: false);

        // The fault is near-field: far cars were always fine, which is why only the close ones were
        // reported. If this stops being true the test is measuring the wrong thing.
        Assert.True(farBefore < 0.015f, $"a car at 50 m stepped {farBefore:P1} — the premise is wrong");
        Assert.True(nearBefore > 0.04f, $"a car at 5 m only stepped {nearBefore:P1} — no fault to fix");

        // A quarter of a semitone (1.5%) is about where a pitch change stops reading as a step.
        Assert.True(nearAfter < 0.015f,
            $"a car passing at 5 m still steps {nearAfter:P1} per update (was {nearBefore:P1}) — that is a pitch quantiser, not Doppler");
    }
}
