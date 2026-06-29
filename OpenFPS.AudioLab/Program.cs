using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Client.Core.Platform;
using Serilog;

// Cross-platform audio test runner. Compiles the platform-neutral OpenFPS audio engine
// (FMOD + acoustics) into a plain net10.0 console app so it runs on Linux as well as Windows.
//
//   dotnet run --project OpenFPS.AudioLab
//
// Requires FMOD's native library next to the binary:
//   Linux:   libfmod.so   (drop into repo-root lib/)
//   Windows: fmod.dll      (already in repo-root lib/)

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();

Console.WriteLine("=== OpenFPS AudioLab ===");
Console.WriteLine($"Runtime: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
Console.WriteLine();

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

if (args.Contains("--provider-orbit") || args.Contains("--provider-orbit-smoke"))
{
    int code = ProviderOrbit.Run(args.Contains("--provider-orbit"), seconds: 3.0);
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
