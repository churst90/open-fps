namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>The rate a synthesised source renders at when nobody names one.</summary>
public static class RenderRate
{
    /// <summary>
    /// 48 kHz: the sound servers, almost every device, the one-shots, door renders, speech and voice chat
    /// all run at it, so nothing is resampled on the way in or out. The mixer asks for it unless told
    /// otherwise (MixerQuality.DefaultRate).
    /// </summary>
    public const int Default = 48000;
}
