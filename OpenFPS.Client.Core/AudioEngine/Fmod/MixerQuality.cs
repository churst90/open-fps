using System.Runtime.InteropServices;
using FMOD;
using Serilog;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Settings of the mixer itself that decide how clean every voice is.
/// </summary>
/// <remarks>
/// The resampler: FMOD resamples every channel whose rate is not the mixer's, and every voice with a
/// pitch other than one, which is every voice with Doppler. Its default, linear interpolation, is down
/// 6 dB at 15 kHz halfway between two samples and flat on one, so a pitch slightly off one modulates the
/// top octave of every noisy sound hundreds of times a second (measured with the lab's --quality
/// resampler; docs/AUDIO_QUALITY_2026-10-06.md). OPENFPS_RESAMPLER=linear|cubic|spline|none overrides,
/// for an A/B by ear.
/// </remarks>
public static class MixerQuality
{
    /// <summary>Read when a mixer is made, so the lab can make one of each in a single run.</summary>
    public static DSP_RESAMPLER Resampler => Parse(Environment.GetEnvironmentVariable("OPENFPS_RESAMPLER"));

    /// <summary>Captures (OPENFPS_AUDIO_CAPTURE, _PRE) in 32-bit float, unless OPENFPS_AUDIO_CAPTURE_FLOAT=0
    /// asks for 16-bit PCM. Float by default since 2026-10-07: the 16-bit capture truncated every sample
    /// with no dither, so a quiet render (an airliner at 60 m, -47 dBFS) was sent for listening with
    /// truncation distortion and sixteen-bit steps the game itself never makes.</summary>
    public static bool CaptureFloat => Environment.GetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT") != "0";

    public const DSP_RESAMPLER Default = DSP_RESAMPLER.SPLINE;

    internal static DSP_RESAMPLER Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "linear" => DSP_RESAMPLER.LINEAR,
        "cubic" => DSP_RESAMPLER.CUBIC,
        "spline" => DSP_RESAMPLER.SPLINE,
        "none" or "nointerp" => DSP_RESAMPLER.NOINTERP,
        _ => Default,
    };

    /// <summary>
    /// The rate the synthesised sources render at by default (<see cref="Core.RenderRate.Default"/>, 48 kHz).
    /// OPENFPS_MIXER_RATE (22050-192000) asks for another, for an A/B.
    /// </summary>
    public const int DefaultRate = Core.RenderRate.Default;

    /// <summary>What FmodAudioProvider asks FMOD for (setSoftwareFormat). See <see cref="DefaultRate"/>.</summary>
    public static int RequestedRate
        => int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_MIXER_RATE"), out int r) && r >= 22050 && r <= 192000 ? r : DefaultRate;

    /// <summary>The mixer's rate once one is made, <see cref="DefaultRate"/> until then. Everything that
    /// renders for the mixer or runs in it reads this: nothing may assume a rate.</summary>
    public static int MixerRate { get => _mixerRate; set => _mixerRate = value > 0 ? value : DefaultRate; }
    private static volatile int _mixerRate = DefaultRate;

    /// <summary>
    /// Brings a buffer to <paramref name="to"/> with a Kaiser-windowed sinc (64 taps a side, about 90 dB
    /// down in the stop band, flat to 95 % of the lower Nyquist), once, on the thread that rendered it.
    /// FMOD's resampler, even its spline, images the top octave: 48 to 44.1 kHz brought a 15 kHz
    /// component back at 11.1 kHz only 24-28 dB under itself (the lab's --quality resampler).
    /// </summary>
    public static float[] Resample(float[] x, int from, int to) => Core.SincResampler.Resample(x, from, to);

    /// <summary>
    /// OPENFPS_FMOD_OUTPUT=pulse|alsa|wasapi: which output FMOD drives, for measuring what reaches the
    /// sound server (the lab's --quality output). Unset, FMOD chooses.
    /// </summary>
    public static void ApplyOutput(FMOD.System system)
    {
        OUTPUTTYPE? type = Environment.GetEnvironmentVariable("OPENFPS_FMOD_OUTPUT")?.Trim().ToLowerInvariant() switch
        {
            "pulse" or "pulseaudio" => OUTPUTTYPE.PULSEAUDIO,
            "alsa" => OUTPUTTYPE.ALSA,
            "wasapi" => OUTPUTTYPE.WASAPI,
            _ => null,
        };
        if (type == null) return;
        RESULT r = system.setOutput(type.Value);
        if (r == RESULT.OK) Log.Information("FMOD output: {Type} (OPENFPS_FMOD_OUTPUT).", type);
        else Log.Warning("FMOD output {Type} refused: {Result}.", type, r);
    }

    /// <summary>Before System.init: FMOD reads it when the mixer is made.</summary>
    public static void ApplyResampler(FMOD.System system)
    {
        DSP_RESAMPLER method = Resampler;
        var adv = new ADVANCEDSETTINGS { cbSize = Marshal.SizeOf<ADVANCEDSETTINGS>() };
        RESULT r = system.getAdvancedSettings(ref adv);
        if (r == RESULT.OK)
        {
            adv.cbSize = Marshal.SizeOf<ADVANCEDSETTINGS>();
            adv.resamplerMethod = method;
            r = system.setAdvancedSettings(ref adv);
        }
        if (r == RESULT.OK) Log.Information("FMOD resampler: {Method}.", method);
        else Log.Warning("FMOD resampler could not be set to {Method}: {Result}; FMOD's default (linear) stands.", method, r);
    }
}
