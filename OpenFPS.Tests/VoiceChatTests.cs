using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Tests;

/// <summary>
/// Voice chat end to end, without the network: a voice encoded as the microphone encodes it, carried by a
/// simulated connection that delays, bunches and drops packets, and read back by the reader a talker's
/// voice in the world uses (TalkerStream into OwnVoiceTap).
/// </summary>
public class VoiceChatTests
{
    private const int Rate = VoiceCodec.Rate;
    private const int Block = 480;              // the mixer's 10 ms

    private static float Tone(long n, double hz = 220) => 0.5f * MathF.Sin((float)(2 * Math.PI * hz * n / Rate));

    /// <summary>Encodes <paramref name="seconds"/> of a voice into numbered 20 ms packets.</summary>
    private static List<(ushort Seq, byte[] Data)> Encode(Func<long, float> voice, double seconds, ushort firstSeq = 1)
    {
        var enc = new VoiceFrameEncoder();
        var packets = new List<(ushort, byte[])>();
        ushort seq = firstSeq;
        var frame = new float[VoiceCodec.FrameSamples];
        long n = 0;
        for (int f = 0; f < seconds * 50; f++)
        {
            for (int i = 0; i < frame.Length; i++) frame[i] = voice(n++);
            enc.Push(frame, p => packets.Add((seq, p)));
            seq++;
            if (seq == 0) seq = 1;
        }
        return packets;
    }

    /// <summary>
    /// Plays packets through a connection: packet k is sent at <c>sendAt(k)</c> and arrives
    /// <c>delay(k)</c> later (null delay = lost). Returns what the reader played, block by block.
    /// </summary>
    private static (float[] Heard, TalkerStream Stream) Carry(List<(ushort Seq, byte[] Data)> packets,
        Func<int, double> sendAt, Func<int, double?> delay, double seconds)
    {
        var stream = new TalkerStream(7);
        var tap = new OwnVoiceTap(stream.Ring, 0f, Rate, TalkerStream.MaxPull);
        var arrivals = packets.Select((p, k) => (p, at: delay(k) is double d ? sendAt(k) + d : double.NaN))
                              .Where(a => !double.IsNaN(a.at)).OrderBy(a => a.at).ToList();
        var heard = new List<float>();
        var block = new float[Block];
        int next = 0;
        for (int b = 0; b < seconds * 100; b++)
        {
            double now = b * 0.01;
            while (next < arrivals.Count && arrivals[next].at <= now)
            {
                stream.Receive(arrivals[next].p.Seq, arrivals[next].p.Data, arrivals[next].at);
                next++;
            }
            stream.Pump(now);
            tap.Consume(block);
            heard.AddRange(block);
        }
        return (heard.ToArray(), stream);
    }

