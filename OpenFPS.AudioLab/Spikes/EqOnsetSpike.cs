using System.Runtime.InteropServices;
using FMOD;

/// <summary>
/// --eq-onset [out=FILE]: what FMOD's THREE_EQ does at a voice's start and when its gains change, the unit
/// every voice's path is applied through. White noise from an oscillator, the EQ at the voice's TAIL as the
/// provider adds it, rendered non-real-time to a 32-bit float file (left channel) with the block index of
/// each event printed. Cases, a second apart: a fresh unit set shut (-80 dB) before the channel is
/// unpaused; a unit that played open on one channel, detached, reset, set shut and put on a new channel;
/// a playing voice shut mid-play; and a shut unit opened mid-play.
/// </summary>
public static class EqOnsetSpike
{
    private const int Rate = 48000, Block = 1024;
    private static readonly List<float> Captured = new();
    private static readonly DSP_READ_CALLBACK TapCallback = Tap;

    private static unsafe RESULT Tap(ref DSP_STATE state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        float* i = (float*)inbuffer, o = (float*)outbuffer;
        for (int k = 0; k < length * inchannels; k++) o[k] = i[k];
        lock (Captured) for (int k = 0; k < length; k++) Captured.Add(i[k * inchannels]);
        return RESULT.OK;
    }

    public static int Run(string[] args)
    {
        string path = args.FirstOrDefault(a => a.StartsWith("out="))?[4..] ?? "/tmp/openfps-eq-onset.wav";
        Factory.System_Create(out FMOD.System sys);
        sys.setOutput(OUTPUTTYPE.NOSOUND_NRT);
        sys.setSoftwareFormat(Rate, SPEAKERMODE.STEREO, 0);
        sys.setDSPBufferSize(Block, 4);
        sys.init(32, INITFLAGS.NORMAL, IntPtr.Zero);
        sys.getMasterChannelGroup(out ChannelGroup master);
        var tapDesc = new DSP_DESCRIPTION { pluginsdkversion = VERSION.number, numinputbuffers = 1, numoutputbuffers = 1, read = TapCallback };
        sys.createDSP(ref tapDesc, out DSP tap);
        master.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, tap);

        int blocks = 0;
        void Mix(int n) { for (int k = 0; k < n; k++) { sys.update(); blocks++; } }
        DSP Eq()
        {
            sys.createDSPByType(DSP_TYPE.THREE_EQ, out var eq);
            eq.setParameterFloat((int)DSP_THREE_EQ.LOWCROSSOVER, 400f);
            eq.setParameterFloat((int)DSP_THREE_EQ.HIGHCROSSOVER, 4000f);
            return eq;
        }
        void Gains(DSP eq, float db) { for (int b = 0; b < 3; b++) eq.setParameterFloat(b, db); }
        Channel Voice(DSP eq)
        {
            sys.createDSPByType(DSP_TYPE.OSCILLATOR, out var osc);
            osc.setParameterInt(0, 5);   // white noise
            sys.playDSP(osc, default, true, out Channel ch);
            ch.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, eq);
            return ch;
        }
        void Event(string what) => Console.WriteLine($"  block {blocks,4} ({blocks * Block / (double)Rate:F3} s): {what}");

        Mix(10);
        var a = Eq(); Gains(a, -80f);
        var ch1 = Voice(a);
        Event("fresh unit set to -80 dB, voice unpaused");
        ch1.setPaused(false);
        Mix(20);
        ch1.removeDSP(a); ch1.stop();
        Mix(30);

        var b = Eq(); Gains(b, 0f);
        var ch2 = Voice(b); ch2.setPaused(false);
        Event("a unit at 0 dB plays a voice");
        Mix(20);
        ch2.removeDSP(b); ch2.stop();
        b.reset(); Gains(b, -80f);
        var ch3 = Voice(b);
        Event("the same unit, detached, reset, set to -80 dB, on a new voice");
        ch3.setPaused(false);
        Mix(20);
        ch3.removeDSP(b); ch3.stop();
        Mix(30);

        var c = Eq(); Gains(c, 0f);
        var ch4 = Voice(c); ch4.setPaused(false);
        Event("a voice at 0 dB");
        Mix(20);
        Event("its gains set to -80 dB while it plays");
        Gains(c, -80f);
        Mix(20);
        Event("and back to 0 dB");
        Gains(c, 0f);
        Mix(20);
        ch4.stop();
        Mix(10);
        sys.release();

        float[] data; lock (Captured) data = Captured.ToArray();
        using (var f = new BinaryWriter(File.Create(path)))
        {
            f.Write("RIFF"u8); f.Write(36 + data.Length * 4); f.Write("WAVEfmt "u8); f.Write(16); f.Write((short)3); f.Write((short)1);
            f.Write(Rate); f.Write(Rate * 4); f.Write((short)4); f.Write((short)32); f.Write("data"u8); f.Write(data.Length * 4);
            foreach (float v in data) f.Write(v);
        }
        Console.WriteLine($"Wrote {path}: {data.Length} samples, {Block} per block");
        return 0;
    }
}
