using System;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>
/// Rescales interleaved float PCM in time without moving its pitch, so playback rate can be changed
/// without the sound of a tape being spun faster or slower.
/// </summary>
/// <remarks>
/// <para>
/// Waveform-similarity overlap-add: the stream is cut into fixed-length sequences that are laid
/// down at the spacing the requested rate calls for, so material is repeated when slowing down and
/// dropped when speeding up. Before a sequence is written it is slid within a short search window
/// to the offset whose waveform best continues the tail of the sequence before it, and the two are
/// cross-faded over that tail, which is what keeps the seams from clicking. No sample is ever
/// resampled, so every frequency in the source comes out where it went in.
/// </para>
/// <para>
/// Not thread safe: it belongs to the decode thread that owns the resampler feeding it.
/// </para>
/// </remarks>
internal sealed class AudioTimeStretcher
{
    // Sequence and search lengths in milliseconds, interpolated across the tempo range. Slowing
    // down wants long sequences so the repeats are far enough apart not to sound like an echo;
    // speeding up wants short ones so a beat is not heard twice.
    private const double SequenceMsAtHalfSpeed = 90.0;
    private const double SequenceMsAtDoubleSpeed = 40.0;
    private const double SearchMsAtHalfSpeed = 20.0;
    private const double SearchMsAtDoubleSpeed = 15.0;
    private const double OverlapMs = 8.0;

    private const double CenterBias = 0.1;
    private const double Epsilon = 1e-9;

    private readonly int sampleRate;
    private readonly int channels;

    private float[] input = Array.Empty<float>();
    private float[] output = Array.Empty<float>();
    private float[] tail = Array.Empty<float>();
    private float[] tailMono = Array.Empty<float>();
    private float[] inputMono = Array.Empty<float>();

    private int inputFrames;
    private int outputFrames;
    private double tailNorm;

    private double speed = 1.0;
    private int overlapLength;
    private int sequenceLength;
    private int searchLength;
    private int requiredFrames;
    private double skipRemainder;
    private bool primed;
    private long consumedFrames;

    public AudioTimeStretcher(int sampleRate, int channels)
    {
        this.sampleRate = sampleRate;
        this.channels = channels;
        Reconfigure();
    }

    public int SampleRate => sampleRate;

    public int Channels => channels;

    /// <summary>Media seconds consumed per second of output. Changing it keeps the seam state, so
    /// the rate can be moved during playback without a gap.</summary>
    public double Speed
    {
        get => speed;
        set
        {
            if (value <= 0 || Math.Abs(value - speed) < 1e-9)
                return;

            speed = value;
            Reconfigure();
        }
    }

    /// <summary>Frames taken out of the fed stream so far, which is the media time it stood for.</summary>
    public long ConsumedFrames => consumedFrames;

    public int OutputFrames => outputFrames;

    public ReadOnlySpan<float> Output => output.AsSpan(0, outputFrames * channels);

    /// <summary>Feeds a block and emits whatever complete sequences it completes.</summary>
    public void Process(ReadOnlySpan<float> interleaved)
    {
        Append(interleaved);
        while (inputFrames >= requiredFrames)
            Step();
    }

    public void ClearOutput() => outputFrames = 0;

    /// <summary>Drops everything held, for a seek or a break in the source.</summary>
    public void Reset()
    {
        inputFrames = 0;
        outputFrames = 0;
        skipRemainder = 0;
        primed = false;
        tailNorm = 0;
        Array.Clear(tail);
        Array.Clear(tailMono);
    }

    private void Reconfigure()
    {
        double tempo = Math.Clamp(speed, 0.5, 2.0);
        overlapLength = FramesOf(OverlapMs);
        sequenceLength = Math.Max(
            FramesOf(Interpolate(SequenceMsAtHalfSpeed, SequenceMsAtDoubleSpeed, tempo)),
            2 * overlapLength);
        searchLength = FramesOf(Interpolate(SearchMsAtHalfSpeed, SearchMsAtDoubleSpeed, tempo));

        // Enough for the furthest the search can push a whole sequence, and for the largest step the
        // rate can ask for, so a pass never reads past what has been fed.
        requiredFrames = (int)Math.Max(speed * (sequenceLength - overlapLength) + overlapLength, sequenceLength)
            + searchLength;

        EnsureLength(ref tail, overlapLength * channels);
        EnsureLength(ref tailMono, overlapLength);
        EnsureLength(ref inputMono, searchLength + overlapLength);
    }

