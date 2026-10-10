namespace OpenFPS.Common.Hearing;

/// <summary>
/// The sound every level is calibrated by: a normal voice a metre away, as a recorded line holds it. The
/// loudness law, the ear model's listening level and the loudspeakers read these; open-fps's speech
/// (<c>Speech</c>) declares its own levels from them.
/// </summary>
public static class ReferenceVoice
{
    /// <summary>Talking to someone beside you: ANSI S3.5-1997, Table 3, normal vocal effort, the speech's
    /// long-term RMS one metre in front of the talker.</summary>
    public const float NormalDb = 62.35f;

    /// <summary>
    /// Where a loaded line sits on average, dBFS RMS: lines are brought to a loudness that for the shipped
    /// set is this within a decibel or so. -28 leaves room for the peakiest take (26.3 dB from RMS to
    /// peak, --speech-lines, 2026-09-27) with 1.7 dB to spare.
    /// </summary>
    public const float BufferRmsDbfs = -28f;

    /// <summary>
    /// The level to declare for a line said at a given effort: a sound's declared level is its buffer's
    /// full scale at one metre, and a line at <see cref="BufferRmsDbfs"/> has its full scale that far above
    /// its speech level.
    /// </summary>
    public static float LevelDb(float effortDb) => effortDb - BufferRmsDbfs;
}
