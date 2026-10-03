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
//   dotnet build OpenFPS.AudioLab --artifacts-path <somewhere off the repo volume>
//   dotnet <artifacts>/bin/OpenFPS.AudioLab/debug/OpenFPS.AudioLab.dll --<instrument> ...
//   --help lists the instruments.
//
// Not `dotnet run`: it writes obj/ and bin/ into the repo, and MSBuild hangs on the ntfs3 volume.
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

// Every instrument, one line each. Keep this in step with the handlers below: a flag that is not
// listed here is not an instrument, and goes.
string[] usage =
{
    "With no flag: a source orbiting the head through the game's engine, to check the HRTF by ear.",
    "",
    "Vehicles",
    "  --voice-levels [preset ...] [sweep] [parts]   the game's whole vehicle voice at 1 m against its declared level",
    "  --engine-levels [preset ...]                  the tailpipe alone, offline: headroom and crest factor",
    "  --engine-orders <preset> [rpm=] [thr=] [wav]  order content, band balance, centroid",
    "  --engine-trace <preset> [idle|off] [rpm= ...] rpm, manifold pressure and torque each quarter second",
    "  --engine-gallery [preset]                     every preset's orders side by side",
    "  --engine-alias [preset] [rpm=] [rates=]       whether a breakdown at the redline is the engine's or the sample rate's",
    "  --engine-solver                               the intake valve solver on one dumped state",
    "  --engine-cost [preset ...] [kmh=] [sec=]      what one live voice costs a core",
    "  --engine-jumps [preset ...] [kmh=] [sec=]     sample-to-sample jumps in the voice alone (crackle)",
    "  --engine-live [preset] [kmh=]                 the game's real-time engine path driven past you",
    "  --engine-street [preset ...] [kmh=]           cars down Concrete Row, the buildings answering",
    "  --vehicle[-live] / --vehicle-rev[-live] / --muscle-rev[-live] <preset> [knobs]",
    "                                                a drive in stems, a pass-by, or the stationary rev bench",
    "  --ride [preset] [knobs] out=FILE.wav          the vehicle voice through a stop-go ride at a fixed gain",
    "  --shift-trace [preset] [top=]                 what the game's driver does with the gearbox in town",
    "  --tap-balance                                 each machine's rear voice against its front voice",
    "  --body-ir [preset ...] [out=] [sec=]          a body's impulse response, modes and band balance",
    "  --intake-ir [preset ...] [thr=] [sec=] [out=] the intake tract alone, thumped once",
    "  --wheel-squeal [out=] [axle] [binaural]       each wheel squealing for itself, and four drives",
    "  --speedway [map] [seconds=] [voices=] [probe] the shipped race heard from its spawn point",
    "  --earshot [map=city] [at=x,z] [top=]          everything audible from a spot, ranked, under both distance laws",
    "  --car-horn / --siren [preset] [sec=]          horns and the siren on the bench, measured and written",
    "  --car-door [out=] [seed=]                     the car door opening and shutting, for tools/car_door_fit",
    "",
    "Machines, aircraft, rail",
    "  --machines [id] [export=DIR]                  what a machine is made of, as the JSON an author writes",
    "  --machine-levels [id ...] [kmh=]              how loud a machine is at a cruise, and what reaches you",
    "  --machine-pass [id] [kmh=] [side=]            a machine driving past, as one voice and as two",
    "  --yard [preset ...] [levels] [pass]           mowers and air conditioners, measured and walked past",
    "  --models [export=DIR]                         the model library's ids, or each as JSON",
    "  --aircraft / --landing / --spool [preset]     flyovers, arrivals, and an engine against its lever",
    "  --train / --crossing / --airbrake / --signals trains, a level crossing, air brakes, horns and bells",
    "",
    "People and things",
    "  --footsteps [surface ...] [shoe=] [run]       footsteps by surface, measured",
    "  --breath [effort=] [seconds=] [out=]          the breathing model laid out at its own times and levels",
    "  --bumps                                       a person walking into things",
    "  --applause [people=] [intensity=] [sec=]      a crowd on its own; compare=DIR against recordings",
    "  --door-knock [out=] [seed=] [knocks=]         knuckles on a wooden door",
    "  --door-opening [out=]                         doors opening and shutting at a metre",
    "  --knob-door [out=] [seed=] [only=] [stems=]   the physical knob door: opens and shuts, hinges worn and oiled",
    "  --beacon-tones [out=]                         each beacon three times at its real period",
    "  --gun-spec                                    synthesized shots against the NIJ recordings",
    "  --gun-fit [nij=DIR] [tag=] [wavs] [grid]      every weapon's report against its own NIJ takes",
    "  --speech-lines                                decodes every shipped voice line as the client does",
    "  --ground-voice [--ladder]                     a talker's ground reflection three ways",
    "",
    "Rooms, paths and the mixer",
    "  --clap-room [out=] [claps=]                   a clap in Marlow flat 01F through the whole mixer",
    "  --room-walk                                   the wood room walked with the megaphone on, captured",
    "  --walk [map= from= to= sprint]                the real movement and ground probe over a real map",
    "  --enclosure [map= at= walk=]                  what the room round a listener measures, and its send",
    "  --path-probe [map=city] ear=x,y,z src=x,y,z   what the occlusion worker hands the mixer",
    "  --siren-route [map= track= at=]               a car's path to a fixed listener, frame by frame",
    "  --pop-hunt [map=city] ear=x,y,z [sec= cars=]  cars driving the streets; paths that jump and come back",
    "  --room-echoes [map=city] ear= src=            the placed reflections a one-off sound gets",
    "  --shot-echoes [map=city] at=x,z               every echo a shot makes there, and what it came off",
    "  --wall-tl                                     the city's constructions' transmission loss per band",
    "  --traced-reverb / --traced-echoes             the traced reverb and per-source echoes, headless",
    "  --sa-frame                                    which way Steam Audio's traced soundfield faces",
    "  --tail-bands / --tail-iacc / --late-field     the tail per octave, its ears' coherence, each source's late energy",
    "  --tail-steady [room=stair|flat|corridor]      does the tail hold still: pulsing, steps, decay, ring, raw vs smooth",
    "  --tail-cost [t60=2]                           the late tail's cost per mixer piece: one channel against the field",
    "  --sim-reverbfield                             the simulator's reverb against enclosure across places",
    "  --scene-cost [map=city]                       what rebuilding the Steam Audio scenes costs",
    "  --pass-by [out=]                              noise driven past through the binaural effect, per block",
    "  --ambisonic                                   ambisonic encode and decode come out of the right ear",
    "  --dsp-order                                   where HEAD and TAIL put a unit in a channel's chain",
    "",
    "The mixer thread (docs/THE_MIXER_THREAD_CRASH.md)",
    "  --scene-churn / --provider-churn / --physical-churn / --reap-churn / --send-churn",
    "  --ended-channel / --foreign-disconnect / --send-drift scenario=N / --send-window",
};
if (args.Contains("--help"))
{
    foreach (string line in usage) Console.WriteLine(line);
    return;
}

