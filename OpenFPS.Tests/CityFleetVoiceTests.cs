using System.Collections.Concurrent;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The city's traffic sounds as it did when Cody approved it (2026-10-04 evening, "vehicles sound good").
///
/// On 2026-10-05 he heard the vehicles as "too reverby, and they all sound the same", after a day
/// that merged six changes into the vehicle code (tyres at the device's rate, the bus compressor,
/// machines built on a base, the exhaust slot, the road air horn, the network trim). Rendered again,
/// every city vehicle's voice was sample for sample what the approved build (cc1855a8) made, so
/// nothing in the voice had moved. This holds it there: each vehicle the city runs, driven away from
/// a stop to 50 km/h and held, measured at a metre in three bands. A change to any of them has to
/// be made on purpose, by ear, and the table updated with it.
///
/// The table is the approved build's own output (the same numbers come out of cc1855a8 and of
/// 94428d20). Half a decibel either way is room for floating point on another machine, not for a
/// change anyone would hear.
///
/// Changed on purpose on 2026-10-05, when the engine bays were opened up ("the front of the car seems
/// to be quiet"): the bay is now worked out from its openings and lining (EngineBaySpec) instead of a
/// flat 0.15 on every car. At this cruise the cars are their tyres and moved by at most 0.4 dB; the
/// diesel pickup (bay 0.30 to 0.66) gained 1.8 dB below 250 Hz, the step van (0.5 to 0.87) 1.7 / 0.8 /
/// 1.4 dB, and the mail truck (0.25 to 0.66) 0.4 dB below 250 Hz. The trucks, the bus and the bikes
/// did not move. This table is that build's output; it waits on Cody's ear like any other change.
///
/// What the table also shows: at this cruise the commuter cars (i4_compact, i4_economy, i4_midsize,
/// i6_street, mail_truck, diesel_i4) measure within half a decibel of each other in every band,
/// 74.5 / 86.4 / 81.7 dB. Their engines are 11 to 20 dB under their tyres there, and every car has
/// the same tyre (TyreProfile.SportsOnAsphalt). That is why they sound alike at a cruise; it was so
/// in the approved build too.
/// </summary>
public class CityFleetVoiceTests
{
    private readonly ITestOutputHelper _o;
    public CityFleetVoiceTests(ITestOutputHelper o) => _o = o;

    private const int Sr = 44100, Block = 512;

    /// <summary>dB SPL at 1 m over the 50 km/h cruise: below 250 Hz, 250 Hz to 2 kHz, above 2 kHz;
    /// and the median rpm there.</summary>
    private static readonly Dictionary<string, (float Low, float Mid, float High, float Rpm)> Approved = new()
    {
        ["boxer4_street"] = (81.5f, 89.2f, 82.7f, 3312f),
        ["cummins_compound"] = (105.5f, 103.2f, 94.5f, 1994f),
        ["diesel_i4"] = (80.0f, 86.8f, 82.0f, 1535f),
        ["diesel_truck"] = (93.6f, 96.3f, 92.2f, 1372f),
        ["duramax_compound"] = (107.6f, 106.5f, 94.0f, 1768f),
        ["i4_compact"] = (75.1f, 86.5f, 81.8f, 2537f),
        ["i4_economy"] = (74.9f, 86.4f, 81.7f, 2535f),
        ["i4_midsize"] = (75.1f, 86.4f, 81.6f, 3187f),
        ["i4_sport_street"] = (91.8f, 97.7f, 87.8f, 3918f),
        ["i4_turbo"] = (89.2f, 92.2f, 85.4f, 2983f),
        ["i6_street"] = (75.2f, 86.7f, 82.3f, 2938f),
        ["mail_truck"] = (74.6f, 86.3f, 82.2f, 2353f),
        ["pickup_v8_flowmaster"] = (95.5f, 102.5f, 93.1f, 2754f),
        ["police_interceptor"] = (78.9f, 88.5f, 83.0f, 2708f),
        ["single"] = (101.1f, 107.1f, 98.9f, 5098f),
        ["step_van"] = (86.8f, 90.2f, 87.0f, 1540f),
        ["transit_bus"] = (90.4f, 93.9f, 90.3f, 1976f),
        ["v6"] = (78.7f, 88.5f, 82.6f, 3917f),
        ["v8_mild"] = (97.6f, 102.1f, 93.4f, 2152f),
        ["vtwin_slipon"] = (95.8f, 95.1f, 89.4f, 2650f),
        ["vtwin_stock"] = (77.6f, 82.6f, 79.0f, 2649f),
    };

