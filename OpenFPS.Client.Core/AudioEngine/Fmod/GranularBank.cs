using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using FMOD;
using Serilog;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Responsibility: Loads audio files directly into raw PCM memory (float arrays) 
/// so the granular synthesis DSP can read them rapidly without locking FMOD's core channels.
/// </summary>
public class GranularBank : IDisposable
{
    private readonly FMOD.System _system;
    
    // Maps SoundId -> Interleaved float PCM data
    private readonly ConcurrentDictionary<string, float[]> _pcmCache = new();
    private readonly ConcurrentDictionary<string, int> _channelsCache = new();
    private readonly ConcurrentDictionary<string, int> _sampleRateCache = new();

    public GranularBank(FMOD.System system)
    {
        _system = system;
    }

    /// <summary>
    /// Gets or loads the raw PCM data for a given sound ID.
    /// </summary>
    public bool TryGetPcmData(string soundId, out float[] data, out int channels, out int sampleRate)
    {
        if (_pcmCache.TryGetValue(soundId, out data!) && 
            _channelsCache.TryGetValue(soundId, out channels) && 
            _sampleRateCache.TryGetValue(soundId, out sampleRate))
        {
            return true;
        }

        channels = 0;
        sampleRate = 0;

        string path = ResolvePath(soundId);
        if (string.IsNullOrEmpty(path)) return false;

        // Load sound as a stream to extract raw data, making sure we decode it properly.
        // We use CREATESAMPLE | OPENONLY so FMOD parses the format.
        MODE mode = MODE.CREATESAMPLE | MODE.OPENONLY | MODE.ACCURATETIME;
        RESULT res = _system.createSound(path, mode, out FMOD.Sound sound);
        if (res != RESULT.OK)
        {
            Log.Error("GranularBank: Failed to open sound {SoundId} for PCM extraction. FMOD Error: {Res}", soundId, res);
            return false;
        }

        try
        {
            sound.getFormat(out SOUND_TYPE type, out SOUND_FORMAT format, out channels, out int bits);
            sound.getDefaults(out float freq, out int priority);
            sampleRate = (int)freq;

            sound.getLength(out uint lengthBytes, TIMEUNIT.PCMBYTES);
            
            // Read data
            byte[] rawBytes = new byte[lengthBytes];
            IntPtr ptr = Marshal.AllocHGlobal((int)lengthBytes);
            
            res = sound.@lock(0, lengthBytes, out IntPtr ptr1, out IntPtr ptr2, out uint len1, out uint len2);
            if (res == RESULT.OK)
            {
                Marshal.Copy(ptr1, rawBytes, 0, (int)len1);
                if (len2 > 0)
                {
                    Marshal.Copy(ptr2, rawBytes, (int)len1, (int)len2);
                }
                sound.unlock(ptr1, ptr2, len1, len2);
            }
            else
            {
                Marshal.FreeHGlobal(ptr);
                return false;
            }
            Marshal.FreeHGlobal(ptr);

            // Convert to float array based on format
            data = ConvertToFloatArray(rawBytes, format, channels);
            
            _pcmCache[soundId] = data;
            _channelsCache[soundId] = channels;
            _sampleRateCache[soundId] = sampleRate;

            Log.Information("GranularBank: Decoded {SoundId} -> {SampleCount} samples, {Channels} ch, {Rate} Hz", 
                soundId, data.Length / channels, channels, sampleRate);

            return true;
        }
        finally
        {
            sound.release();
        }
    }

    private string ResolvePath(string soundId)
    {
        if (string.IsNullOrEmpty(soundId)) return string.Empty;
        if (soundId.Contains("ASSETS", StringComparison.OrdinalIgnoreCase)) return soundId;

        string normId = soundId.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS", "SOUNDS", normId);
        
        if (File.Exists(path)) return path;

        string[] extensions = { ".wav", ".ogg", ".mp3" };
        foreach (var ext in extensions) 
        { 
            if (File.Exists(path + ext)) return path + ext; 
        }
        return string.Empty;
    }

    private float[] ConvertToFloatArray(byte[] rawBytes, SOUND_FORMAT format, int channels)
    {
        int bytesPerSample = format switch
        {
            SOUND_FORMAT.PCM8 => 1,
            SOUND_FORMAT.PCM16 => 2,
            SOUND_FORMAT.PCM24 => 3,
            SOUND_FORMAT.PCM32 => 4,
            SOUND_FORMAT.PCMFLOAT => 4,
            _ => 2 // Default to 16-bit
        };

        int totalSamples = rawBytes.Length / bytesPerSample;
        float[] floatData = new float[totalSamples];

        if (format == SOUND_FORMAT.PCM16)
        {
            for (int i = 0; i < totalSamples; i++)
            {
                short val = BitConverter.ToInt16(rawBytes, i * 2);
                floatData[i] = val / 32768f;
            }
        }
        else if (format == SOUND_FORMAT.PCMFLOAT)
        {
            Buffer.BlockCopy(rawBytes, 0, floatData, 0, rawBytes.Length);
        }
        else if (format == SOUND_FORMAT.PCM8)
        {
            for (int i = 0; i < totalSamples; i++)
            {
                floatData[i] = (rawBytes[i] - 128) / 128f;
            }
        }
        else if (format == SOUND_FORMAT.PCM24)
        {
            for (int i = 0; i < totalSamples; i++)
            {
                int val = rawBytes[i * 3] | (rawBytes[i * 3 + 1] << 8) | ((sbyte)rawBytes[i * 3 + 2] << 16);
                floatData[i] = val / 8388608f;
            }
        }
        else if (format == SOUND_FORMAT.PCM32)
        {
            for (int i = 0; i < totalSamples; i++)
            {
                int val = BitConverter.ToInt32(rawBytes, i * 4);
                floatData[i] = val / 2147483648f;
            }
        }

        return floatData;
    }

    public void Dispose()
    {
        _pcmCache.Clear();
        _channelsCache.Clear();
        _sampleRateCache.Clear();
    }
}