    private static float Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (float v in x) s += v * v;
        return (float)Math.Sqrt(s / Math.Max(1, x.Length));
    }

    [Fact]
    public void TheCodecCarriesTheWholeVoiceAtAModestRate()
    {
        // 180 Hz and 9 kHz together: fullband keeps the sibilant, which 24 kbit/s VOIP threw away.
        var packets = Encode(n => 0.3f * MathF.Sin(2 * MathF.PI * 180 * n / Rate) + 0.1f * MathF.Sin(2 * MathF.PI * 9000 * n / Rate), 2);
        double kbps = packets.Average(p => p.Data.Length) * 8 * 50 / 1000.0;
        Assert.InRange(kbps, 20, 80);

        var dec = VoiceCodec.CreateDecoder();
        var pcm = new float[VoiceCodec.MaxFrameSamples];
        var outp = new List<float>();
        foreach (var p in packets) outp.AddRange(pcm.AsSpan(0, dec.Decode(p.Data, pcm, VoiceCodec.MaxFrameSamples, false)).ToArray());
        // The 9 kHz part, by a second difference (a crude high-pass): present at close to its level.
        var tail = outp.Skip(Rate / 2).ToArray();
        var hp = new float[tail.Length - 2];
        for (int i = 0; i < hp.Length; i++) hp[i] = tail[i] - 2 * tail[i + 1] + tail[i + 2];
        // A 9 kHz sine of amplitude a has second difference amplitude a * (2 - 2cos w) = a * 2.6.
        float expected = 0.1f / MathF.Sqrt(2) * (2 - 2 * MathF.Cos(2 * MathF.PI * 9000 / Rate));
        Assert.InRange(Rms(hp), expected * 0.7f, expected * 1.3f);
    }

    [Fact]
    public void TheResamplerChangesRateWithoutChangingTheVoice()
    {
        var r = new SincResampler(44100, Rate);
        var input = Enumerable.Range(0, 44100).Select(n => 0.5f * MathF.Sin(2 * MathF.PI * 1000 * n / 44100f)).ToList();
        var output = new List<float>();
        for (int i = 0; i < input.Count; i += 441) r.Process(input.GetRange(i, 441), output);
        Assert.InRange(output.Count, Rate - 100, Rate + 10);
        var steady = output.Skip(1000).Take(Rate - 2000).ToArray();
        Assert.InRange(Rms(steady), 0.5f / MathF.Sqrt(2) * 0.99f, 0.5f / MathF.Sqrt(2) * 1.01f);
        // The tone's own second difference: no interpolation images above it, and it is not dulled.
        var d2 = new float[steady.Length - 2];
        for (int i = 0; i < d2.Length; i++) d2[i] = steady[i] - 2 * steady[i + 1] + steady[i + 2];
        float toneD2 = 0.5f / MathF.Sqrt(2) * (2 - 2 * MathF.Cos(2 * MathF.PI * 1000 / Rate));
        Assert.InRange(Rms(d2), toneD2 * 0.95f, toneD2 * 1.05f);
    }

    [Fact]
    public void AJitteryConnectionPlaysASteadyVoice()
    {
        // Every packet up to 70 ms late, at random, so they bunch and reorder.
        var rng = new Random(3);
        var packets = Encode(n => Tone(n), 6);
        var delays = packets.Select(_ => 0.03 + rng.NextDouble() * 0.07).ToArray();
        var (heard, stream) = Carry(packets, k => k * 0.02, k => delays[k], 6.5);

        Assert.Equal(0, stream.Lost);
        Assert.Equal(0, stream.Late);
        Assert.Equal(0, stream.RanDry);
        // From a second in to just before the end: no block falls silent, and the tone's period holds.
        var steady = heard.Skip(Rate).Take(Rate * 4).ToArray();
        for (int b = 0; b + Block <= steady.Length; b += Block)
            Assert.True(Rms(steady.AsSpan(b, Block)) > 0.3f, $"a gap at {1 + b / (double)Rate:F2} s");
        var up = Enumerable.Range(1, steady.Length - 1).Where(i => steady[i - 1] < 0 && steady[i] >= 0).ToArray();
        var periods = up.Zip(up.Skip(1), (a, b) => (b - a) / (double)Rate).ToArray();
        // On average the pitch is the one sent, to within the reader's pull.
        Assert.InRange(periods.Min(), 1 / 220.0 * 0.97, 1 / 220.0 * 1.03);
        Assert.InRange(periods.Max(), 1 / 220.0 * 0.97, 1 / 220.0 * 1.03);
        Assert.InRange(periods.Average(), 1 / 220.0 * (1 - TalkerStream.MaxPull - 0.0005), 1 / 220.0 * (1 + TalkerStream.MaxPull + 0.0005));
        // And the buffer is what the jitter needs, not the most it may be.
        Assert.InRange(stream.Ring.MarginSeconds, 0.07, 0.2);
    }

    [Trait("Category", "Timing")] // depends on this machine's speed or on real time; not run on CI
    [Fact]
    public void LostPacketsAreConcealedNotGaps()
    {
        // One packet in twenty lost, never two together.
        var packets = Encode(n => Tone(n), 6);
        var (heard, stream) = Carry(packets, k => k * 0.02, k => k % 20 == 7 ? null : 0.04, 6.5);

        Assert.InRange(stream.Lost, 14, 16);
        Assert.Equal(stream.Lost, stream.Rebuilt);
        // The buffer never ran dry while they talked: the wait for the missing frame was inside the margin.
        Assert.Equal(0, stream.RanDry);
        // Nothing fell silent: the codec's rebuild of a tone may dip for a frame, a hole reads as nothing.
        var steady = heard.Skip(Rate).Take(Rate * 4).ToArray();
        for (int b = 0; b + Block <= steady.Length; b += Block)
            Assert.True(Rms(steady.AsSpan(b, Block)) > 0.04f, $"a gap at {1 + b / (double)Rate:F2} s");
        Assert.True(Rms(steady) > 0.33f);
    }

    [Fact]
    public void TheSequenceWrapsWithoutALoss()
    {
        var packets = Encode(n => Tone(n), 1, firstSeq: 65530);
        Assert.Contains(packets, p => p.Seq == 1);
        Assert.DoesNotContain(packets, p => p.Seq == 0);
        var (_, stream) = Carry(packets, k => k * 0.02, _ => 0.03, 1.5);
        Assert.Equal(0, stream.Lost);
        Assert.Equal(0, stream.Late);
    }

    [Fact]
    public void TalkingAgainStartsFromTheFirstWord()
    {
        // Talk for a second, stop for a second, then a click as the first thing said: it must be heard,
        // not skipped by a reader that was still sitting at the end of the last sentence.
        var first = Encode(n => Tone(n), 1, firstSeq: 1);
        var second = Encode(n => n < 960 * 5 ? 0f : n < 960 * 5 + 48 ? 0.9f : 0f, 1, firstSeq: 51);
        var all = first.Concat(second).ToList();
        var (heard, stream) = Carry(all, k => k < 50 ? k * 0.02 : 2.0 + (k - 50) * 0.02, _ => 0.03, 3.5);

        // The click left the sender 100 ms into the second run, at 2.1 s; heard a margin and a bit later.
        var after = heard.Skip(2 * Rate).ToArray();
        int peak = Array.IndexOf(after, after.Max());
        double at = 2 + peak / (double)Rate;
        Assert.True(after.Max() > 0.3f, "the first word was not heard");
        Assert.InRange(at, 2.1 + 0.03, 2.1 + 0.03 + TalkerStream.MaxMarginSeconds);
    }

    [Fact]
    public void ACorruptPacketDoesNotStopTheVoice()
    {
        var packets = Encode(n => Tone(n), 2);
        packets[30] = (packets[30].Seq, new byte[] { 0xFF, 0xFF, 0xFF });
        var (heard, stream) = Carry(packets, k => k * 0.02, _ => 0.03, 2.5);
        Assert.True(Rms(heard.AsSpan(Rate + Rate / 2, Block)) > 0.2f);
    }
}
