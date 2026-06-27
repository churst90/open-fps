using System;
using System.Runtime.InteropServices;
using Thread = System.Threading.Thread;
using FMOD;
using Serilog;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Real-time spike: plays a mono looping source through FMOD with the Steam Audio binaural DSP
/// attached, and orbits the direction live (horizontal circle, then vertical). Proves the
/// real-time DSP path — the offline WAV proved the HRTF; this proves it works streaming through
/// FMOD's mixer.
///
///   interactive=true  -> live, runs until Q is pressed (headphone test).
///   interactive=false -> headless smoke: runs `seconds`, verifies the DSP callback fired and
///                        produced audio, returns 0/1. No ears needed.
/// </summary>
public static class SteamAudioLiveTest
{
    public static int Run(bool interactive, double seconds = 2.0)
    {
        if (Factory.System_Create(out FMOD.System system) != RESULT.OK) { Console.WriteLine("System_Create failed"); return 1; }
        system.setSoftwareFormat(44100, SPEAKERMODE.STEREO, 0);
        if (system.init(64, INITFLAGS.NORMAL, IntPtr.Zero) != RESULT.OK) { Console.WriteLine("FMOD init failed"); return 1; }
        system.getDSPBufferSize(out uint blockSize, out int _);
        int frameSize = (int)blockSize;

        // Steam Audio context + HRTF (shared) and a per-voice binaural effect.
        var ctxS = new Phonon.IPLContextSettings { version = Phonon.STEAMAUDIO_VERSION, simdLevel = Phonon.IPL_SIMDLEVEL_AVX2, flags = 0 };
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("iplContextCreate failed"); return 1; }
        var audio = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = frameSize };
        var hrtfS = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        if (Phonon.iplHRTFCreate(ctx, ref audio, ref hrtfS, out IntPtr hrtf) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("iplHRTFCreate failed"); return 1; }
        var effS = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        if (Phonon.iplBinauralEffectCreate(ctx, ref audio, ref effS, out IntPtr effect) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("iplBinauralEffectCreate failed"); return 1; }

        var state = new SteamAudioVoiceState
        {
            Context = ctx, Hrtf = hrtf, Effect = effect, FrameSize = frameSize,
            MonoScratch = new float[frameSize], StereoScratch = new float[frameSize * 2]
        };
        Phonon.iplAudioBufferAllocate(ctx, 1, frameSize, ref state.InBuf);
        Phonon.iplAudioBufferAllocate(ctx, 2, frameSize, ref state.OutBuf);

        // Mono looping broadband source.
        byte[] pcm = GenerateMonoNoise(44100);
        var info = new CREATESOUNDEXINFO
        {
            cbsize = Marshal.SizeOf<CREATESOUNDEXINFO>(),
            length = (uint)pcm.Length, numchannels = 1, defaultfrequency = 44100, format = SOUND_FORMAT.PCM16
        };
        system.createSound(pcm, MODE.OPENMEMORY | MODE.OPENRAW | MODE.LOOP_NORMAL | MODE._2D, ref info, out FMOD.Sound sound);
        system.playSound(sound, default, true, out FMOD.Channel channel);

        SteamAudioDsp.CreateDSP(system, state, out FMOD.DSP dsp, out GCHandle handle);
        channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, dsp);
        channel.setPaused(false);

        Log.Information("Steam Audio live DSP: frameSize={Frame}, running.", frameSize);

        bool running = true;
        if (interactive)
        {
            Console.WriteLine("LIVE Steam Audio binaural orbit. HEADPHONES on. Press Q to quit.");
            Console.WriteLine("0-8s horizontal (front->right->back->left), 8-16s VERTICAL (front->up->back->down), looping.");
            var keyThread = new Thread(() => { while (running) if (Console.ReadKey(true).Key == ConsoleKey.Q) running = false; }) { IsBackground = true };
            keyThread.Start();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (running)
        {
            double t = sw.Elapsed.TotalSeconds;
            if (!interactive && t >= seconds) break;

            double tc = t % 16.0;
            Phonon.IPLVector3 dir;
            if (tc < 8.0) { double th = 2 * Math.PI * (tc / 4.0); dir = new() { x = (float)Math.Sin(th), y = 0f, z = (float)(-Math.Cos(th)) }; }
            else { double ph = 2 * Math.PI * ((tc - 8.0) / 4.0); dir = new() { x = 0f, y = (float)Math.Sin(ph), z = (float)(-Math.Cos(ph)) }; }
            state.DirX = dir.x; state.DirY = dir.y; state.DirZ = dir.z;

            system.update();
            Thread.Sleep(20);
        }

        int result = 0;
        if (!interactive)
        {
            bool ok = state.CallbackCount > 0 && state.ProducedAudio;
            Console.WriteLine(ok
                ? $"RESULT: PASSED — DSP callback fired {state.CallbackCount}x and produced binaural audio."
                : $"RESULT: FAILED — callbacks={state.CallbackCount}, producedAudio={state.ProducedAudio}.");
            result = ok ? 0 : 1;
        }

        channel.removeDSP(dsp);
        dsp.release();
        if (handle.IsAllocated) handle.Free();
        sound.release();
        Phonon.iplAudioBufferFree(ctx, ref state.InBuf);
        Phonon.iplAudioBufferFree(ctx, ref state.OutBuf);
        Phonon.iplBinauralEffectRelease(ref effect);
        Phonon.iplHRTFRelease(ref hrtf);
        Phonon.iplContextRelease(ref ctx);
        system.close();
        system.release();
        return result;
    }

    /// <summary>
    /// Verifies that FMOD still applies DISTANCE attenuation on a 3D channel when its pan level is
    /// 0 (the mode the provider uses with Steam Audio). Measures the DSP output RMS near vs far.
    /// </summary>
    public static int RunDistanceCheck()
    {
        Factory.System_Create(out FMOD.System system);
        system.setSoftwareFormat(44100, SPEAKERMODE.STEREO, 0);
        system.init(64, INITFLAGS.NORMAL, IntPtr.Zero);
        system.getDSPBufferSize(out uint blockSize, out int _);
        int frameSize = (int)blockSize;

        var ctxS = new Phonon.IPLContextSettings { version = Phonon.STEAMAUDIO_VERSION, simdLevel = Phonon.IPL_SIMDLEVEL_AVX2, flags = 0 };
        Phonon.iplContextCreate(ref ctxS, out IntPtr ctx);
        var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = frameSize };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        var es = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        Phonon.iplBinauralEffectCreate(ctx, ref au, ref es, out IntPtr effect);

        var state = new SteamAudioVoiceState
        {
            Context = ctx, Hrtf = hrtf, Effect = effect, FrameSize = frameSize,
            MonoScratch = new float[frameSize], StereoScratch = new float[frameSize * 2],
            DirX = 0f, DirY = 0f, DirZ = -1f // straight ahead; only distance varies
        };
        Phonon.iplAudioBufferAllocate(ctx, 1, frameSize, ref state.InBuf);
        Phonon.iplAudioBufferAllocate(ctx, 2, frameSize, ref state.OutBuf);

        byte[] pcm = GenerateMonoNoise(44100);
        var info = new CREATESOUNDEXINFO { cbsize = Marshal.SizeOf<CREATESOUNDEXINFO>(), length = (uint)pcm.Length, numchannels = 1, defaultfrequency = 44100, format = SOUND_FORMAT.PCM16 };
        system.createSound(pcm, MODE.OPENMEMORY | MODE.OPENRAW | MODE.LOOP_NORMAL | MODE._3D | MODE._3D_LINEARROLLOFF, ref info, out FMOD.Sound sound);
        system.playSound(sound, default, true, out FMOD.Channel channel);
        SteamAudioDsp.CreateDSP(system, state, out FMOD.DSP dsp, out GCHandle handle);
        channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, dsp);
        channel.set3DLevel(0.0f);
        channel.set3DMinMaxDistance(3f, 100f);
        channel.setPaused(false);

        var lpos = new FMOD.VECTOR(); var lvel = new FMOD.VECTOR();
        var fwd = new FMOD.VECTOR { x = 0, y = 0, z = 1 }; var up = new FMOD.VECTOR { x = 0, y = 1, z = 0 };
        system.set3DListenerAttributes(0, ref lpos, ref lvel, ref fwd, ref up);

        // getAudibility reports the channel's final effective volume AFTER 3D rolloff + API volume
        // (the post-fader value the DSP callback cannot see).
        float Measure(float dist, bool manualRolloff)
        {
            var pos = new FMOD.VECTOR { x = 0, y = 0, z = dist };
            var vel = new FMOD.VECTOR();
            channel.set3DAttributes(ref pos, ref vel);
            float atten = manualRolloff ? Math.Clamp(1f - (dist - 3f) / MathF.Max(0.01f, 100f - 3f), 0f, 1f) : 1f;
            channel.setVolume(atten);
            for (int k = 0; k < 12; k++) { system.update(); Thread.Sleep(20); }
            channel.getAudibility(out float aud);
            return aud;
        }

        float fmodNear = Measure(3f, false), fmodFar = Measure(90f, false);
        float manNear = Measure(3f, true), manFar = Measure(90f, true);
        float fmodRatio = fmodNear > 0 ? fmodFar / fmodNear : 0;
        float manRatio = manNear > 0 ? manFar / manNear : 0;
        bool fmodAttenuates = fmodRatio < 0.5f;
        bool manualAttenuates = manNear > 0.001f && manRatio < 0.3f;
        Console.WriteLine($"FMOD native @3DLevel(0): audibility near={fmodNear:F3} far={fmodFar:F3} far/near={fmodRatio:F2}.");
        Console.WriteLine($"Manual rolloff:          audibility near={manNear:F3} far={manFar:F3} far/near={manRatio:F2}.");
        if (fmodAttenuates)
            Console.WriteLine("RESULT: FMOD already attenuates by distance at set3DLevel(0) — the manual rolloff is REDUNDANT (remove it to avoid double attenuation).");
        else if (manualAttenuates)
            Console.WriteLine("RESULT: PASSED — FMOD does NOT attenuate at set3DLevel(0); the manual rolloff provides correct distance falloff.");
        else
            Console.WriteLine("RESULT: FAILED — neither path attenuates; investigate.");
        bool ok = !fmodAttenuates && manualAttenuates;

        channel.removeDSP(dsp); dsp.release(); if (handle.IsAllocated) handle.Free(); sound.release();
        Phonon.iplAudioBufferFree(ctx, ref state.InBuf); Phonon.iplAudioBufferFree(ctx, ref state.OutBuf);
        Phonon.iplBinauralEffectRelease(ref effect); Phonon.iplHRTFRelease(ref hrtf); Phonon.iplContextRelease(ref ctx);
        system.close(); system.release();
        return ok ? 0 : 2;
    }

    /// <summary>One second of mono 16-bit broadband noise bursts (good for localization).</summary>
    private static byte[] GenerateMonoNoise(int sampleRate)
    {
        int n = sampleRate;
        var pcm = new byte[n * 2];
        int period = sampleRate / 6;
        uint seed = 2463534242;
        for (int i = 0; i < n; i++)
        {
            int pos = i % period;
            float env = pos < period * 0.30 ? 1f : 0f;
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            float noise = (int)seed / (float)int.MaxValue;
            short s = (short)(noise * env * 0.5f * short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return pcm;
    }
}
