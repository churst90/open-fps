using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Aircraft;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// "The prop plane and the helicopter flange when they're flying." Two identical pulse trains whose
/// relative phase drifts are a flanger. The twin turboprop's props ran half a per cent apart, and the
/// piston single's prop ran on its own smoothed speed rather than on the crank it is bolted to, so
/// its blade rate and the exhaust's firing rate (the same frequencies on a two-blade prop and a
/// four-cylinder engine) slid past each other. Measured as how much the fine structure of the
/// spectrum wanders from frame to frame with the listener still.
/// </summary>
public class PropellerPhaseTests
{
    private const int Sr = 44100;

    private static double Wander(AircraftProfile p)
    {
        var syn = new AircraftSynth(p, Sr, 3);
        syn.PlaceAtLever(0.7f); syn.Lever = 0.7f;
        syn.SetListener(new Vector3(60f, -40f, 10f));
        int n = Sr * 8;
        var x = new double[n];
        for (int i = 0; i < n; i++) { syn.Step(); x[i] = syn.Total; }
        // Twelfth-octave bands 400 Hz - 3 kHz, 93 ms frames, Goertzel per 10 Hz inside each band.
        var bands = new List<double>();
        for (double f = 400; f < 3000; f *= Math.Pow(2, 1.0 / 12)) bands.Add(f);
        const int N = 4096;
        var series = bands.Select(_ => new List<double>()).ToList();
        for (int s0 = Sr * 2; s0 + N <= n; s0 += N)
        {
            for (int b = 0; b < bands.Count; b++)
            {
                double e = 1e-30, hi = bands[b] * Math.Pow(2, 1.0 / 12);
                for (double f = bands[b]; f < hi; f += 10)
                {
                    double w = 2 * Math.PI * f / Sr, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
                    for (int i = 0; i < N; i++)
                    {
                        double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
                        double s = x[s0 + i] * win + c * s1 - s2; s2 = s1; s1 = s;
                    }
                    e += s1 * s1 + s2 * s2 - c * s1 * s2;
                }
                series[b].Add(10 * Math.Log10(e));
            }
        }
        return series.Average(sr => { double m = sr.Average(); return Math.Sqrt(sr.Average(v => (v - m) * (v - m))); });
    }

    [Fact]
    public void ASynchrophasedTwinIsAsSteadyAsOneEngine()
    {
        var p = AircraftProfile.Presets["turboprop"]();
        Assert.True(p.Synchrophased);
        double twin = Wander(p), single = Wander(p with { Engines = 1 });
        double free = Wander(p with { Synchrophased = false });
        Assert.True(twin < single + 0.8, $"synchrophased twin wanders {twin:F2} dB against {single:F2} for one engine");
        Assert.True(free > twin + 1.0, $"the unsynchronised twin ({free:F2}) should be the flanger, not {twin:F2}");
    }

    [Fact]
    public void APistonPropTurnsWithItsCrank()
    {
        var p = AircraftProfile.Presets["piston_single"]();
        Assert.True(Wander(p) < 1.5);
    }
}
