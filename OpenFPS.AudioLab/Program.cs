using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Client.Core.Platform;
using Serilog;

// FMOD'S OWN LOGGING, FIRST THING. It does nothing whatsoever once System::create has run, so it
// cannot live down among the mode handlers — the first attempt put it beside --reap-churn and every
// mode above it silently got FMOD's default TTY logging instead.
{
    string? armed = OpenFPS.Client.Core.AudioEngine.Fmod.FmodDebugLog.ArmFromEnvironment();
    if (armed != null) Console.Error.WriteLine(armed);
}


// Cross-platform audio test runner. Compiles the platform-neutral OpenFPS audio engine
// (FMOD + acoustics) into a plain net10.0 console app so it runs on Linux as well as Windows.
//
//   dotnet run --project OpenFPS.AudioLab
//
// Requires FMOD's native library next to the binary:
//   Linux:   libfmod.so   (drop into repo-root lib/)
//   Windows: fmod.dll      (already in repo-root lib/)

// A FILE as well as the console, and not as an afterthought. Every diagnosis in this project has
// come from lining a log up against a capture, and scrollback in a terminal is not a log — least of
// all for somebody reading it with a screen reader. OPENFPS_LAB_LOG moves it.
string labLog = Environment.GetEnvironmentVariable("OPENFPS_LAB_LOG") ?? "/tmp/openfps-lab.log";
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File(labLog, rollingInterval: RollingInterval.Infinite, shared: true)
    .CreateLogger();

