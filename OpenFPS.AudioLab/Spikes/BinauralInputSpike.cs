using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --binaural-input: a mono voice through the game's binaural stage (SteamAudioDsp) in a real FMOD
/// mixer, against Steam Audio's HRTF applied to the same signal directly. Each ear must come out at the
/// level the HRTF alone gives it, which is the stage taking the voice at its own level. Prints the
/// difference per ear and direction, and exits 1 if any is more than 0.1 dB.
///
/// Native, so here and not in OpenFPS.Tests: the fault it guards was FMOD's, which upmixed a mono voice
/// to a stage that asked for a stereo input by panning it to the middle, 3.01 dB down on each side.
/// </summary>
public static class BinauralInputSpike
{
    private const int Rate = 48000, Block = 1024;
    private const float ToleranceDb = 0.1f;

    private static readonly DSP_READ_CALLBACK MasterTapCallback = MasterTap;
    private static double _tapL, _tapR;

    private static unsafe RESULT MasterTap(ref DSP_STATE state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        float* i = (float*)inbuffer, o = (float*)outbuffer;
        for (int k = 0; k < length * inchannels; k++) o[k] = i[k];
        for (int k = 0; k < length; k++)
        {
            double l = i[k * inchannels], r = inchannels > 1 ? i[k * inchannels + 1] : l;
            _tapL += l * l; _tapR += r * r;
        }
        return RESULT.OK;
    }

    public static int Run()
    {
        var rng = new Random(7);
        var signal = new float[Rate * 2];
        for (int i = 0; i < signal.Length; i++) signal[i] = (float)(rng.NextDouble() * 2 - 1) * 0.25f;

        var cs = Phonon.DefaultContextSettings();
        Phonon.iplContextCreate(ref cs, out IntPtr ctx);
        var au = new Phonon.IPLAudioSettings { samplingRate = Rate, frameSize = Block };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);

