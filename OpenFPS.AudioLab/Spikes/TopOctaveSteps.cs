namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// Steps in a stereo capture's top octaves (two first differences, about 6 kHz up), in 2 ms frames: each frame against
/// the median of the 50 ms round it, the round never taken as quieter than <see cref="SilenceDb"/> under the loudest
/// frame measured. A sound's steady top has no frame standing out of its neighbours; a step in it, a click, does.
///
/// Without the floor a sound that had ended was measured against digital silence: the patio door's run
/// (MovingEchoesSpike) showed a "click" 54 dB over its round that was a frame 200 dB under the door, its last float
/// dust against a round of zeros, and its render's own end, 87 dB under its peak, stood 51 dB over the silence after
/// it. Sixty decibels under the loudest is the depth the mixer takes for silence (FmodAudioProvider.HeadSilentVolume)
/// and the line every voice's end is held to (StopClicksSpike).
/// </summary>
public static class TopOctaveSteps
{
    /// <summary>The quietest a frame's round is taken to be, dB under the loudest frame measured.</summary>
    public const double SilenceDb = -60.0;

    /// <summary>Frames either side of a frame in its round: 12 of 2 ms, the 50 ms round it.</summary>
    public const int Round = 12;

    /// <summary>One 2 ms frame: where it starts (seconds from <c>from</c>), how far it stands over its round, and its own
    /// level under the loudest frame measured, both dB.</summary>
    public readonly record struct Frame(double Seconds, double OverRoundDb, double UnderLoudestDb);

    /// <summary>Every frame from sample <paramref name="from"/> (at least 2) to <paramref name="to"/> that has a whole round.</summary>
    public static List<Frame> Measure(float[] left, float[] right, int sampleRate, int from, int to)
    {
        from = Math.Max(2, from);
        to = Math.Min(to, Math.Min(left.Length, right.Length));
        int frame = Math.Max(1, sampleRate / 500);
        var e = new List<double>();
        for (int i = from; i + frame <= to; i += frame)
        {
            double s = 0;
            for (int k = i; k < i + frame; k++)
            {
                double hl = left[k] - 2.0 * left[k - 1] + left[k - 2], hr = right[k] - 2.0 * right[k - 1] + right[k - 2];
                s += hl * hl + hr * hr;
            }
            e.Add(s / frame + 1e-30);
        }
        var frames = new List<Frame>();
        if (e.Count == 0) return frames;
        double loudest = e.Max();
        double floor = loudest * Math.Pow(10.0, SilenceDb / 10.0);
        var near = new double[2 * Round + 1];
        for (int i = Round; i < e.Count - Round; i++)
        {
            e.CopyTo(i - Round, near, 0, near.Length);
            Array.Sort(near);
            double round = Math.Max(near[Round], floor);
            frames.Add(new Frame(i * frame / (double)sampleRate, 10.0 * Math.Log10(e[i] / round), 10.0 * Math.Log10(e[i] / loudest)));
        }
        return frames;
    }
}