    /// <summary>Every road vehicle the city map runs (OpenFPS.Server/maps/city.json "Vehicles").</summary>
    public static readonly string[] CityTraffic =
    {
        "boxer4_street", "cummins_compound", "diesel_i4", "diesel_truck", "duramax_compound", "i4_compact",
        "i4_economy", "i4_midsize", "i4_sport_street", "i4_turbo", "i6_street", "mail_truck",
        "pickup_v8_flowmaster", "police_interceptor", "single", "step_van", "transit_bus", "v6", "v8_mild",
        "vtwin_slipon", "vtwin_stock",
    };

    /// <summary>
    /// The voice the mixer plays (<see cref="EngineVoiceState"/>), pulled away from a standstill at
    /// 2 m/s² to 50 km/h and held there: 4 s still, the ramp, then 6 s of cruise metered.
    /// </summary>
    internal static (float Low, float Mid, float High, float Rpm) Fingerprint(string key)
    {
        var v = MachineRegistry.VehicleFor(key);
        var voice = new EngineVoiceState(v, Sr, 11);
        voice.Revive();
        var buf = new float[Block];
        float dtBlock = Block / (float)Sr, target = 0f, top = 50f / 3.6f;
        int still = (int)(4f / dtBlock), ramp = (int)(top / 2f / dtBlock), cruise = (int)(6f / dtBlock);
        // Two poles at each corner, so a band is a band and not a shelf.
        float aLow = 1f - MathF.Exp(-2f * MathF.PI * 250f / Sr);
        float aHigh = 1f - MathF.Exp(-2f * MathF.PI * 2000f / Sr);
        float l1 = 0, l2 = 0, h1 = 0, h2 = 0;
        double low = 0, mid = 0, high = 0;
        long n = 0;
        var rpms = new List<float>();
        for (int b = 0; b < still + ramp + cruise; b++)
        {
            if (b >= still) target = MathF.Min(top, target + 2f * dtBlock);
            voice.TargetSpeed = target;
            voice.Render(buf);
            bool metered = b >= still + ramp + (int)(1f / dtBlock);   // a second to settle at the cruise
            if (metered) rpms.Add(voice.Engine.Rpm);
            foreach (float x0 in buf)
            {
                float x = x0 * voice.PascalsAtFullScale;
                l1 += aLow * (x - l1); l2 += aLow * (l1 - l2);
                h1 += aHigh * (x - h1); h2 += aHigh * (h1 - h2);
                if (!metered) continue;
                float lo = l2, hi = x - h2, md = x - lo - hi;
                low += (double)lo * lo; mid += (double)md * md; high += (double)hi * hi; n++;
            }
        }
        static float Db(double e, long n) => (float)(10.0 * Math.Log10(Math.Max(1e-30, e / n) / (20e-6 * 20e-6)));
        rpms.Sort();
        return (Db(low, n), Db(mid, n), Db(high, n), rpms[rpms.Count / 2]);
    }

    [Fact]
    public void EveryCityVehicleSoundsAsApproved()
    {
        var got = new ConcurrentDictionary<string, (float Low, float Mid, float High, float Rpm)>();
        Parallel.ForEach(CityTraffic, key => got[key] = Fingerprint(key));

        var bad = new List<string>();
        foreach (var key in CityTraffic)
        {
            var g = got[key];
            _o.WriteLine($"[\"{key}\"] = ({g.Low:F1}f, {g.Mid:F1}f, {g.High:F1}f, {g.Rpm:F0}f),");
            if (!Approved.TryGetValue(key, out var a)) { bad.Add($"{key}: not in the approved table"); continue; }
            if (MathF.Abs(g.Low - a.Low) > 0.5f || MathF.Abs(g.Mid - a.Mid) > 0.5f || MathF.Abs(g.High - a.High) > 0.5f
                || MathF.Abs(g.Rpm - a.Rpm) > a.Rpm * 0.03f)
                bad.Add($"{key}: low/mid/high {g.Low:F1}/{g.Mid:F1}/{g.High:F1} dB at {g.Rpm:F0} rpm, "
                      + $"approved {a.Low:F1}/{a.Mid:F1}/{a.High:F1} dB at {a.Rpm:F0} rpm");
        }
        Assert.True(bad.Count == 0,
            "A city vehicle no longer sounds as Cody approved it. If that was meant, it needs his ear, then this table:\n  "
            + string.Join("\n  ", bad));
    }
}
