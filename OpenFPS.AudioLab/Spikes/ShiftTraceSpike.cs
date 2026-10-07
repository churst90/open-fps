using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// What the game's driver does with a gearbox in town: a preset driven by <see cref="VirtualDriver"/>
/// through a stop-go speed profile, printing gear, rpm, clutch and throttle five times a second.
/// `--shift-trace sportbike [top=50]`.
/// </summary>
public static class ShiftTraceSpike
{
    public static int Run(string[] args)
    {
        int at = Array.IndexOf(args, "--shift-trace");
        string key = at + 1 < args.Length && !args[at + 1].StartsWith("-") ? args[at + 1] : "sportbike";
        float every_s = float.TryParse(args.FirstOrDefault(a => a.StartsWith("every="))?[6..], out var ev) ? ev : 0.2f;
        float top = float.TryParse(args.FirstOrDefault(a => a.StartsWith("top="))?[4..], out var t) ? t : 50f;
        var v = VehicleProfile.ByName(key);
        const int sr = 44100;
        var engine = new EngineSynth(v.Engine, sr, 11);
        var dl = new Driveline(v);
        var driver = new VirtualDriver(dl, engine);
        engine.SpinTo(v.Engine.IdleRpm);
        float dt = 1f / sr;
        // Stand 6 s (past the start flare), pull away at 2 m/s^2 to the top speed, hold 6 s, brake at 3 m/s^2 to 15 km/h,
        // hold 3 s, back up to the top, then stop.
        float topMs = top / 3.6f;
        var legs = new (float Target, float Rate, float Hold)[]
            { (0, 0, 6), (topMs, 2f, 6), (15 / 3.6f, 3f, 3), (topMs, 2f, 4), (0, 3f, 3) };
        float target = 0; int leg = 0; float hold = legs[0].Hold; float time = 0;
        Console.WriteLine($"  {v.Name}: top {top} km/h");
        Console.WriteLine("    t(s)  want  km/h  gear   rpm  clutch throttle  torque  brake");
        int every = (int)(sr * every_s); int i = 0;
        while (leg < legs.Length)
        {
            var l = legs[leg];
            if (MathF.Abs(target - l.Target) > 1e-3f)
                target += MathF.Sign(l.Target - target) * MathF.Min(MathF.Abs(l.Target - target), l.Rate * dt);
            else if ((hold -= dt) <= 0) { leg++; if (leg < legs.Length) hold = legs[leg].Hold; }
            driver.TargetSpeed = target;
            driver.Apply(dt);
            dl.Step(engine, dt);
            time += dt;
            if (i++ % every == 0)
                Console.WriteLine($"  {time,6:F1} {target * 3.6f,5:F0} {dl.Speed * 3.6f,5:F0}  {(dl.Gear == 0 ? "N" : dl.Gear.ToString()),4} {engine.Rpm,5:F0}   {dl.Clutch,4:F2}   {engine.Throttle,4:F2}  {engine.Torque,6:F1}  {dl.Brake,4:F2} {(dl.Locked ? "L" : "s")} gr{dl.GearRpm(dl.Gear),5:F0}");
        }
        return 0;
    }
}