    private void Append(ReadOnlySpan<float> interleaved)
    {
        EnsureLength(ref input, inputFrames * channels + interleaved.Length);
        interleaved.CopyTo(input.AsSpan(inputFrames * channels));
        inputFrames += interleaved.Length / channels;
    }

    private void Step()
    {
        int offset = primed ? FindBestOffset() : 0;
        int produced = sequenceLength - overlapLength;
        int start = outputFrames * channels;
        EnsureLength(ref output, start + produced * channels);

        if (primed)
        {
            for (int i = 0; i < overlapLength; i++)
            {
                float fadeIn = (float)i / overlapLength;
                int source = (offset + i) * channels;
                int destination = start + (i * channels);
                for (int c = 0; c < channels; c++)
                    output[destination + c] = (tail[(i * channels) + c] * (1f - fadeIn)) + (input[source + c] * fadeIn);
            }

            skipRemainder += speed * produced;
        }
        else
        {
            // Nothing to continue from yet, so the first sequence is written as it stands rather
            // than faded up out of silence.
            Array.Copy(input, offset * channels, output, start, overlapLength * channels);
            primed = true;
            skipRemainder = speed * produced;
        }

        int copied = sequenceLength - (2 * overlapLength);
        if (copied > 0)
        {
            Array.Copy(
                input,
                (offset + overlapLength) * channels,
                output,
                start + (overlapLength * channels),
                copied * channels);
        }

        outputFrames += produced;

        Array.Copy(input, (offset + sequenceLength - overlapLength) * channels, tail, 0, overlapLength * channels);
        Downmix(tail, tailMono, overlapLength);

        double norm = 0;
        for (int i = 0; i < overlapLength; i++)
            norm += tailMono[i] * tailMono[i];
        tailNorm = Math.Sqrt(norm);

        int skip = (int)skipRemainder;
        skipRemainder -= skip;
        Consume(skip);
    }

    /// <summary>Offset within the search window whose waveform best continues the previous tail.</summary>
    private int FindBestOffset()
    {
        Downmix(input, inputMono, searchLength + overlapLength);

        double norm = 0;
        for (int i = 0; i < overlapLength; i++)
            norm += inputMono[i] * inputMono[i];

        int best = 0;
        double bestScore = double.NegativeInfinity;
        for (int i = 0; i < searchLength; i++)
        {
            double dot = 0;
            for (int j = 0; j < overlapLength; j++)
                dot += inputMono[i + j] * tailMono[j];

            double correlation = dot / ((Math.Sqrt(norm) * tailNorm) + Epsilon);

            // Pulled gently toward the middle so a stretch of equally good matches does not walk
            // the sequence to one edge of the window and leave it there.
            double distance = ((2.0 * i) - searchLength) / searchLength;
            double score = correlation - (CenterBias * distance * distance);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }

            float leaving = inputMono[i];
            float entering = inputMono[i + overlapLength];
            norm = Math.Max(0, norm + (entering * entering) - (leaving * leaving));
        }

        return best;
    }

    private void Downmix(float[] source, float[] destination, int frames)
    {
        if (channels == 1)
        {
            Array.Copy(source, destination, frames);
            return;
        }

        int index = 0;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += source[index++];
            destination[i] = sum;
        }
    }

    private void Consume(int frames)
    {
        int remaining = (inputFrames - frames) * channels;
        if (remaining > 0)
            Array.Copy(input, frames * channels, input, 0, remaining);

        inputFrames -= frames;
        consumedFrames += frames;
    }

    private int FramesOf(double milliseconds) => Math.Max(1, (int)(sampleRate * milliseconds / 1000.0));

    private static double Interpolate(double atHalfSpeed, double atDoubleSpeed, double tempo) =>
        atHalfSpeed + ((atDoubleSpeed - atHalfSpeed) * (tempo - 0.5) / 1.5);

    private static void EnsureLength(ref float[] buffer, int length)
    {
        if (buffer.Length < length)
            Array.Resize(ref buffer, length);
    }
}
