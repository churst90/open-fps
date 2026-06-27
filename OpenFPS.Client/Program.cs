using System;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;

namespace OpenFPS.Client;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Configure Serilog FIRST. The client never set up a logger, so every Log.* call
        // (including FMOD init failures) was silently dropped. Console + rolling file.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File("logs/client-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Audio diagnostic harness (Step 1a): isolate the renderer from the rest of the app.
        if (args.Contains("--audio-test"))
        {
            AudioDiagnostics.RunOrbitTest();
            Log.CloseAndFlush();
            return;
        }

        ApplicationConfiguration.Initialize();

        using var serviceProvider = ConfigureServices().BuildServiceProvider();
        serviceProvider.GetRequiredService<ClientRunner>().Run();
        Log.CloseAndFlush();
    }

    private static IServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAudioProvider, FmodAudioProvider>();
        services.AddSingleton<AudioEngineFacade>();
        services.AddSingleton<ClientRunner>();
        return services;
    }
}
