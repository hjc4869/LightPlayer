using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;

namespace LightStudio.RemoteServices;

/// <summary>Half-open byte interval <c>[Start, End)</c> of a remote resource.</summary>
internal readonly record struct RemoteByteRange(long Start, long End);

/// <summary>
/// Sends ranged requests to one endpoint and remembers when it cannot answer several ranges at once.
/// </summary>
/// <remarks>
/// An endpoint that rejects a multiple-range request with <c>416</c> or ignores it with <c>200</c>
/// receives one range covering all requested ranges from then on.
/// </remarks>
internal sealed class RemoteRangeRequests
{
    private volatile bool singleRangeOnly;

    public async Task<RemoteRangeResponse> OpenAsync(
        IReadOnlyList<RemoteByteRange> ranges,
        long length,
        Func<RangeHeaderValue, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        if (ranges.Count == 0)
        {
            throw new ArgumentException("At least one byte range is required.", nameof(ranges));
        }

        if (ranges.Count > 1 && !singleRangeOnly)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await send(CreateHeader(ranges, length), cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var opened = await RemoteRangeResponse
                        .OpenAsync(response, length, ranges, cancellationToken)
                        .ConfigureAwait(false);
                    response = null;
                    return opened;
                }
            }
            catch (HttpRequestException exception) when (
                exception.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
            }
            finally
            {
                response?.Dispose();
            }

            singleRangeOnly = true;
            AppLog.Information(
                "Remote.Media",
                "The server does not answer multiple byte ranges; requesting covering ranges instead.");
        }

        RemoteByteRange[] covering = [new(ranges[0].Start, ranges[^1].End)];
        var single = await send(CreateHeader(covering, length), cancellationToken).ConfigureAwait(false);
        try
        {
            return await RemoteRangeResponse.OpenAsync(single, length, covering, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            single.Dispose();
            throw;
        }
    }

    /// <summary>Formats the ranges, leaving a range that reaches the end of the resource open-ended.</summary>
    private static RangeHeaderValue CreateHeader(IReadOnlyList<RemoteByteRange> ranges, long length)
    {
        var header = new RangeHeaderValue();
        foreach (var range in ranges)
        {
            header.Ranges.Add(new RangeItemHeaderValue(range.Start, range.End >= length ? null : range.End - 1));
        }

        return header;
    }
}

/// <summary>
/// Body of a ranged response, read part by part as it arrives.
/// </summary>
/// <remarks>
/// Accepts a single <c>206</c> range, a <c>multipart/byteranges</c> response, or a <c>200</c> response
/// with the whole resource when the request started at its first byte. Parts are reported as they
/// appear; callers handle bytes outside the ranges they asked for.
/// </remarks>
internal sealed class RemoteRangeResponse : IDisposable
{
    private const int BufferBytes = 16 * 1024;
    private const int MaximumLineBytes = 8 * 1024;
    private const int MaximumBoundaryBytes = 70;
    private const int MaximumSkippedLines = 16;
    private const int MaximumPartHeaders = 32;
    private const int SkipBufferBytes = 64 * 1024;

    private readonly HttpResponseMessage response;
    private readonly Stream body;
    private readonly long length;
    private readonly string? delimiter;
    private readonly byte[] buffer;
    private RemoteByteRange? singlePart;
    private int bufferStart;
    private int bufferEnd;
    private long remaining;
    private bool finished;

    private RemoteRangeResponse(
        HttpResponseMessage response,
        Stream body,
        long length,
        string? delimiter,
        RemoteByteRange? singlePart)
    {
        this.response = response;
        this.body = body;
        this.length = length;
        this.delimiter = delimiter;
        this.singlePart = singlePart;
        buffer = delimiter is null ? [] : new byte[BufferBytes];
    }

    /// <summary>First byte of the current part.</summary>
    public long PartStart { get; private set; }

    /// <summary>End (exclusive) of the current part.</summary>
    public long PartEnd { get; private set; }

