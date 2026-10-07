using System.Numerics;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// What the traced reverb and the traced echoes the game is using right now give the two ears: the
/// listener's trace (TracedReverbSet.Listener) and one source's echoes (TracedReverbSet.Echoes), each
/// IR played once for an impulse through Steam Audio's reflection effect and decoded binaurally through
/// the default HRTF, facing north, as the game decodes them. Said as the level against the impulse, the
/// left against the right, the share of the energy in the first-order (directional) channels, and the
/// two ears' coherence (IACC, the largest normalised cross-correlation within a millisecond) over the
/// reverberant part, broadband and in three octaves. A wall field round the head reads well under one
/// above 500 Hz; an empty scene has no level at all, and a field with no direction in it reads near one.
///
/// For the path probe's door swings (--path-probe ... traced): a scene that traced as empty after a
/// swap took the walls out of both, and Cody heard the reflections go mono (2026-10-06).
/// </summary>
internal static class TracedBinaural
{
    private static int _nudge;
    /// <summary>Traces to wait for after the listener moved (the echoes, two banks, twice as many).</summary>
    private const int RunsToWait = 3;

    /// <summary>Both traces measured where they stand now, after they have run on the scene in use.
    /// <paramref name="ask"/> asks the worker something with the ear where it is given: the worker moves
    /// the listener's trace to the ear of what it is asked.</summary>
    public static void Report(Vector3 ear, Vector3 source, Action<Vector3> ask, string indent = "      ")
    {
        // Steam Audio keeps averaging a trace's energy while the listener and the source stand still
        // and the scene's version number is unchanged, and two different scenes can carry the same
        // number (every whole scene is version 1): moved by a millimetre, both traces start again on the
        // scene they now have, as they do in the game, where the listener moves. Twice, waiting out a
        // few traces each time: on the whole-scene build the first traces after a swap sometimes still
        // gave the room as it was (seen 2026-10-06, a second later they did not).
        var until = DateTime.UtcNow.AddSeconds(30);
        while (TracedReverbSet.Reconfiguring && DateTime.UtcNow < until) Thread.Sleep(20);
        var listener = TracedReverbSet.Listener;
        var echoes = TracedReverbSet.Echoes;
        if (listener == null) { Console.WriteLine($"{indent}traced reverb: none (no listener trace)"); return; }
        listener.EnsureReader(1);
        int slot = -1;
        for (int pass = 0; pass < 2; pass++)
        {
            float d = 0.001f * (++_nudge % 2 == 0 ? 1f : -1f);
            var at = ear + new Vector3(d, 0f, 0f);
            var src = source + new Vector3(0f, 0f, d);
            ask(at);
            TracedReverbSet.SetListener(at);
            if (echoes != null)
            {
                slot = _slot >= 0 ? _slot : (_slot = echoes.Acquire(src));
                echoes.SetSource(slot, src);
                echoes.SetListener(at);
            }
            int lr = listener.Runs, er = echoes?.Runs ?? 0;
            while (DateTime.UtcNow < until && (listener.Runs < lr + RunsToWait || (echoes != null && echoes.Runs < er + 2 * RunsToWait))) { ask(at); Thread.Sleep(20); }
        }

        if (listener.TryGetParams(1, out var p))
            Console.WriteLine($"{indent}traced reverb at the ear   " + Describe(listener.Context, p, TracedReverb.Order, listener.SampleRate, listener.FrameSize, listener.IrSize));
        else Console.WriteLine($"{indent}traced reverb at the ear   no IR");
        if (echoes != null && slot >= 0 && echoes.NewestBank >= 0 && echoes.TryGetParams(slot, echoes.NewestBank, out var q))
            Console.WriteLine($"{indent}traced echoes of the source " + Describe(echoes.Context, q, TracedEchoes.Order, echoes.SampleRate, echoes.FrameSize, echoes.IrSize));
        else Console.WriteLine($"{indent}traced echoes of the source no IR");
    }

    private static int _slot = -1;

