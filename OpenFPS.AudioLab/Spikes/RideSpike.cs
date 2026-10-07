using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// The game's whole vehicle voice (<see cref="EngineVoiceState"/>) driven through the same stop-go ride
/// as `--shift-trace`, written to a WAV at a FIXED gain so variants keep their level differences.
/// Knobs change the preset in the lab only: muffler=stock|bikestock|chambered, steep= exp= baffle= absorb= mufflen= prim= coll= mid= tail= taild= pops= cruiseup= downshift= colld= wall= knock= valve=.
/// `--ride sportbike [knobs] out=FILE.wav [top=50]`.
/// </summary>
public static class RideSpike
{
    public static int Run(string[] args)
    {
        int at = Array.IndexOf(args, "--ride");
        string key = at + 1 < args.Length && !args[at + 1].Contains('=') && !args[at + 1].StartsWith("-") ? args[at + 1] : "sportbike";
        float? Knob(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=")) is { } s
            && float.TryParse(s[(name.Length + 1)..], System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : null;
        string outPath = args.FirstOrDefault(a => a.StartsWith("out="))?[4..] ?? $"ride_{key}.wav";
        float top = Knob("top") ?? 50f;

        var v = VehicleProfile.ByName(key);
        var e = v.Engine;
        var ex = e.Exhaust;
        var mf = ex.Muffler;
        string? muffler = args.FirstOrDefault(a => a.StartsWith("muffler="))?[8..];
        if (muffler == "stock") mf = MufflerSpec.Stock;
        else if (muffler == "bikestock")
            // A litre bike's own: the pre-chamber under the engine (catalyst, two short expansions)
            // and a packed can. Compact, which is the point — the volume is under the bike, not behind it.
            mf = MufflerSpec.Stock with { ChamberLengthsMetres = new[] { 0.10f, 0.14f }, ExpansionRatio = 7f };
        else if (muffler == "chambered") mf = MufflerSpec.Chambered40;
        if (Knob("exp") is { } xr) mf = mf with { ExpansionRatio = xr };
        if (Knob("baffle") is { } bf) mf = mf with { BaffleLoss = bf };
        if (Knob("shell") is { } sh) mf = mf with { ShellLevel = sh };
        if (Knob("absorb") is { } ab) mf = mf with { Absorption = ab };
        if (Knob("mufflen") is { } ml) mf = mf with { AbsorptiveLengthMetres = ml };
        ex = ex with { Muffler = mf };
        if (Knob("steep") is { } st) ex = ex with { Steepening = st };
        if (Knob("taild") is { } td) ex = ex with { TailpipeDiameterMm = td };
        if (Knob("colld") is { } cd) ex = ex with { CollectorDiameterMm = cd };
        if (Knob("jet") is { } jt) ex = ex with { JetNoiseLevel = jt };
        if (Knob("pops") is { } pp) ex = ex with { OverrunPopRate = pp };
        if (Knob("wall") is { } wl) ex = ex with { WallLossMultiplier = wl };
        if (Knob("prim") is { } pr) ex = ex with { PrimaryLengthMetres = pr };
        if (Knob("coll") is { } co) ex = ex with { CollectorPipeMetres = co };
        if (Knob("mid") is { } mi) ex = ex with { MidPipeMetres = mi };
        if (Knob("tail") is { } ta) ex = ex with { TailpipeMetres = new[] { ta } };
        var mech = e.Mechanical;
        if (Knob("knock") is { } kn) mech = mech with { CombustionKnock = kn };
        if (Knob("valve") is { } va) mech = mech with { ValvetrainLevel = va };
        v = v with { Engine = e with { Exhaust = ex, Mechanical = mech } };
        if (Knob("cruiseup") is { } cu) v = v with { Gearbox = v.Gearbox with { CruiseUpshiftRpm = cu } };
        if (Knob("downshift") is { } dn) v = v with { Gearbox = v.Gearbox with { DownshiftRpm = dn } };

        const int sr = 44100, block = 512;
        var voice = new EngineVoiceState(v, sr, 11);
        voice.Revive();
        if (Knob("front") is { } fm) voice.FrontMix = fm;
        if (Knob("tyres") is { } tm) voice.TyreMix = tm;
        if (Knob("fan") is { } fn) voice.FanMix = fn;
        if (Knob("body") is { } bd) voice.BodyMix = bd;
        float topMs = top / 3.6f;
        float accel = Knob("accel") ?? 2f;
        var legs = new (float Target, float Rate, float Hold)[]
            { (0, 0, 4), (topMs, accel, 6), (15 / 3.6f, 3f, 3), (topMs, accel, 4), (0, 3f, 3) };
        float target = 0; int leg = 0; float hold = legs[0].Hold;
        float dtBlock = block / (float)sr;
        var all = new System.Collections.Generic.List<float>();
        var rpms = new System.Collections.Generic.List<float>();
        var buf = new float[block];
        while (leg < legs.Length)
        {
            var l = legs[leg];
            if (MathF.Abs(target - l.Target) > 1e-3f)
                target += MathF.Sign(l.Target - target) * MathF.Min(MathF.Abs(l.Target - target), l.Rate * dtBlock);
            else if ((hold -= dtBlock) <= 0) { leg++; if (leg < legs.Length) hold = legs[leg].Hold; }
            voice.TargetSpeed = target;
            voice.Render(buf);
            all.AddRange(buf);
            if (target > 0.5f) rpms.Add(voice.Engine.Rpm);
        }
        // What it measures at a metre, flat out and at the steady cruise (the hold at the top speed,
        // 4 + 12.5 .. 4 + 12.5 + 6 seconds in, allowing for the ramp at 2 m/s^2).
        double Spl(int from, int to)
        {
            double sum = 0; int n = 0;
            for (int i = Math.Max(0, from); i < Math.Min(all.Count, to); i++) { sum += (double)all[i] * all[i]; n++; }
            return n == 0 ? 0 : 20 * Math.Log10(Math.Sqrt(sum / n) * voice.PascalsAtFullScale / 20e-6);
        }
        float rampS = topMs / accel;
        Console.WriteLine($"  at 1 m: cruising at {top:F0} km/h {Spl((int)((4 + rampS + 1) * sr), (int)((4 + rampS + 5) * sr)):F1} dB SPL,"
                          + $" pulling away {Spl(4 * sr, (int)((4 + rampS) * sr)):F1} dB SPL");
        rpms.Sort();
        if (rpms.Count > 0)
        {
            float p10 = rpms[rpms.Count / 10], p50 = rpms[rpms.Count / 2], p90 = rpms[rpms.Count * 9 / 10];
            float firing = v.Engine.Cylinders / (v.Engine.CycleDegrees / 360f) / 60f;
            Console.WriteLine($"  moving: rpm 10/50/90 % {p10:F0} / {p50:F0} / {p90:F0}, peak {rpms[^1]:F0};"
                              + $" firing note {p10 * firing:F0} / {p50 * firing:F0} / {p90 * firing:F0} Hz");
        }
        // Skip the first two seconds (start-up), fixed gain: full scale is the declared level.
        var samples = all.Skip(2 * sr).Select(x => x * (Knob("gain") ?? 1.5f)).ToArray();
        float peak = samples.Max(MathF.Abs);
        double rms = Math.Sqrt(samples.Select(x => (double)x * x).Average());
        Console.WriteLine($"  {v.Name}: {outPath}  peak {20 * MathF.Log10(peak):F1} dBFS  rms {20 * Math.Log10(rms):F1} dBFS"
                          + (peak > 1f ? "  CLIPPED" : ""));
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + dataBytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(dataBytes);
        foreach (float x in samples) w.Write((short)Math.Clamp(x * 32767f, -32768f, 32767f));
        return 0;
    }
}
