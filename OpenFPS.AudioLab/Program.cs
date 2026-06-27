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
