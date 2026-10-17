using System.Threading;

namespace OpenFPS.Sound.Core;

/// <summary>
/// One audio world's air now: its temperature, from the weather its host gives it, and the speed of sound in
/// it, the one figure the world's Doppler, echo delays, ground reflection and flight times read (echoes once
/// ran at 343 while Doppler followed the weather). The speed is the air's own law
/// (<see cref="AudioPhysics.SpeedOfSoundAt"/>, <see cref="OpenFPS.Materials.Air"/>), never a second one.
///
/// Who holds it: the mixer (FmodAudioProvider.Atmosphere), and the host's audio system reads the same one
/// through AudioEngineFacade. A world whose weather has not spoken yet is in the air the heard laws are
/// written in, <see cref="OpenFPS.Materials.Air.Reference"/>: 20 °C and 343 m/s.
/// </summary>
public sealed class Atmosphere
{
    /// <summary>The speed of sound in this world's air now, m/s.</summary>
    public float SpeedOfSound => Volatile.Read(ref _speed);
    private float _speed = AudioPhysics.SpeedOfSound;

    /// <summary>This world's air temperature now, °C: what a vehicle's cooling and an air conditioner's
    /// compressor run against.</summary>
    public float TemperatureC => Volatile.Read(ref _celsius);
    private float _celsius = OpenFPS.Materials.Air.Reference.TemperatureC;

    /// <summary>The weather's temperature, and the speed of sound with it. The mixer's thread reads the two;
    /// a reader between the writes hears the new speed a block before the new temperature, as before.</summary>
    public void SetTemperature(float celsius)
    {
        Volatile.Write(ref _speed, AudioPhysics.SpeedOfSoundAt(celsius));
        Volatile.Write(ref _celsius, celsius);
    }
}
