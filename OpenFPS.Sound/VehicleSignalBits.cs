namespace OpenFPS.Common;

/// <summary>
/// A vehicle's horn and siren as one byte on the wire (EntityState.Signals). Bit 0 the horn is held, bit 1 the siren is switched on, bits 2-4 its tone (a <see cref="SirenMode"/>),
/// bit 7 the switches are a driver's. A vehicle somebody can drive always carries bit 7, so its siren
/// follows its switch whether or not anybody is in it now; traffic never does, and its siren is read
/// off what it is doing (<see cref="SirenController"/>).
/// </summary>
public static class VehicleSignalBits
{
    public const byte Horn = 1, SirenOn = 2, Manual = 128;
    private const int ModeShift = 2;
    private const byte ModeMask = 7 << ModeShift;

    public static byte Encode(bool manual, bool horn, bool sirenOn, SirenMode tone)
    {
        int b = (manual ? Manual : 0) | (horn ? Horn : 0) | (sirenOn ? SirenOn : 0);
        b |= ((int)tone << ModeShift) & ModeMask;
        return (byte)b;
    }

    public static bool IsManual(byte b) => (b & Manual) != 0;
    public static bool HornHeld(byte b) => (b & Horn) != 0;

    /// <summary>What the siren is playing: its tone while it is switched on, else Off.</summary>
    public static SirenMode Siren(byte b)
        => (b & SirenOn) == 0 ? SirenMode.Off : (SirenMode)((b & ModeMask) >> ModeShift);
}
