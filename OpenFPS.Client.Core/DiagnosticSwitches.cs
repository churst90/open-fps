using System;
using Serilog;

namespace OpenFPS.Client.Core;

/// <summary>
/// The OPENFPS_* environment variables the client reads for diagnosis and A/B listening. Every client
/// head logs the ones that are set at startup: a session that behaved differently because a variable
/// was still set from the last one is a day lost.
/// </summary>
public static class DiagnosticSwitches
{
    /// <summary>Every client-side switch that changes what is heard or logged.</summary>
    public static readonly string[] Names =
    {
        "OPENFPS_AUDIO_DEBUG", "OPENFPS_AUDIO_TRACE", "OPENFPS_AUDIO_CAPTURE", "OPENFPS_FMOD_WAV",
        "OPENFPS_FMOD_DEBUG", "OPENFPS_PROFILE", "OPENFPS_WEATHER",
        "OPENFPS_ENGINE_VOICES", "OPENFPS_ENGINE_ECHOES", "OPENFPS_MACHINE_VOICES", "OPENFPS_FRONT_VOICES",
        "OPENFPS_STEAMAUDIO_SIM", "OPENFPS_HRTF", "OPENFPS_LEVEL_COMPRESSION",
        "OPENFPS_MASTER_DB", "OPENFPS_MASTER_MAKEUP_DB",
        "OPENFPS_TAIL", "OPENFPS_TAIL_DB", "OPENFPS_COPIES_DB", "OPENFPS_REFLECTIONS_DB", "OPENFPS_ECHOES",
        "OPENFPS_TAIL_SDM", "OPENFPS_TAIL_PARAMETRIC", "OPENFPS_TAIL_AMBISONIC", "OPENFPS_DIFFUSE_TAIL",
    };

    /// <summary>Logs a warning for each switch that is set.</summary>
    public static void LogSet()
    {
        foreach (string key in Names)
        {
            string? val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(val)) Log.Warning("{Key}={Value} — a diagnostic switch is set.", key, val);
        }
    }
}
