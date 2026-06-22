using System;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;

namespace OpenFPS.Client;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var serviceProvider = ConfigureServices().BuildServiceProvider();
        serviceProvider.GetRequiredService<ClientRunner>().Run();
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