        Factory.System_Create(out FMOD.System sys);
        sys.setOutput(OUTPUTTYPE.NOSOUND_NRT);
        sys.setSoftwareFormat(Rate, SPEAKERMODE.STEREO, 0);
        sys.setDSPBufferSize(Block, 4);
        sys.init(32, INITFLAGS.NORMAL, IntPtr.Zero);
        sys.getMasterChannelGroup(out ChannelGroup master);
        var tapDesc = new DSP_DESCRIPTION { pluginsdkversion = VERSION.number, numinputbuffers = 1, numoutputbuffers = 1, read = MasterTapCallback };
        sys.createDSP(ref tapDesc, out DSP tap);
        master.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, tap);

        var bytes = new byte[signal.Length * 4];
        Buffer.BlockCopy(signal, 0, bytes, 0, bytes.Length);
        var ex = new CREATESOUNDEXINFO
        {
            cbsize = Marshal.SizeOf<CREATESOUNDEXINFO>(), length = (uint)bytes.Length, numchannels = 1,
            defaultfrequency = Rate, format = SOUND_FORMAT.PCMFLOAT,
        };
        sys.createSound(bytes, MODE.OPENMEMORY | MODE.OPENRAW | MODE.CREATESAMPLE | MODE.LOOP_OFF | MODE._2D, ref ex, out Sound sound);

        Console.WriteLine("  A mono voice through the binaural stage in FMOD, against the HRTF alone (dB, each ear):");
        bool ok = true;
        foreach (var (name, x, y, z) in new[] { ("front", 0f, 0f, -1f), ("right", 1f, 0f, 0f), ("behind", 0f, 0f, 1f), ("above", 0f, 1f, 0f) })
        {
            var (refL, refR) = Direct(ctx, hrtf, au, signal, x, y, z);
            var (stageL, stageR) = ThroughStage(sys, master, sound, ctx, hrtf, au, x, y, z);
            float dl = (float)(10 * Math.Log10(stageL / refL)), dr = (float)(10 * Math.Log10(stageR / refR));
            bool pass = MathF.Abs(dl) <= ToleranceDb && MathF.Abs(dr) <= ToleranceDb;
            ok &= pass;
            Console.WriteLine($"    {name,-7} L {dl,6:+0.00;-0.00}  R {dr,6:+0.00;-0.00}  {(pass ? "ok" : "WRONG")}");
        }
        Console.WriteLine(ok ? "  The stage takes the voice at its own level." : $"  FAIL: the stage is off by more than {ToleranceDb} dB.");

        sound.release(); tap.release(); sys.release();
        Phonon.iplHRTFRelease(ref hrtf); Phonon.iplContextRelease(ref ctx);
        return ok ? 0 : 1;
    }

    /// <summary>Energy in each ear of the signal through Steam Audio's binaural effect, block by block.</summary>
    private static (double L, double R) Direct(IntPtr ctx, IntPtr hrtf, Phonon.IPLAudioSettings au, float[] signal, float x, float y, float z)
    {
        var es = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        Phonon.iplBinauralEffectCreate(ctx, ref au, ref es, out IntPtr effect);
        var inBuf = new Phonon.IPLAudioBuffer(); var outBuf = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(ctx, 1, Block, ref inBuf);
        Phonon.iplAudioBufferAllocate(ctx, 2, Block, ref outBuf);
        var mono = new float[Block]; var stereo = new float[Block * 2];
        double l = 0, r = 0;
        // The signal and then as much silence again, for the filter's tail.
        for (int b = 0; b < 2 * signal.Length / Block; b++)
        {
            for (int i = 0; i < Block; i++) { int at = b * Block + i; mono[i] = at < signal.Length ? signal[at] : 0f; }
            Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
            var prm = new Phonon.IPLBinauralEffectParams
            {
                direction = new Phonon.IPLVector3 { x = x, y = y, z = z }, interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR,
                spatialBlend = 1f, hrtf = hrtf, peakDelays = IntPtr.Zero,
            };
            Phonon.iplBinauralEffectApply(effect, ref prm, ref inBuf, ref outBuf);
            Phonon.iplAudioBufferInterleave(ctx, ref outBuf, stereo);
            for (int i = 0; i < Block; i++) { l += stereo[2 * i] * (double)stereo[2 * i]; r += stereo[2 * i + 1] * (double)stereo[2 * i + 1]; }
        }
        Phonon.iplAudioBufferFree(ctx, ref inBuf); Phonon.iplAudioBufferFree(ctx, ref outBuf);
        Phonon.iplBinauralEffectRelease(ref effect);
        return (l, r);
    }

    /// <summary>Energy in each ear of the sound played on a 2D channel through the game's stage.</summary>
    private static (double L, double R) ThroughStage(FMOD.System sys, ChannelGroup master, Sound sound, IntPtr ctx, IntPtr hrtf,
                                                     Phonon.IPLAudioSettings au, float x, float y, float z)
    {
        var es = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        Phonon.iplBinauralEffectCreate(ctx, ref au, ref es, out IntPtr effect);
        var state = new SteamAudioVoiceState
        {
            Context = ctx, Hrtf = hrtf, Effect = effect, FrameSize = Block,
            MonoScratch = new float[Block], StereoScratch = new float[Block * 2],
            DirX = x, DirY = y, DirZ = z,
        };
        Phonon.iplAudioBufferAllocate(ctx, 1, Block, ref state.InBuf);
        Phonon.iplAudioBufferAllocate(ctx, 2, Block, ref state.OutBuf);
        SteamAudioDsp.CreateDSP(sys, state, out DSP stage, out GCHandle handle);

        _tapL = _tapR = 0;
        sys.playSound(sound, master, true, out Channel channel);
        channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, stage);
        channel.setPaused(false);
        bool playing = true;
        for (int i = 0; i < 2000 && playing; i++) { sys.update(); channel.isPlaying(out playing); }
        channel.stop();
        for (int i = 0; i < 20; i++) sys.update();
        var energy = (_tapL, _tapR);

        stage.release(); handle.Free();
        Phonon.iplAudioBufferFree(ctx, ref state.InBuf); Phonon.iplAudioBufferFree(ctx, ref state.OutBuf);
        Phonon.iplBinauralEffectRelease(ref effect);
        return energy;
    }
}
