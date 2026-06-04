using System.Net;
using System.Text;

public sealed class HttpRequest
{
    private const int DefaultMaxBufferedBodyBytes = 10 * 1024 * 1024;
    private const int MultipartBufferBytes = 64 * 1024;
    private const int MaxMultipartHeaderBytes = 64 * 1024;
    private static int maxMultipartFieldBytes = 64 * 1024;

    public required string Method { get; init; }
    public required string Path { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Form { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];
    public Stream? BodyStream { get; init; }
    public long ContentLength { get; init; }

    public static async Task<HttpRequest?> ReadAsync(Stream stream, ServerOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ServerOptions();
        maxMultipartFieldBytes = options.MaxMultipartFieldBytes;
        string? headerText = await ReadHeaderTextAsync(stream, options.MaxHeaderBytes, TimeSpan.FromSeconds(options.HeaderReadTimeoutSeconds), cancellationToken);

        if (string.IsNullOrWhiteSpace(headerText))
        {
            return null;
        }

        string[] headerLines = headerText.Split("\r\n", StringSplitOptions.None);
        string requestLine = headerLines[0];
        string[] requestParts = requestLine.Split(' ', 3);

        if (requestParts.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in headerLines.Skip(1))
        {
            int separator = line.IndexOf(':');

            if (separator > 0)
            {
                string name = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();
                headers[name] = value;
            }
        }

        long contentLength = 0;

        if (headers.TryGetValue("Content-Length", out string? lengthText)
            && long.TryParse(lengthText, out long parsedLength)
            && parsedLength > 0)
        {
            contentLength = parsedLength;
        }

        string[] targetParts = requestParts[1].Split('?', 2);
        bool isMultipart = IsMultipart(headers);
        byte[] body = [];

        if (!isMultipart && contentLength > 0)
        {
            int maxBufferedBodyBytes = Math.Max(DefaultMaxBufferedBodyBytes, options.MaxChunkUploadBytes);

            if (contentLength > maxBufferedBodyBytes)
            {
                throw new InvalidOperationException($"Request body is too large to buffer: {contentLength} bytes.");
            }

            int length = (int)contentLength;
            body = new byte[length];
            int offset = 0;

            while (offset < length)
            {
                int read = await stream.ReadAsync(body.AsMemory(offset, length - offset), cancellationToken);

                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            if (offset != length)
            {
                Array.Resize(ref body, offset);
            }
        }

        string bodyText = Encoding.UTF8.GetString(body);

        return new HttpRequest
        {
            Method = requestParts[0],
            Path = targetParts[0],
            Headers = headers,
            Query = targetParts.Length == 2 ? ParseForm(targetParts[1]) : new Dictionary<string, string>(),
            Form = IsUrlEncoded(headers) ? ParseForm(bodyText) : new Dictionary<string, string>(),
            Body = body,
            BodyStream = isMultipart ? stream : null,
            ContentLength = contentLength
        };
    }

    private static async Task<string?> ReadHeaderTextAsync(Stream stream, int maxHeaderBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        byte[] buffer = new byte[1];
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        while (true)
        {
            int read = await stream.ReadAsync(buffer, timeoutSource.Token);

            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add(buffer[0]);

            if (bytes.Count > maxHeaderBytes)
            {
                throw new InvalidDataException("HTTP headers are too large.");
            }

            if (bytes.Count >= 4
                && bytes[^4] == '\r'
                && bytes[^3] == '\n'
                && bytes[^2] == '\r'
                && bytes[^1] == '\n')
            {
                return Encoding.ASCII.GetString(bytes.Take(bytes.Count - 4).ToArray());
            }
        }
    }

    public string? GetMultipartField(string fieldName)
    {
        if (!TryGetMultipartBoundary(out string boundary))
        {
            return null;
        }

        foreach (MultipartPart part in EnumerateMultipartParts(boundary))
        {
            string? name = ExtractQuotedValue(part.HeaderText, "name");

            if (!string.Equals(name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? fileName = ExtractQuotedValue(part.HeaderText, "filename");

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(part.DataText));
        }

        return null;
    }

    public string? GetCookie(string name)
    {
        if (!Headers.TryGetValue("Cookie", out string? cookieHeader))
        {
            return null;
        }

        string[] cookies = cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (string cookie in cookies)
        {
            string[] parts = cookie.Trim().Split('=', 2);

            if (parts.Length == 2 && parts[0] == name)
            {
                return parts[1];
            }
        }

        return null;
    }

    public async Task<MultipartReadResult> ReadMultipartToTempFilesAsync(string tempRoot, long maxFileBytes, CancellationToken cancellationToken = default)
    {
        if (!TryGetMultipartBoundary(out string boundary))
        {
            return new MultipartReadResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);
        }

        if (BodyStream is null)
        {
            throw new InvalidOperationException("Multipart body stream is not available.");
        }

        Directory.CreateDirectory(tempRoot);

        var reader = new MultipartStreamReader(BodyStream, boundary, ContentLength, cancellationToken);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<UploadedTempFile>();
        long totalFileBytes = 0;
        string? currentTempPath = null;

        try
        {
            await reader.ReadToFirstBoundaryAsync();

            while (await reader.ReadNextPartHeadersAsync() is MultipartHeaders headers)
            {
                string? name = ExtractQuotedValue(headers.HeaderText, "name");

                if (string.IsNullOrWhiteSpace(name))
                {
                    await reader.DiscardCurrentPartAsync();
                    continue;
                }

                string? fileName = ExtractQuotedValue(headers.HeaderText, "filename");

                if (string.IsNullOrWhiteSpace(fileName))
                {
                    using var fieldBytes = new MemoryStream();
                    await reader.CopyCurrentPartAsync(fieldBytes, maxMultipartFieldBytes, () => false);
                    fields[name] = Encoding.UTF8.GetString(fieldBytes.ToArray());
                    continue;
                }

                currentTempPath = System.IO.Path.Combine(tempRoot, $"{Guid.NewGuid():N}.upload");
                await using (var tempFile = new FileStream(currentTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, MultipartBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    long remainingFileBytes = Math.Max(0, maxFileBytes - totalFileBytes);
                    long written = await reader.CopyCurrentPartAsync(tempFile, remainingFileBytes, () => false);
                    totalFileBytes += written;
                }

                files.Add(new UploadedTempFile(name, fileName, currentTempPath, new FileInfo(currentTempPath).Length));
                currentTempPath = null;
            }

            return new MultipartReadResult(fields, files);
        }
        catch
        {
            if (currentTempPath is not null)
            {
                DeleteQuietly(currentTempPath);
            }

            foreach (UploadedTempFile file in files)
            {
                DeleteQuietly(file.TempPath);
            }

            throw;
        }
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = WebUtility.UrlDecode(parts[0]);
            string value = parts.Length == 2 ? WebUtility.UrlDecode(parts[1]) : "";
            form[key] = value;
        }

        return form;
    }

    private static bool IsUrlEncoded(Dictionary<string, string> headers)
    {
        return headers.TryGetValue("Content-Type", out string? contentType)
            && contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMultipart(Dictionary<string, string> headers)
    {
        return headers.TryGetValue("Content-Type", out string? contentType)
            && contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetMultipartBoundary(out string boundary)
    {
        boundary = "";

        if (!Headers.TryGetValue("Content-Type", out string? contentType)
            || !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? parsed = GetBoundary(contentType);

        if (string.IsNullOrWhiteSpace(parsed))
        {
            return false;
        }

        boundary = parsed;
        return true;
    }

    private IEnumerable<MultipartPart> EnumerateMultipartParts(string boundary)
    {
        string bodyText = Encoding.Latin1.GetString(Body);
        string marker = "--" + boundary;
        string[] parts = bodyText.Split(marker, StringSplitOptions.RemoveEmptyEntries);

        foreach (string rawPart in parts)
        {
            if (rawPart.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string part = rawPart.TrimStart('\r', '\n');
            int headerEnd = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);

            if (headerEnd < 0)
            {
                continue;
            }

            string headerText = part[..headerEnd];
            string dataText = part[(headerEnd + 4)..];

            if (dataText.EndsWith("\r\n", StringComparison.Ordinal))
            {
                dataText = dataText[..^2];
            }

            yield return new MultipartPart(headerText, dataText);
        }
    }

    private static string? GetBoundary(string contentType)
    {
        foreach (string segment in contentType.Split(';'))
        {
            string trimmed = segment.Trim();

            if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["boundary=".Length..].Trim('"');
            }
        }

        return null;
    }

    private static string? ExtractQuotedValue(string text, string name)
    {
        string prefix = name + "=\"";
        int start = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            return null;
        }

        start += prefix.Length;
        int end = text.IndexOf('"', start);

        return end < 0 ? null : text[start..end];
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private sealed record MultipartPart(string HeaderText, string DataText);

    private sealed record MultipartHeaders(string HeaderText);

    private sealed class MultipartStreamReader
    {
        private readonly Stream stream;
        private readonly byte[] boundary;
        private readonly byte[] contentDelimiter;
        private readonly long contentLength;
        private readonly byte[] readBuffer = new byte[MultipartBufferBytes];
        private readonly CancellationToken cancellationToken;
        private int readOffset;
        private int readLength;
        private long consumed;
        private bool finalBoundary;

        public MultipartStreamReader(Stream stream, string boundary, long contentLength, CancellationToken cancellationToken)
        {
            this.stream = stream;
            this.boundary = Encoding.ASCII.GetBytes("--" + boundary);
            contentDelimiter = Encoding.ASCII.GetBytes("\r\n--" + boundary);
            this.contentLength = contentLength;
            this.cancellationToken = cancellationToken;
        }

        public async Task ReadToFirstBoundaryAsync()
        {
            string? line;

            while ((line = await ReadLineAsync(MaxMultipartHeaderBytes)) is not null)
            {
                if (line == Encoding.ASCII.GetString(boundary))
                {
                    return;
                }

                if (line == Encoding.ASCII.GetString(boundary) + "--")
                {
                    finalBoundary = true;
                    return;
                }
            }

            throw new InvalidDataException("Multipart boundary was not found.");
        }

        public async Task<MultipartHeaders?> ReadNextPartHeadersAsync()
        {
            if (finalBoundary)
            {
                return null;
            }

            var header = new StringBuilder();

            while (true)
            {
                string? line = await ReadLineAsync(MaxMultipartHeaderBytes);

                if (line is null)
                {
                    return null;
                }

                if (line.Length == 0)
                {
                    return new MultipartHeaders(header.ToString());
                }

                header.Append(line).Append("\r\n");

                if (header.Length > MaxMultipartHeaderBytes)
                {
                    throw new InvalidDataException("Multipart headers are too large.");
                }
            }
        }

        public Task DiscardCurrentPartAsync()
        {
            return CopyCurrentPartAsync(Stream.Null, long.MaxValue, () => false);
        }

        public async Task<long> CopyCurrentPartAsync(Stream destination, long maxBytes, Func<bool> quotaExceeded)
        {
            byte[] matched = new byte[contentDelimiter.Length];
            byte[] output = new byte[MultipartBufferBytes];
            byte[] singleByte = new byte[1];
            int matchedLength = 0;
            int outputLength = 0;
            long written = 0;

            async Task FlushAsync()
            {
                if (outputLength == 0)
                {
                    return;
                }

                if (written + outputLength > maxBytes || quotaExceeded())
                {
                    throw new UploadQuotaExceededException();
                }

                await destination.WriteAsync(output.AsMemory(0, outputLength), cancellationToken);
                written += outputLength;
                outputLength = 0;
            }

            async Task AppendAsync(ReadOnlyMemory<byte> bytes)
            {
                int offset = 0;

                while (offset < bytes.Length)
                {
                    int copy = Math.Min(bytes.Length - offset, output.Length - outputLength);
                    bytes.Slice(offset, copy).CopyTo(output.AsMemory(outputLength, copy));
                    outputLength += copy;
                    offset += copy;

                    if (outputLength == output.Length)
                    {
                        await FlushAsync();
                    }
                }
            }

            while (true)
            {
                int value = await ReadByteAsync();

                if (value < 0)
                {
                    throw new EndOfStreamException("Multipart body ended before the next boundary.");
                }

                byte current = (byte)value;

                if (current == contentDelimiter[matchedLength])
                {
                    matched[matchedLength++] = current;

                    if (matchedLength == contentDelimiter.Length)
                    {
                        await FlushAsync();
                        string? boundarySuffix = await ReadLineAsync(MaxMultipartHeaderBytes);
                        finalBoundary = boundarySuffix == "--";
                        return written;
                    }

                    continue;
                }

                if (matchedLength > 0)
                {
                    await AppendAsync(matched.AsMemory(0, matchedLength));
                    matchedLength = 0;
                }

                singleByte[0] = current;
                await AppendAsync(singleByte);
            }
        }

        private async Task<string?> ReadLineAsync(int maxBytes)
        {
            var bytes = new List<byte>();

            while (true)
            {
                int value = await ReadByteAsync();

                if (value < 0)
                {
                    return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                }

                if (value == '\n')
                {
                    if (bytes.Count > 0 && bytes[^1] == '\r')
                    {
                        bytes.RemoveAt(bytes.Count - 1);
                    }

                    return Encoding.ASCII.GetString(bytes.ToArray());
                }

                bytes.Add((byte)value);

                if (bytes.Count > maxBytes)
                {
                    throw new InvalidDataException("Multipart line is too large.");
                }
            }
        }

        private async Task<int> ReadByteAsync()
        {
            if (contentLength > 0 && consumed >= contentLength)
            {
                return -1;
            }

            if (readOffset >= readLength)
            {
                int maxRead = readBuffer.Length;

                if (contentLength > 0)
                {
                    maxRead = (int)Math.Min(maxRead, contentLength - consumed);
                }

                readLength = await stream.ReadAsync(readBuffer.AsMemory(0, maxRead), cancellationToken);
                readOffset = 0;

                if (readLength == 0)
                {
                    return -1;
                }
            }

            consumed += 1;
            return readBuffer[readOffset++];
        }
    }
}

public sealed record UploadedTempFile(string FieldName, string FileName, string TempPath, long Length);

public sealed record MultipartReadResult(Dictionary<string, string> Fields, IReadOnlyList<UploadedTempFile> Files);

public sealed class UploadQuotaExceededException : Exception
{
    public UploadQuotaExceededException()
        : base("Upload exceeds the available quota.")
    {
    }
}
