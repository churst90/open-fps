using System;
using System.Text;
using FMOD;

/// <summary>--dsp-order: builds a channel the way the provider does (a DSP played as the source, then
/// THREE_EQ, LOWPASS and a stand-in for the HRTF each added at TAIL) and prints the chain from index
/// 0, with the fader marked — so where HEAD and TAIL put a unit is read, not remembered.</summary>
public static class DspOrderSpike
{
    public static int Run()
    {
        Factory.System_Create(out FMOD.System sys);
        sys.setOutput(OUTPUTTYPE.NOSOUND);
        sys.init(32, INITFLAGS.NORMAL, IntPtr.Zero);
        sys.createDSPByType(DSP_TYPE.OSCILLATOR, out var src);
        sys.playDSP(src, default, true, out Channel ch);
        sys.createDSPByType(DSP_TYPE.THREE_EQ, out var eq);
        sys.createDSPByType(DSP_TYPE.LOWPASS, out var lp);
        sys.createDSPByType(DSP_TYPE.ECHO, out var hrtf);
        sys.createDSPByType(DSP_TYPE.HIGHPASS, out var cap);
        sys.createDSPByType(DSP_TYPE.CHORUS, out var mix);
        ch.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, eq);
        ch.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, lp);
        ch.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, hrtf);
        void Dump(string when)
        {
            ch.getNumDSPs(out int n);
            ch.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var fader);
            var sb = new StringBuilder($"{when}: ");
            for (int i = 0; i < n; i++)
            {
                ch.getDSP(i, out var d);
                d.getType(out DSP_TYPE t);
                sb.Append($"[{i}] {(d.handle == fader.handle ? "FADER" : d.handle == src.handle ? "SOURCE" : t.ToString())}  ");
            }
            Console.WriteLine(sb.ToString());
        }
        Dump("as the provider builds it (ECHO stands in for the HRTF)");
        ch.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, cap);
        Dump("plus HIGHPASS at HEAD");
        ch.getNumDSPs(out int count);
        ch.addDSP(count, mix);
        Dump("plus CHORUS at index numDSPs");
        Console.WriteLine("index 0 is the output end: a unit is processed after every unit with a larger index.");
        sys.release();
        return 0;
    }
}