    private static string Describe(IntPtr ctx, Phonon.IPLReflectionEffectParams p, int order, int sr, int frame, int irSize)
    {
        int ch = (order + 1) * (order + 1);
        var au = new Phonon.IPLAudioSettings { samplingRate = sr, frameSize = frame };
        var es = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = irSize, numChannels = ch };
        if (Phonon.iplReflectionEffectCreate(ctx, ref au, ref es, out IntPtr effect) != Phonon.IPL_STATUS_SUCCESS) return "no effect";
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        var dsx = new Phonon.IPLAmbisonicsDecodeEffectSettings { speakerLayout = Phonon.StereoLayout(), hrtf = hrtf, maxOrder = order };
        Phonon.iplAmbisonicsDecodeEffectCreate(ctx, ref au, ref dsx, out IntPtr dec);
        var dp = new Phonon.IPLAmbisonicsDecodeEffectParams { order = order, hrtf = hrtf, orientation = Phonon.ListenerFrame(Quaternion.Identity), binaural = Phonon.IPL_TRUE };
        var inBuf = new Phonon.IPLAudioBuffer(); var ambi = new Phonon.IPLAudioBuffer(); var st = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(ctx, 1, frame, ref inBuf);
        Phonon.iplAudioBufferAllocate(ctx, ch, frame, ref ambi);
        Phonon.iplAudioBufferAllocate(ctx, 2, frame, ref st);
        var mono = new float[frame]; var inter = new float[frame * ch]; var stI = new float[frame * 2];
        int blocks = (int)Math.Ceiling(1.0 * sr / frame);
        var L = new double[blocks * frame]; var R = new double[blocks * frame];
        double eOmni = 0, eDir = 0;
        // Warmed on silence first: a new effect crossfades its first block in from an empty response.
        for (int b = -4; b < blocks; b++)
        {
            Array.Clear(mono);
            if (b == 0) mono[0] = 1f;
            Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
            Phonon.iplReflectionEffectApply(effect, ref p, ref inBuf, ref ambi, IntPtr.Zero);
            Phonon.iplAmbisonicsDecodeEffectApply(dec, ref dp, ref ambi, ref st);
            if (b < 0) continue;
            Phonon.iplAudioBufferInterleave(ctx, ref ambi, inter);
            Phonon.iplAudioBufferInterleave(ctx, ref st, stI);
            for (int k = 0; k < frame; k++)
            {
                L[b * frame + k] = stI[k * 2]; R[b * frame + k] = stI[k * 2 + 1];
                eOmni += (double)inter[k * ch] * inter[k * ch];
                for (int c = 1; c < Math.Min(4, ch); c++) eDir += (double)inter[k * ch + c] * inter[k * ch + c];
            }
        }
        Phonon.iplAudioBufferFree(ctx, ref inBuf); Phonon.iplAudioBufferFree(ctx, ref ambi); Phonon.iplAudioBufferFree(ctx, ref st);
        Phonon.iplAmbisonicsDecodeEffectRelease(ref dec); Phonon.iplHRTFRelease(ref hrtf); Phonon.iplReflectionEffectRelease(ref effect);

        double el = L.Sum(v => v * v), er = R.Sum(v => v * v);
        if (el + er < 1e-12) return "silent: nothing traced back";
        // The reverberant part: 20 to 300 ms after the impulse.
        int a = sr / 50, z = Math.Min(L.Length, sr * 3 / 10);
        string Iacc(double lo, double hi)
        {
            var l = lo > 0 ? Band(L, lo, hi, sr) : L; var r = lo > 0 ? Band(R, lo, hi, sr) : R;
            return IaccOf(l[a..z], r[a..z], sr).ToString("F2");
        }
        return $"level {10 * Math.Log10((el + er) / 2):F1} dB, L/R {10 * Math.Log10(el / Math.Max(er, 1e-30)):+0.0;-0.0} dB, "
             + $"directional share {eDir / Math.Max(eOmni, 1e-30):F2}, IACC 20-300 ms {Iacc(0, 0)} (500 Hz {Iacc(354, 707)}, 1 kHz {Iacc(707, 1414)}, 2 kHz {Iacc(1414, 2828)})";
    }

    /// <summary>The largest normalised cross-correlation within ±1 ms.</summary>
    private static double IaccOf(double[] a, double[] b, int sr)
    {
        double ea = 0, eb = 0; for (int i = 0; i < a.Length; i++) { ea += a[i] * a[i]; eb += b[i] * b[i]; }
        double norm = Math.Sqrt(ea * eb) + 1e-30, best = -1;
        int max = sr / 1000;
        for (int lag = -max; lag <= max; lag++)
        {
            double s = 0;
            for (int i = Math.Max(0, -lag); i < a.Length && i + lag < b.Length; i++) s += a[i] * b[i + lag];
            best = Math.Max(best, s / norm);
        }
        return best;
    }

    /// <summary>A band-pass (two passes of a second-order section, RBJ) from <paramref name="lo"/> to <paramref name="hi"/> Hz.</summary>
    private static double[] Band(double[] x, double lo, double hi, int sr)
    {
        double f0 = Math.Sqrt(lo * hi), bw = Math.Log2(hi / lo);
        double w0 = 2 * Math.PI * f0 / sr, alpha = Math.Sin(w0) * Math.Sinh(Math.Log(2) / 2 * bw * w0 / Math.Sin(w0));
        double b0 = alpha, b2 = -alpha, a0 = 1 + alpha, a1 = -2 * Math.Cos(w0), a2 = 1 - alpha;
        var y = (double[])x.Clone();
        for (int pass = 0; pass < 2; pass++)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double xi = y[i], yi = (b0 * xi + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1; x1 = xi; y2 = y1; y1 = yi; y[i] = yi;
            }
        }
        return y;
    }
}