Console.WriteLine("=== OpenFPS AudioLab ===");
Console.WriteLine($"Runtime: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
Console.WriteLine();

// The authored machine library, copied in beside the maps and prefabs, so a spike auditions the same
// cars the game plays rather than only the built-in ones.
OpenFPS.Common.MachineRegistry.EnsureLoaded();
OpenFPS.Common.ModelLibrary.EnsureLoaded();

if (args.Contains("--login-test"))
{
    var net = new OpenFPS.Client.Core.ClientNetworkService();
    bool done = false, success = false; string detail = "";
    net.OnConnected += () =>
    {
        Console.WriteLine("connected; sending login (admin)");
        net.Send(new OpenFPS.Common.Networking.LoginRequest { Username = "admin", Password = "admin123" });
    };
    net.OnMessageReceived += m =>
    {
        if (m is OpenFPS.Common.Networking.LoginResponse lr)
        { success = lr.Success; detail = $"user={lr.Username} role={lr.Role} msg='{lr.Message}'"; done = true; }
    };
    net.Start();
    net.Connect("127.0.0.1", 33288);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (!done && sw.Elapsed.TotalSeconds < 6) { net.Poll(); Thread.Sleep(15); }
    Console.WriteLine(done
        ? (success ? $"RESULT: PASSED — login OK ({detail})" : $"RESULT: login rejected ({detail})")
        : "RESULT: TIMEOUT — no response (is the server running on 33288?)");
    Log.CloseAndFlush();
    Environment.Exit(success ? 0 : 1);
}

if (args.Contains("--models"))
{
    string? exportTo = args.FirstOrDefault(a => a.StartsWith("export=", StringComparison.Ordinal))?["export=".Length..];
    if (exportTo != null)
    {
        int n = OpenFPS.Common.ModelLibrary.Export(exportTo);
        Console.WriteLine($"  wrote {n} models to {exportTo}");
        Console.WriteLine("  NOT into models/ — an authored file overrides the built-in, so a copy of");
        Console.WriteLine("  the library there would freeze every model at today's numbers for good.");
    }
    else
    {
        foreach (string kind in OpenFPS.Common.ModelLibrary.AllKinds)
            Console.WriteLine($"  {kind,-13} {string.Join(", ", OpenFPS.Common.ModelLibrary.Ids(kind))}");
        Console.WriteLine("\n  --models export=DIR writes every one as the JSON the loader reads.");
    }
    Log.CloseAndFlush();
    Environment.Exit(0);
}

if (args.Contains("--airbrake"))
{
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.CrossingSpike.RunAirBrake(args);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--crossing"))
{
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.CrossingSpike.RunCrossing(args);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--train"))
{
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.TrainSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--signals"))
{
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.SignalSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--steam-distance"))
{
    int code = SteamAudioLiveTest.RunDistanceCheck();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-occlusion"))
{
    int code = SimOcclusionSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-pathing"))
{
    int code = SimPathingSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--dsp-order"))
{
    int code = DspOrderSpike.Run();
    Environment.Exit(code);
}

if (args.Contains("--traced-echoes"))
{
    int code = OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedEchoesSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--traced-reverb"))
{
    int code = OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-scene"))
{
    int code = SimSceneSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-perframe"))
{
    int code = SimPerFrameSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-worldscene"))
{
    int code = SimWorldSceneSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-pathdir"))
{
    int code = SimPathDirSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-pathframe"))
{
    int code = SimPathFrameSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-reflect"))
{
    int code = SimReflectSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-reverbfield"))
{
    int code = SimReverbFieldSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-roomdbg"))
{
    int code = SimRoomDbgSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--ear-test"))
{
    int code = EarTest.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--make-siren"))
{
    // Emit a realistic police-siren wail WAV. Optional path after the flag; defaults to the client asset.
    int idx = Array.IndexOf(args, "--make-siren");
    string outPath = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
        ? args[idx + 1]
        : System.IO.Path.Combine("OpenFPS.Client", "ASSETS", "SOUNDS", "BEACONS", "siren.wav");
    OpenFPS.Client.Core.AudioEngine.Tools.PoliceSirenGenerator.WriteWav(outPath);
    Console.WriteLine($"Wrote police-siren wail to {outPath} ({new System.IO.FileInfo(outPath).Length} bytes).");
    Log.CloseAndFlush();
    return;
}

if (args.Contains("--steam-stereo"))
{
    int code = SteamAudioLiveTest.RunStereoCheck();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--dry-check"))
{
    foreach (var prof in new[]{ OpenFPS.Client.AudioEngine.Core.WeaponProfile.Rifle,
                                OpenFPS.Client.AudioEngine.Core.WeaponProfile.Pistol,
                                OpenFPS.Client.AudioEngine.Core.WeaponProfile.Shotgun })
    {
        var pcm = OpenFPS.Client.AudioEngine.Core.WeaponSynth.MuzzleBlast(prof);
        double mean = 0; foreach (var v in pcm) mean += v; mean /= pcm.Length;
        float tail = 0; for (int i = pcm.Length - pcm.Length/10; i < pcm.Length; i++) tail = Math.Max(tail, Math.Abs(pcm[i]));
        Console.WriteLine($"  {prof.Name,-8} len={pcm.Length} mean={mean:F6} tailPeak={tail:F4} last={pcm[^1]:F6}");
    }
    Log.CloseAndFlush();
    Environment.Exit(0);
}

if (args.Contains("--engine-solver"))
{
    // The intake valve of the 1.6 at idle, as dumped: runner at 0.6 bar, cylinder at 0.18 bar.
    float mean = 0.6f * 101325f;
    float Z = 397000f, area = 8.8e-4f, pCyl = 18200f, tCyl = 386f, pipeK = 305f, gamma = 1.4f, uMean = -6e-4f, uCap = 0.107f;
    for (float bb = 40000f; bb >= -50000f; bb -= 10000f)
    {
        float g = OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.ValveResidual(0f, bb, Z, area, pCyl, tCyl, pipeK, gamma, uMean, uCap, mean, out float m);
        Console.WriteLine($"  b={bb,8:F0}  pPort={(mean + bb) / 1e5f:F3} bar  G={g,9:F5}  mdot={m * 1e3f,7:F1} g/s");
    }
    float b = OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.SolveValve(0f, 1300f, Z, area, pCyl, tCyl, pipeK, gamma, 61.8e-6f, 1f / 44100f, uMean, uCap, mean, out float md);
    Console.WriteLine($"  solver: b={b:F0}  pPort={(mean + b) / 1e5f:F3} bar  mdot={md * 1e3f:F1} g/s");
    Log.CloseAndFlush();
    Environment.Exit(0);
}
if (args.Contains("--engine-street"))
{
    // --engine-street [preset ...] [kmh=50,100]: cars driving past you down Concrete Row, with the
    // buildings answering. Default: the big block, then the truck.
    var keys = args.Where(a => OpenFPS.Common.VehicleProfile.Presets.ContainsKey(a)).ToArray();
    if (keys.Length == 0) keys = new[] { "v8_muscle", "diesel_truck" };
    string? kmhArg = args.FirstOrDefault(a => a.StartsWith("kmh="));
    float[]? kmhs = kmhArg == null ? null : Array.ConvertAll(kmhArg[4..].Split(','), float.Parse);
    int scode = OpenFPS.Client.Core.AudioEngine.Fmod.VehicleSpike.RunStreet(keys, kmhs);
    Log.CloseAndFlush();
    Environment.Exit(scode);
}
if (args.Contains("--engine-live"))
{
    // --engine-live [preset] [kmh=30,60,90]: the game's real-time engine path, driven past you.
    string presetKey = args.FirstOrDefault(a => OpenFPS.Common.VehicleProfile.Presets.ContainsKey(a)) ?? "v8_muscle";
    string? kmh = args.FirstOrDefault(a => a.StartsWith("kmh="));
    float[]? speeds = kmh == null ? null : Array.ConvertAll(kmh[4..].Split(','), float.Parse);
    int lcode = OpenFPS.Client.Core.AudioEngine.Fmod.VehicleSpike.RunLive(presetKey, speeds);
    Log.CloseAndFlush();
    Environment.Exit(lcode);
}
if (args.Contains("--tyres"))
{
    // --tyres [preset ...]: a standing start with wheelspin and chirping upshifts, a lock-up under
    // braking, and a corner tightened until the tyres let go. One curve, three demands.
    var keys = args.Where(a => OpenFPS.Common.VehicleProfile.Presets.ContainsKey(a)).ToArray();
    int tcode = OpenFPS.Client.Core.AudioEngine.Fmod.GripSpike.RunTyres(keys);
    Log.CloseAndFlush();
    Environment.Exit(tcode);
}
if (args.Contains("--turbo"))
{
    // --turbo [preset ...]: the same 2.0 four with and without a turbocharger, then the truck.
    var keys = args.Where(a => OpenFPS.Common.VehicleProfile.Presets.ContainsKey(a)).ToArray();
    int bcode = OpenFPS.Client.Core.AudioEngine.Fmod.GripSpike.RunTurbo(keys);
    Log.CloseAndFlush();
    Environment.Exit(bcode);
}
if (args.Contains("--speedway"))
{
    // --speedway [map] [seconds=] [voices=]: the shipped map, its cars on its track, its walls
    // answering them, heard from its own spawn point.
    int wcode = OpenFPS.Client.Core.AudioEngine.Fmod.SpeedwaySpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(wcode);
}
if (args.Contains("--engine-jumps"))
{
    int jcode = OpenFPS.Client.Core.AudioEngine.Fmod.EngineCostSpike.Jumps(args);
    Log.CloseAndFlush();
    Environment.Exit(jcode);
}
if (args.Contains("--applause"))
{
    // --applause [people=] [intensity=] [sec=] [out=DIR]: a crowd, on its own.
    int acode = OpenFPS.Client.Core.AudioEngine.Fmod.ApplauseSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(acode);
}
if (args.Contains("--body-ir"))
{
    // --body-ir [preset ...] [out=DIR] [sec=..]: the CAR, with no engine in it — its impulse
    // response rendered, written to a WAV and measured.
    int bcode = OpenFPS.Client.Core.AudioEngine.Fmod.BodyIrSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(bcode);
}
if (args.Contains("--intake-ir"))
{
    // --intake-ir [preset ...] [thr=..] [sec=..] [out=DIR]: the INTAKE TRACT alone, thumped once —
    // what note the airbox and its snorkel make, independently of anything driving them.
    int iicode = OpenFPS.Client.Core.AudioEngine.Fmod.IntakeIrSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(iicode);
}
if (args.Contains("--footsteps"))
{
    // --footsteps [surface ...] [shoe=..] [run] [kg=..] [steps=..] [table]
    int fscode = OpenFPS.AudioLab.Spikes.FootstepSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(fscode);
}
if (args.Contains("--machine-levels"))
{
    // --machine-levels [id ...] [kmh=..]: how loud a machine is at a cruise, and what reaches a listener.
    int mlcode = OpenFPS.AudioLab.Spikes.MachineSpike.Levels(args);
    Log.CloseAndFlush();
    Environment.Exit(mlcode);
}
if (args.Contains("--machine-pass"))
{
    // --machine-pass [id] [kmh=..] [side=..] [one] [two]: a machine drives past, once as one voice
    // and once as two, so the rig can be judged by ear rather than by argument.
    int mpcode = OpenFPS.AudioLab.Spikes.MachineSpike.Pass(args);
    Log.CloseAndFlush();
    Environment.Exit(mpcode);
}
if (args.Contains("--machines"))
{
    // --machines [id] [export=DIR]: what a machine is made of, and the JSON an author would write.
    int mcode = OpenFPS.AudioLab.Spikes.MachineSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(mcode);
}
if (args.Contains("--voice-levels"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.EngineCostSpike.VoiceLevels(args));
}

if (args.Contains("--engine-levels"))
{
    int lvcode = OpenFPS.Client.Core.AudioEngine.Fmod.EngineCostSpike.Levels(args);
    Log.CloseAndFlush();
    Environment.Exit(lvcode);
}
if (args.Contains("--engine-cost"))
{
    // --engine-cost [preset ...] [kmh=..] [sec=..]: what one live voice costs a core, so a grid
    // of cars can be sized before it is authored.
    int ccode = OpenFPS.Client.Core.AudioEngine.Fmod.EngineCostSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(ccode);
}
if (args.Contains("--engine-trace"))
{
    int tcode = OpenFPS.Client.Core.AudioEngine.Fmod.EngineOrderSpike.Trace(args);
    Log.CloseAndFlush();
    Environment.Exit(tcode);
}
if (args.Contains("--engine-gallery"))
{
    int gcode = OpenFPS.Client.Core.AudioEngine.Fmod.EngineOrderSpike.Gallery(args);
    Log.CloseAndFlush();
    Environment.Exit(gcode);
}
if (args.Contains("--engine-alias"))
{
    // --engine-alias [preset] [rpm=..] [rates=..]: is the redline the engine's or the sample rate's?
    int eacode = OpenFPS.Client.Core.AudioEngine.Fmod.EngineOrderSpike.Alias(args);
    Log.CloseAndFlush();
    Environment.Exit(eacode);
}
if (args.Contains("--breath"))
{
    // --breath [effort= seconds= out=]: the real Breathing model rendered through the real
    // TransientSynth, laid out at its own times and levels. See BreathSpike.
    int brCode = OpenFPS.Client.Core.AudioEngine.Fmod.BreathSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(brCode);
}

if (args.Contains("--walk"))
{
    // --walk [map= from= to= seconds= stand= sprint]: the REAL movement engine and ground probe over a
    // real map, reporting every footfall, every landing and every time the ground moved. See WalkSpike.
    int walkCode = OpenFPS.Client.Core.AudioEngine.Fmod.WalkSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(walkCode);
}

if (args.Contains("--enclosure"))
{
    // --enclosure [map= at= walk= step= head= dist=]: what the room round a listener measures at a
    // place on a real map, and the reverb send that follows from it. See EnclosureSpike.
    int encCode = OpenFPS.Client.Core.AudioEngine.Fmod.EnclosureSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(encCode);
}

if (args.Contains("--yard"))
{
    // --yard [preset ...] [levels] [pass] [sec= dist=]: the machinery that stands in a garden and
    // runs — mowers and air conditioners — measured, scripted and walked past.
    int yardCode = OpenFPS.Client.Core.AudioEngine.Fmod.YardSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(yardCode);
}

if (args.Contains("--bellcheck"))
{
    OpenFPS.Common.AcousticRegistry.Initialize();
    foreach (var id in new[] { "crossing_gong", "loco_bell", "tram_gong" })
    {
        try
        {
            var b = OpenFPS.Common.ModelLibrary.Bell(id);
            Console.WriteLine($"  ModelLibrary.Bell(\"{id}\") -> {b.Name}, {b.ReferenceDb:F0} dB, {b.DiameterMetres:F2} m");
        }
        catch (Exception ex) { Console.WriteLine($"  ModelLibrary.Bell(\"{id}\") THREW: {ex.GetType().Name}: {ex.Message}"); }
    }
    Console.WriteLine($"  ModelLibrary.Knows(Bell, crossing_gong) = {OpenFPS.Common.ModelLibrary.Knows(OpenFPS.Common.ModelLibrary.Kinds.Bell, "crossing_gong")}");
    Console.WriteLine($"  ids: {string.Join(", ", OpenFPS.Common.ModelLibrary.Ids(OpenFPS.Common.ModelLibrary.Kinds.Bell))}");
    Environment.Exit(0);
}

if (args.Contains("--earshot"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.EarshotSpike.Run(args));
}

if (args.Contains("--gun-spec"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.GunSpecSpike.Run(args));
}

if (args.Contains("--tap-balance"))
{
    Environment.Exit(OpenFPS.Client.AudioEngine.Fmod.TapBalanceSpike.Run(args));
}

if (args.Contains("--pass-by"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.SteamAudio.PassBySpike.Run(args));
}

if (args.Contains("--door-opening"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.DoorOpeningSpike.Run(args));
}

if (args.Contains("--car-horn"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.CarHornSpike.Run(args));
}

if (args.Contains("--siren"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.SirenSpike.Run(args));
}

if (args.Contains("--landing"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.AircraftSpike.Landing(args));
}

if (args.Contains("--spool"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.AircraftSpike.Spool(args));
}

if (args.Contains("--aircraft"))
{
    // --aircraft [preset ...] [alt= speed= offset= sec= lever= descend=]: aircraft flying past a
    // listener on the ground, one WAV each. See docs/AIRCRAFT.md.
    int aircode = OpenFPS.Client.Core.AudioEngine.Fmod.AircraftSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(aircode);
}
if (args.Contains("--engine-orders"))
{
    int orderCode = OpenFPS.Client.Core.AudioEngine.Fmod.EngineOrderSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(orderCode);
}

if (args.Contains("--vehicle-cams") || args.Contains("--vehicle-cams-live"))
{
    Console.WriteLine("--- The same V8 with three camshafts ---");
    int camCode = OpenFPS.Client.Core.AudioEngine.Fmod.VehicleSpike.RunCamComparison(
        args.Contains("--vehicle-cams-live"));
    Log.CloseAndFlush();
    Environment.Exit(camCode);
}

if (args.Contains("--vehicle") || args.Contains("--vehicle-live")
    || args.Contains("--vehicle-rev") || args.Contains("--vehicle-rev-live")
    || args.Contains("--muscle-rev") || args.Contains("--muscle-rev-live"))
{
    Console.WriteLine("--- V8 sports car with Flowmaster 40s, synthesized from its mechanism ---");
    bool muscle = args.Contains("--muscle-rev") || args.Contains("--muscle-rev-live");
    string? preset = args.FirstOrDefault(a => !a.StartsWith("--") && OpenFPS.Common.VehicleProfile.Presets.ContainsKey(a));
    bool stationary = muscle || args.Contains("--vehicle-rev") || args.Contains("--vehicle-rev-live");
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.VehicleSpike.Run(
        args.Contains("--vehicle-live") || args.Contains("--vehicle-rev-live")
        || args.Contains("--muscle-rev-live"), stationary, muscle, preset,
        withBody: !args.Contains("body=off"),
        coupling: args.FirstOrDefault(a => a.StartsWith("coupling=")) is { } cp
                  && float.TryParse(cp[9..], out float cv) ? cv : null,
        withShell: !args.Contains("shell=off"),
        shellCase: args.FirstOrDefault(a => a.StartsWith("case=")) is { } sc ? sc[5..] : null,
        shellLoss: args.FirstOrDefault(a => a.StartsWith("ring=")) is { } rg
                   && float.TryParse(rg[5..], out float rv) ? rv : null,
        shellWiden: args.FirstOrDefault(a => a.StartsWith("wide=")) is { } wd
                    && float.TryParse(wd[5..], out float wv) ? wv : null,
        shellLevel: args.FirstOrDefault(a => a.StartsWith("shell=") && a != "shell=off") is { } sl
                    && float.TryParse(sl[6..], out float slv) ? slv : null,
        knobs: args);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--blast-compare") || args.Contains("--blast-compare-live"))
{
    Console.WriteLine("--- Blast comparison: recording vs recording+sub vs synthesis ---");
    int i = Array.FindIndex(args, a => a.StartsWith("--blast-compare"));
    string? only = (i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : null;
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.BlastCompareSpike.Run(
        args.Contains("--blast-compare-live"), only);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--blast-probe"))
{
    // Writes each weapon's blast three ways — synthesis only, recording only, and the composite — so
    // the three can be compared spectrally. Which layer is carrying which part of the sound is not
    // something to decide by argument.
    string outDir = System.IO.Path.Combine(AppContext.BaseDirectory, "blast-probe");
    System.IO.Directory.CreateDirectory(outDir);
    string? assets = null;
    var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
    {
        string c = System.IO.Path.Combine(d.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS");
        if (System.IO.Directory.Exists(c)) { assets = c; break; }
    }
    foreach (var w in OpenFPS.Common.WeaponRegistry.All)
    {
        var prof = OpenFPS.Client.AudioEngine.Core.WeaponProfile.From(w);
        var synth = OpenFPS.Client.AudioEngine.Core.WeaponSynth.MuzzleBlast(prof);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, $"{w.Id}_synth.wav"),
            OpenFPS.Client.AudioEngine.Core.WeaponSynth.ToWav16(synth));
        if (assets != null)
        {
            string dir = System.IO.Path.Combine(assets, w.FiringFolder.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.Directory.Exists(dir))
            {
                var files = System.IO.Directory.GetFiles(dir, "*.wav");
                if (files.Length > 0)
                {
                    var rec = OpenFPS.Client.AudioEngine.Core.WeaponSynth.ReadWav16Mono(System.IO.File.ReadAllBytes(files[0]));
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, $"{w.Id}_composite.wav"),
                        OpenFPS.Client.AudioEngine.Core.WeaponSynth.ToWav16(
                            OpenFPS.Client.AudioEngine.Core.WeaponSynth.CompositeBlast(prof, rec)));
                }
            }
        }
    }
    Console.WriteLine($"wrote {outDir}");
    Log.CloseAndFlush();
    Environment.Exit(0);
}

if (args.Contains("--battle") || args.Contains("--battle-live"))
{
    Console.WriteLine("--- Concrete Row: a firefight in a street with sides ---");
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.BattleSpike.Run(args.Contains("--battle-live"));
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--street") || args.Contains("--street-live"))
{
    Console.WriteLine("--- A street: two doors, a window shot out, a truck, and a wall ---");
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.StreetSceneSpike.Run(args.Contains("--street-live"));
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--gunshot") || args.Contains("--gunshot-live"))
{
    Console.WriteLine("--- Weapons: a dry synthesized shot, and a crack-to-report gap that encodes range ---");
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.GunshotSpike.Run(args.Contains("--gunshot-live"), "");
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--bed") || args.Contains("--bed-live"))
{
    int bi = Array.IndexOf(args, args.Contains("--bed-live") ? "--bed-live" : "--bed");
    string bedId = (bi >= 0 && bi + 1 < args.Length && !args[bi + 1].StartsWith("--"))
        ? args[bi + 1] : "AMBIENCE/woods_mid_day";
    Console.WriteLine("--- Ambient bed: does a real recorded soundfield play and turn with the listener? ---");
    int code = OpenFPS.Client.Core.AudioEngine.SteamAudio.AmbientBedSpike.Run(bedId, args.Contains("--bed-live"));
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--ambisonic"))
{
    Console.WriteLine("--- Ambisonics: does a recorded soundfield rotate with the listener and decode to the right ear? ---");
    int code = OpenFPS.Client.Core.AudioEngine.SteamAudio.AmbisonicSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--boundary") || args.Contains("--boundary-live"))
{
    Console.WriteLine("--- Near-field boundaries: does a nearby surface colour the mix, and does the colour track it? ---");
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.BoundarySpike.Run(args.Contains("--boundary-live"));
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--open-air-reverb"))
{
    Console.WriteLine("--- Open air: does naming a place put a roof over it? ---");
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.OpenAirReverbSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--tailcheck"))
{
    Console.WriteLine("--- Tail check: one decay forced onto the unit, one footstep, and the decay of what came OUT ---");
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.TailCheckSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--room-walk"))
{
    Console.WriteLine("--- Room walk: the wood room with the megaphone on, walked, the mix captured to a WAV ---");
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.RoomWalkSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--reverb-route"))
{
    Console.WriteLine("--- Reverb routing: is a room's reverb gated by, and heard through, its doorway? ---");
    int code = OpenFPS.Client.Core.AudioEngine.Fmod.ReverbRouteSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--provider-orbit") || args.Contains("--provider-orbit-smoke"))
{
    int code = ProviderOrbit.Run(args.Contains("--provider-orbit"), seconds: 3.0);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--scene-churn"))
{
    // --scene-churn [sec=]: engines, machines, aircraft and region reverb buses all at once, made
    // and released while the listener walks — the headless shape of a city. See RunSceneChurn.
    double secs = args.FirstOrDefault(a => a.StartsWith("sec=")) is { } sa && double.TryParse(sa[4..], out double sv) ? sv : 30.0;
    int code = ProviderOrbit.RunSceneChurn(secs);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--reverb-churn"))
{
    // --reverb-churn [n]: ask FMOD for n region reverb buses and report what it does when it will
    // not give out another. See ProviderOrbit.RunReverbChurn.
    int n = args.FirstOrDefault(a => a.StartsWith("n=")) is { } na && int.TryParse(na[2..], out int nv) ? nv : 400;
    int code = ProviderOrbit.RunReverbChurn(n);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--physical-churn"))
{
    // --physical-churn: every machine and aircraft preset, created, moved and released against a
    // live mixer. The headless reproduction of "the client dies a few seconds after an aircraft
    // starts". See ProviderOrbit.RunPhysicalChurn.
    int code = ProviderOrbit.RunPhysicalChurn(seconds: 12.0);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--ended-channel"))
{
    // --ended-channel: asks FMOD directly whether a DSP stays attached to a Channel that ended on
    // its own. The answer decides whether pooling those DSPs is safe at all.
    int code = ProviderOrbit.RunEndedChannelProbe();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--send-churn"))
{
    // --send-churn [sec=] [noflip] [ownroom]: one-shots WITH reverb sends, ending on their own, while the
    // listener's region flips every update — the one path no other harness had (see ProviderOrbit).
    double sec = 30.0;
    foreach (var a in args) if (a.StartsWith("sec=")) sec = double.Parse(a[4..]);
    int code = ProviderOrbit.RunSendChurn(sec, flip: !args.Contains("noflip"), ownRoom: args.Contains("ownroom"));
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--foreign-disconnect"))
{
    // --foreign-disconnect: one send disconnected through the WRONG reverb unit, and what FMOD's
    // input counts do afterwards. The mechanism of the city crash, isolated.
    int code = ProviderOrbit.RunForeignDisconnectProbe();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--send-drift"))
{
    // --send-drift scenario=N: tears a sending channel down one way, then trips the wire.
    int sc = 1; foreach (var a in args) if (a.StartsWith("scenario=")) sc = int.Parse(a[9..]);
    int code = ProviderOrbit.RunSendDriftProbe(sc);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--send-window"))
{
    // --send-window [sec=] [mode=client|forget|stop]: FMOD alone, forcing the window between a
    // queued send disconnect and the channel finishing.
    double sec = 20.0; string mode = "client";
    foreach (var a in args) { if (a.StartsWith("sec=")) sec = double.Parse(a[4..]); if (a.StartsWith("mode=")) mode = a[5..]; }
    int code = ProviderOrbit.RunSendWindowProbe(sec, mode);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--reap-churn"))
{
    // --reap-churn [sec=]: one-shots that END ON THEIR OWN and are collected by the reaper. The only
    // harness that exercises a STALE channel handle — every other one stops its voices itself, which
    // is the safe path, and is why none of them reproduced the city crash.
    double sec = 20.0;
    foreach (var a in args) if (a.StartsWith("sec=")) sec = double.Parse(a[4..]);
    int code = ProviderOrbit.RunReapChurn(sec);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--provider-churn"))
{
    int code = ProviderOrbit.RunChurn(seconds: 12.0);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--steam-live") || args.Contains("--steam-live-smoke"))
{
    bool interactive = args.Contains("--steam-live");
    int code = SteamAudioLiveTest.Run(interactive, seconds: 2.5);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--steam-test"))
{
    string wav = Path.Combine(Directory.GetCurrentDirectory(), "steam_orbit.wav");
    Console.WriteLine("Rendering Steam Audio HRTF orbit (this is offline, takes a moment)...");
    int code = SteamAudioSpike.RenderOrbitWav(wav);
    if (code == 0)
    {
        Console.WriteLine($"\nWrote: {wav}");
        Console.WriteLine("Play it on HEADPHONES, e.g.:  paplay steam_orbit.wav   (or mpv/aplay)");
        Console.WriteLine("First 8s = horizontal circle; last 8s = VERTICAL circle (front/up/back/down).");
        Console.WriteLine("If you now hear ABOVE vs BELOW, Steam Audio HRTF is working.");
    }
    Log.CloseAndFlush();
    return;
}

if (args.Contains("--speech-test"))
{
    Console.WriteLine("Linux speech test via speech-dispatcher...");
    using var speech = new SpeechDispatcherOutput();
    if (speech.Initialize())
    {
        Console.WriteLine($"Backend: {speech.BackendName}");
        speech.Speak("Open F P S. Linux speech output is working.");
        Thread.Sleep(3000);
        Console.WriteLine("RESULT: PASSED — speech-dispatcher connected and spoke.");
    }
    else
    {
        Console.WriteLine("RESULT: speech backend unavailable (is the speech-dispatcher daemon running?).");
    }
    Log.CloseAndFlush();
    return;
}

if (args.Contains("--smoke"))
{
    int code = AudioDiagnostics.RunSmokeTest(seconds: 3);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

AudioDiagnostics.RunOrbitTest();

Log.CloseAndFlush();
