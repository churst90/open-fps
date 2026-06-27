using System.Runtime.InteropServices;
using OpenFPS.Client.Core;
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

AudioDiagnostics.RunOrbitTest();

Log.CloseAndFlush();