    /// <summary>Wraps a successful response, taking ownership of it once this method returns.</summary>
    /// <param name="requested">The ranges the request named, in ascending order.</param>
    public static async Task<RemoteRangeResponse> OpenAsync(
        HttpResponseMessage response,
        long length,
        IReadOnlyList<RemoteByteRange> requested,
        CancellationToken cancellationToken)
    {
        RemoteByteRange? singlePart = null;
        string? delimiter = null;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var contentType = response.Content.Headers.ContentType;
            if (string.Equals(contentType?.MediaType, "multipart/byteranges", StringComparison.OrdinalIgnoreCase))
            {
                string? boundary = null;
                foreach (var parameter in contentType!.Parameters)
                {
                    if (string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase))
                    {
                        boundary = parameter.Value?.Trim('"');
                    }
                }

                if (string.IsNullOrEmpty(boundary) || boundary.Length > MaximumBoundaryBytes)
                {
                    throw new InvalidDataException("The multipart range response has no valid boundary.");
                }

                delimiter = "--" + boundary;
            }
            else if (response.Content.Headers.ContentRange is { } contentRange)
            {
                singlePart = ParseContentRange(contentRange, length);
            }
            else if (requested.Count == 1)
            {
                // Some endpoints omit Content-Range; the body length is still checked as it is read.
                singlePart = requested[0];
            }
            else
            {
                throw new InvalidDataException("The partial response did not describe its byte range.");
            }
        }
        else if (response.StatusCode == HttpStatusCode.OK && requested[0].Start == 0)
        {
            singlePart = new RemoteByteRange(0, length);
        }
        else
        {
            throw new InvalidDataException(
                $"The server answered a byte range request with {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteRangeResponse(response, body, length, delimiter, singlePart);
    }

    /// <summary>Advances to the next part, skipping what is left of the current one.</summary>
    /// <returns><see langword="false"/> once the response has no further parts.</returns>
    public async ValueTask<bool> MoveNextPartAsync(CancellationToken cancellationToken)
    {
        if (delimiter is null)
        {
            if (singlePart is not { } part)
            {
                return false;
            }

            singlePart = null;
            SetPart(part);
            return true;
        }

        if (remaining > 0)
        {
            await SkipAsync(remaining, cancellationToken).ConfigureAwait(false);
        }

        if (finished)
        {
            return false;
        }

        // The body of the previous part is followed by CRLF, which reads as an empty line.
        for (var skipped = 0; ; skipped++)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The multipart range response ended before its closing boundary.");
            if (line.StartsWith(delimiter, StringComparison.Ordinal))
            {
                var rest = line.AsSpan(delimiter.Length);
                if (rest.StartsWith("--"))
                {
                    finished = true;
                    return false;
                }

                if (rest.IsWhiteSpace())
                {
                    break;
                }
            }

            if (skipped == MaximumSkippedLines)
            {
                throw new InvalidDataException("The multipart range response does not delimit its parts.");
            }
        }

        ContentRangeHeaderValue? contentRange = null;
        for (var headers = 0; ; headers++)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The multipart range response ended inside part headers.");
            if (line.Length == 0)
            {
                break;
            }

            if (headers == MaximumPartHeaders)
            {
                throw new InvalidDataException("A multipart range response part has too many headers.");
            }

            var separator = line.IndexOf(':');
            if (separator > 0
                && line.AsSpan(0, separator).Trim().Equals("Content-Range", StringComparison.OrdinalIgnoreCase)
                && ContentRangeHeaderValue.TryParse(line[(separator + 1)..].Trim(), out var parsed))
            {
                contentRange = parsed;
            }
        }

        SetPart(ParseContentRange(
            contentRange ?? throw new InvalidDataException("A multipart range response part has no Content-Range."),
            length));
        return true;
    }

    /// <summary>Reads from the current part; returns 0 once the part is exhausted.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (remaining == 0 || destination.IsEmpty)
        {
            return 0;
        }

        if (destination.Length > remaining)
        {
            destination = destination[..(int)remaining];
        }

        int read;
        if (bufferEnd > bufferStart)
        {
            read = Math.Min(destination.Length, bufferEnd - bufferStart);
            buffer.AsSpan(bufferStart, read).CopyTo(destination.Span);
            bufferStart += read;
        }
        else
        {
            read = await body.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException(
                    $"The range response ended {remaining} byte(s) before the end of bytes {PartStart}-{PartEnd - 1}.");
            }
        }

        remaining -= read;
        return read;
    }

    /// <summary>Fills <paramref name="destination"/> from the current part.</summary>
    public async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.Length > remaining)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), "The read extends beyond the current part.");
        }

        while (!destination.IsEmpty)
        {
            var read = await ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            destination = destination[read..];
        }
    }

    /// <summary>Discards <paramref name="count"/> bytes of the current part.</summary>
    public async ValueTask SkipAsync(long count, CancellationToken cancellationToken)
    {
        if (count > remaining)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The skip extends beyond the current part.");
        }

        var scratch = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, SkipBufferBytes));
        try
        {
            while (count > 0)
            {
                var read = await ReadAsync(
                    scratch.AsMemory(0, (int)Math.Min(count, scratch.Length)),
                    cancellationToken).ConfigureAwait(false);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public void Dispose()
    {
        body.Dispose();
        response.Dispose();
    }

    private void SetPart(RemoteByteRange part)
    {
        PartStart = part.Start;
        PartEnd = part.End;
        remaining = part.End - part.Start;
    }

    private static RemoteByteRange ParseContentRange(ContentRangeHeaderValue contentRange, long length)
    {
        if (!string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || contentRange.From is not { } from
            || contentRange.To is not { } to
            || from > to
            || to >= length)
        {
            throw new InvalidDataException($"The server returned an invalid byte range '{contentRange}'.");
        }

        if (contentRange.Length is { } total && total != length)
        {
            throw new InvalidDataException("The remote file changed while it was being read.");
        }

        return new RemoteByteRange(from, to + 1);
    }

    /// <summary>Reads one CRLF- or LF-terminated line, or returns null at the end of the body.</summary>
    private async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', bufferStart, bufferEnd - bufferStart);
            if (newline >= 0)
            {
                var end = newline > bufferStart && buffer[newline - 1] == (byte)'\r' ? newline - 1 : newline;
                var line = Encoding.Latin1.GetString(buffer, bufferStart, end - bufferStart);
                bufferStart = newline + 1;
                return line;
            }

            if (bufferEnd - bufferStart >= MaximumLineBytes)
            {
                throw new InvalidDataException("A multipart range response line is too long.");
            }

            if (bufferStart > 0)
            {
                Buffer.BlockCopy(buffer, bufferStart, buffer, 0, bufferEnd - bufferStart);
                bufferEnd -= bufferStart;
                bufferStart = 0;
            }

            var read = await body.ReadAsync(buffer.AsMemory(bufferEnd), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return bufferEnd > bufferStart
                    ? throw new IOException("The multipart range response ended inside a line.")
                    : null;
            }

            bufferEnd += read;
        }
    }
}