// The authored machine library, copied in beside the maps and prefabs, so a spike auditions the same
// cars the game plays rather than only the built-in ones.
OpenFPS.Common.MachineRegistry.EnsureLoaded();
OpenFPS.Common.ModelLibrary.EnsureLoaded();

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

if (args.Contains("--siren-route"))
{
    int code = OpenFPS.Client.Core.AudioEngine.SteamAudio.SirenRouteSpike.Run(args);
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--speech-lines"))
{
    int code = SpeechLinesSpike.Run();
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

if (args.Contains("--sa-frame"))
{
    // --sa-frame: which way Steam Audio's traced soundfield faces. A wall to the left and a wall ahead;
    // the traced response must say left and ahead, and decode that way round the head. SA_MIRROR=0
    // shows the unflipped world it used to be given (the wall ahead answered from behind).
    int code = OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSpike.FrameCheck();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--sim-reverbfield"))
{
    int code = SimReverbFieldSpike.Run();
    Log.CloseAndFlush();
    Environment.Exit(code);
}

if (args.Contains("--engine-solver"))
{
    // The intake valve of the 1.6 at idle, as dumped: runner at 0.6 bar, cylinder at 0.18 bar.
    float mean = 0.6f * 101325f;
    float Z = 397000f, area = 8.8e-4f, pCyl = 18200f, tCyl = 386f, pipeK = 305f, gamma = 1.4f, uMean = -6e-4f, uCap = 0.107f;
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
if (args.Contains("--wheel-squeal"))
{
    // --wheel-squeal [out=DIR] [axle] [binaural]: each wheel squealing for itself, measured, then four drives
    // rendered (an ordinary stop, a hard stop, a fast turn, a wheelspin pull-away). `axle` renders
    // the same drives with the axle voices squealing from the overall demand, as before.
    string? outArg = args.FirstOrDefault(a => a.StartsWith("out="));
    OpenFPS.Client.Core.AudioEngine.Fmod.WheelSquealSpike.AxleOnly = args.Contains("axle");
    OpenFPS.Client.Core.AudioEngine.Fmod.WheelSquealSpike.Binaural = args.Contains("binaural");
    int wcode = OpenFPS.Client.Core.AudioEngine.Fmod.WheelSquealSpike.Run(outArg?[4..]);
    Log.CloseAndFlush();
    Environment.Exit(wcode);
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
if (args.Contains("--shot-echoes"))
{
    // --shot-echoes [map=city] at=x,z [shot=x,z]: every echo a shot makes there, and what it came off.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.SteamAudio.ShotEchoSpike.Run(args));
}
if (args.Contains("--ride"))
{
    // --ride [preset] [steep= absorb= mufflen= prim= coll= mid= tail= knock= valve=] out=FILE.wav:
    // the game's vehicle voice through a stop-go ride, at a fixed gain, for comparing variants by ear.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.RideSpike.Run(args));
}
if (args.Contains("--shift-trace"))
{
    // --shift-trace [preset] [top=50]: what the game's driver does with the gearbox in town.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.ShiftTraceSpike.Run(args));
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

if (args.Contains("--earshot"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.EarshotSpike.Run(args));
}

if (args.Contains("--gun-fit"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.GunFitSpike.Run(args));
}

if (args.Contains("--gun-spec"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.GunSpecSpike.Run(args));
}

if (args.Contains("--tap-balance"))
{
    Environment.Exit(OpenFPS.Client.AudioEngine.Fmod.TapBalanceSpike.Run(args));
}

if (args.Contains("--bumps"))
{
    Environment.Exit(OpenFPS.AudioLab.Spikes.BumpSpike.Run(args));
}

if (args.Contains("--ground-voice"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.SteamAudio.GroundVoiceSpike.Run(args));
}

if (args.Contains("--pass-by"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.SteamAudio.PassBySpike.Run(args));
}

if (args.Contains("--car-door"))
{
    // --car-door [out=DIR] [seed=N]: the car door model (OpenFPS.Common.CarDoor) opening and shutting,
    // one WAV each, to compare with its recording (tools/car_door_fit/cmp.py and tonal.py).
    string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?.Substring(4) ?? ".";
    int seed = int.TryParse(args.FirstOrDefault(a => a.StartsWith("seed="))?.Substring(5), out int sd) ? sd : 3;
    System.IO.Directory.CreateDirectory(dir);
    foreach (bool closing in new[] { false, true })
    {
        var pcm = OpenFPS.Common.CarDoor.Render(closing, 48000, seed);
        string path = System.IO.Path.Combine(dir, closing ? "car_door_close.wav" : "car_door_open.wav");
        using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
        {
            w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(48000); w.Write(96000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
            foreach (float v in pcm) w.Write((short)Math.Clamp(v * 0.89f * 32767f, -32768f, 32767f));
        }
        Console.WriteLine($"  wrote {path}");
    }
    Environment.Exit(0);
}

if (args.Contains("--beacon-tones"))
{
    // --beacon-tones [out=DIR]: each beacon three times at its real 1.6 s period, one WAV each.
    string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?.Substring(4) ?? ".";
    System.IO.Directory.CreateDirectory(dir);
    int rate = 44100, k = 0;
    foreach (var cat in OpenFPS.Common.Beacons.Categories)
    {
        var tone = OpenFPS.Client.Core.BeaconAids.Tone(cat, rate);
        var pcm = new float[(int)(1.6f * rate * 3)];
        for (int r = 0; r < 3; r++)
            for (int i = 0; i < tone.Length && (int)(r * 1.6f * rate) + i < pcm.Length; i++)
                pcm[(int)(r * 1.6f * rate) + i] = tone[i] * 0.5f;
        string path = System.IO.Path.Combine(dir, $"{++k} beacon {cat}.wav");
        using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
        Console.WriteLine($"  wrote {path}");
    }
    Environment.Exit(0);
}

if (args.Contains("--room-echoes"))
{
    // --room-echoes [map=city] ear=x,y,z src=x,y,z: the reflections a one-off sound is placed with, each with its box.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.SteamAudio.RoomEchoesSpike.Run(args));
}
if (args.Contains("--wall-tl"))
{
    // --wall-tl: the city's walls, floors, doors and glass, transmission loss per third octave and per mixer band.
    Environment.Exit(OpenFPS.AudioLab.Spikes.WallTlSpike.Run(args));
}
if (args.Contains("--pop-hunt"))
{
    // --pop-hunt [map=city] ear=x,y,z [sec=60] [cars=40] [extra=30]: answers that jump 15 dB and come back.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.SteamAudio.PopHuntSpike.Run(args));
}
if (args.Contains("--path-probe"))
{
    // --path-probe [map=city] ear=x,y,z src=x,y,z ...: what the occlusion worker hands the mixer.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.SteamAudio.PathProbeSpike.Run(args));
}
if (args.Contains("--scene-cost"))
{
    // --scene-cost [map=city]: what rebuilding the Steam Audio scenes costs, as a door opening does.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.SteamAudio.SceneCostSpike.Run(args));
}
if (args.Contains("--tail-bands"))
{
    // --tail-bands: the flat's traced tail per octave (T20, late energy) against Sabine and Eyring.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.TailBandsSpike.Run(args));
}
if (args.Contains("--tail-steady"))
{
    // --tail-steady [room=stair|flat|corridor] [seconds=10] [jitter=CM] [skip=4]: the tail standing still, each trace's own
    // samples against SmoothTail's averaged energy through fixed noise.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.TailSteadySpike.Run(args));
}
if (args.Contains("--tail-cost"))
{
    // --tail-cost [t60=2] [seconds=20]: the late tail's cost per mixer piece, one channel against the field.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.TailCostSpike.Run(args));
}
if (args.Contains("--tail-iacc"))
{
    // --tail-iacc: the tail's spatial rendering alone, the two ears' coherence per octave.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.TailIaccSpike.Run(args));
}
if (args.Contains("--late-field"))
{
    // --late-field [place=flat|tunnel|street]: each source's own late energy and direction, traced.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.LateFieldSpike.Run(args));
}
if (args.Contains("--nan-mix"))
{
    // --nan-mix [from= to= door= seconds=]: the walk into Marlow flat 00B through the whole mixer; what the non-finite guard caught.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.SteamAudio.NanMixSpike.Run(args));
}
if (args.Contains("--nan-walk"))
{
    // --nan-walk [map=city] [from=x,y,z] [to=x,y,z] [steps=12]: the listener's trace walked over a real map, every response checked for non-finite values.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.SteamAudio.NanWalkSpike.Run(args));
}
if (args.Contains("--early-tail"))
{
    // --early-tail [room=flat|stair|corridor]: the trace's first 120 ms against the room's image sources.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.EarlyTailSpike.Run(args));
}
if (args.Contains("--clap-room"))
{
    // --clap-room [out=path] [claps=4]: a clap in Marlow flat 01F through the whole mixer, and the room against it.
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.ClapRoomSpike.Run(args));
}
if (args.Contains("--door-knock"))
{
    // --door-knock [out=DIR] [seed=N] [knocks=N]: knuckles on a wooden door (OpenFPS.Common.DoorKnock).
    string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?.Substring(4) ?? ".";
    int seed = int.TryParse(args.FirstOrDefault(a => a.StartsWith("seed="))?.Substring(5), out int sd) ? sd : 3;
    int knocks = int.TryParse(args.FirstOrDefault(a => a.StartsWith("knocks="))?.Substring(7), out int kn) ? kn : 3;
    System.IO.Directory.CreateDirectory(dir);
    var pcm = OpenFPS.Common.DoorKnock.Render(knocks, 44100, seed);
    string path = System.IO.Path.Combine(dir, $"door_knock_{knocks}_seed{seed}.wav");
    using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
    {
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * 0.89f * 32767f, -32768f, 32767f));
    }
    Console.WriteLine($"  wrote {path}");
    Environment.Exit(0);
}

if (args.Contains("--knob-door"))
{
    // --knob-door [out=DIR] [seed=N]: the physical knob door (OpenFPS.Common.KnobDoor). One WAV per
    // render, all on one scale (KnobDoor.PascalsAtFullScale), with what each contact did.
    string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?.Substring(4) ?? ".";
    int seed = int.TryParse(args.FirstOrDefault(a => a.StartsWith("seed="))?.Substring(5), out int sd) ? sd : 1;
    System.IO.Directory.CreateDirectory(dir);
    var renders = new List<(string Name, Func<OpenFPS.Common.KnobDoor.Report, float[]> Make)>();
    OpenFPS.Common.KnobDoor.Door Make(OpenFPS.Common.KnobDoor.Construction c, float[]? wear)
        => new() { Leaf = c, HingeWear = wear, Seed = seed };
    var hollow = OpenFPS.Common.KnobDoor.Construction.HollowCore;
    var solid = OpenFPS.Common.KnobDoor.Construction.SolidWood;
    float[] oiled = { 0f, 0f, 0f }, worn = { 0.5f, 0.3f, 0.6f }, dry = { 0.95f, 0.8f, 1f };
    renders.Add(("hollow-open-oiled", r => OpenFPS.Common.KnobDoor.RenderOpen(Make(hollow, oiled), 48000, 0.9, r)));
    renders.Add(("hollow-open-worn", r => OpenFPS.Common.KnobDoor.RenderOpen(Make(hollow, worn), 48000, 0.9, r)));
    renders.Add(("hollow-open-dry", r => OpenFPS.Common.KnobDoor.RenderOpen(Make(hollow, dry), 48000, 0.9, r)));
    renders.Add(("hollow-open-dry-slow", r => OpenFPS.Common.KnobDoor.RenderOpen(Make(hollow, dry), 48000, 2.0, r)));
    foreach (var how in new[] { OpenFPS.Common.KnobDoor.Shut.Gentle, OpenFPS.Common.KnobDoor.Shut.Normal, OpenFPS.Common.KnobDoor.Shut.Slam })
        renders.Add(($"hollow-close-{how.ToString().ToLowerInvariant()}", r => OpenFPS.Common.KnobDoor.RenderClose(Make(hollow, worn), how, 48000, r)));
    renders.Add(("solid-open-worn", r => OpenFPS.Common.KnobDoor.RenderOpen(Make(solid, worn), 48000, 0.9, r)));
    renders.Add(("solid-close-normal", r => OpenFPS.Common.KnobDoor.RenderClose(Make(solid, worn), OpenFPS.Common.KnobDoor.Shut.Normal, 48000, r)));
    renders.Add(("solid-close-slam", r => OpenFPS.Common.KnobDoor.RenderClose(Make(solid, worn), OpenFPS.Common.KnobDoor.Shut.Slam, 48000, r)));
    // As the game sends them: the prefab door, its 0.9 s swing, each character.
    for (int v = 0; v < OpenFPS.Common.KnobDoor.Variants; v++)
    {
        int vv = v;
        renders.Add(($"game-open-v{v}", r => OpenFPS.Common.KnobDoor.RenderOpen(
            new OpenFPS.Common.KnobDoor.Door { HingeWear = OpenFPS.Common.KnobDoor.WearOf(vv), Seed = 1 + vv }, 48000, 0.9, r)));
        renders.Add(($"game-close-v{v}", r => OpenFPS.Common.KnobDoor.RenderGameClose(
            new OpenFPS.Common.KnobDoor.Door { HingeWear = OpenFPS.Common.KnobDoor.WearOf(vv), Seed = 1 + vv }, 48000, 0.9, 1.0, r)));
    }
    string? only = args.FirstOrDefault(a => a.StartsWith("only=", StringComparison.Ordinal))?.Substring(5);
    OpenFPS.Common.KnobDoor.StemFolder = args.FirstOrDefault(a => a.StartsWith("stems=", StringComparison.Ordinal))?.Substring(6);
    if (args.Contains("pins")) OpenFPS.Common.KnobDoor.PinTrace = new List<string>();
    // Every file on one gain, set by the loudest, so a slam and a gentle close keep their difference.
    var made = new List<(string Name, float[] Pcm)>();
    foreach (var (name, make) in renders)
    {
        if (only != null && !name.Contains(only)) continue;
        var rep = new OpenFPS.Common.KnobDoor.Report();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pcm = make(rep);
        made.Add((name, pcm));
        Console.WriteLine($"{name}  ({pcm.Length / 48000.0:F2} s, rendered in {sw.ElapsedMilliseconds} ms)");
        if (OpenFPS.Common.KnobDoor.PinTrace != null)
        {
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, name + ".pin.txt"), OpenFPS.Common.KnobDoor.PinTrace);
            OpenFPS.Common.KnobDoor.PinTrace.Clear();
        }
        Console.WriteLine(rep);
    }
    float loudest = 1e-9f;
    foreach (var (_, pcm) in made) foreach (float v in pcm) loudest = Math.Max(loudest, Math.Abs(v));
    float gain = 0.89f / loudest;
    foreach (var (name, pcm) in made)
    {
        string path = System.IO.Path.Combine(dir, name + ".wav");
        using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(48000); w.Write(96000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * gain * 32767f, -32768f, 32767f));
    }
    // Full scale in every file is this many pascals at a metre.
    double fullScalePa = OpenFPS.Common.KnobDoor.PascalsAtFullScale / gain;
    Console.WriteLine($"full scale = {fullScalePa:F1} Pa at 1 m ({20 * Math.Log10(fullScalePa / 2e-5):F1} dB SPL peak)");
    Environment.Exit(0);
}

if (args.Contains("--door-opening"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.DoorOpeningSpike.Run(args));
}

if (args.Contains("--car-horn"))
{
    Environment.Exit(OpenFPS.Client.Core.AudioEngine.Fmod.CarHornSpike.Run(args));
}

if (args.Contains("--mic"))
{
    // --mic [sec=3] [device=NAME]: records from the microphone through FMOD, the Linux head's voice
    // chat path, and says whether the device opened, how many samples came, and how loud they were.
    // Nothing is kept or sent.
    float micSec = 3f;
    string micDevice = "";
    foreach (var a in args)
    {
        if (a.StartsWith("sec=")) float.TryParse(a[4..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out micSec);
        if (a.StartsWith("device=")) micDevice = a[7..];
    }
    var micAudio = new OpenFPS.Client.AudioEngine.Core.AudioEngineFacade();
    micAudio.Initialize();
    Console.WriteLine($"Input devices: {string.Join(" | ", micAudio.InputDevices())}");
    if (!micAudio.StartRecording(micDevice, out int micRate)) { Console.WriteLine("  the microphone did not open"); Environment.Exit(1); }
    var micSamples = new List<float>();
    var micUntil = DateTime.UtcNow.AddSeconds(micSec);
    while (DateTime.UtcNow < micUntil) { micAudio.ReadRecording(micSamples); Thread.Sleep(10); }
    micAudio.StopRecording();
    double micSum = 0; foreach (float v in micSamples) micSum += v * v;
    double micRms = micSamples.Count > 0 ? Math.Sqrt(micSum / micSamples.Count) : 0;
    Console.WriteLine($"  {micRate} Hz, {micSamples.Count} samples in {micSec:F1} s ({micSamples.Count / Math.Max(0.1, micSec):F0}/s), RMS {20 * Math.Log10(Math.Max(1e-9, micRms)):F1} dBFS");
    micAudio.Dispose();
    Environment.Exit(micSamples.Count > 0 ? 0 : 1);
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

if (args.Contains("--ambisonic"))
{
    Console.WriteLine("--- Ambisonics: does a recorded soundfield rotate with the listener and decode to the right ear? ---");
    int code = OpenFPS.Client.Core.AudioEngine.SteamAudio.AmbisonicSpike.Run();
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

if (args.Contains("--scene-churn"))
{
    // --scene-churn [sec=]: engines, machines, aircraft and region reverb buses all at once, made
    // and released while the listener walks — the headless shape of a city. See RunSceneChurn.
    double secs = args.FirstOrDefault(a => a.StartsWith("sec=")) is { } sa && double.TryParse(sa[4..], out double sv) ? sv : 30.0;
    int code = ProviderOrbit.RunSceneChurn(secs);
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

AudioDiagnostics.RunOrbitTest();

Log.CloseAndFlush();
