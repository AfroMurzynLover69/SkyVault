using System.Text;

public sealed class HttpResponse
{
    private const int StreamBufferBytes = 64 * 1024;
    private readonly int statusCode;
    private readonly string reason;
    private readonly byte[]? bodyBytes;
    private readonly string contentType;
    private readonly Func<CancellationToken, Task<Stream?>>? openBodyStream;
    private readonly long? contentLength;

    public HttpResponse(int statusCode, string reason, string body)
    {
        this.statusCode = statusCode;
        this.reason = reason;
        bodyBytes = Encoding.UTF8.GetBytes(body);
        contentType = "text/html; charset=utf-8";
        contentLength = bodyBytes.Length;
    }

    public HttpResponse(int statusCode, string reason, string body, string contentType)
    {
        this.statusCode = statusCode;
        this.reason = reason;
        bodyBytes = Encoding.UTF8.GetBytes(body);
        this.contentType = contentType;
        contentLength = bodyBytes.Length;
    }

    public HttpResponse(int statusCode, string reason, byte[] bodyBytes, string contentType)
    {
        this.statusCode = statusCode;
        this.reason = reason;
        this.bodyBytes = bodyBytes;
        this.contentType = contentType;
        contentLength = bodyBytes.Length;
    }

    public HttpResponse(int statusCode, string reason, string contentType, long contentLength, Func<CancellationToken, Task<Stream?>> openBodyStream)
    {
        this.statusCode = statusCode;
        this.reason = reason;
        this.contentType = contentType;
        this.contentLength = contentLength;
        this.openBodyStream = openBodyStream;
    }

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task WriteAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Headers["Content-Type"] = contentType;
        Headers["Content-Length"] = (contentLength ?? bodyBytes?.LongLength ?? 0).ToString();
        Headers["Connection"] = "close";

        var headerBuilder = new StringBuilder();
        headerBuilder.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n");

        foreach ((string name, string value) in Headers)
        {
            headerBuilder.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        headerBuilder.Append("\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(headerBuilder.ToString());
        await stream.WriteAsync(headerBytes, cancellationToken);

        if (bodyBytes is not null)
        {
            await stream.WriteAsync(bodyBytes, cancellationToken);
            return;
        }

        if (openBodyStream is null)
        {
            return;
        }

        await using Stream? bodyStream = await openBodyStream(cancellationToken);

        if (bodyStream is null)
        {
            return;
        }

        if (contentLength is null)
        {
            await bodyStream.CopyToAsync(stream, StreamBufferBytes, cancellationToken);
            return;
        }

        byte[] buffer = new byte[StreamBufferBytes];
        long remaining = contentLength.Value;

        while (remaining > 0)
        {
            int read = await bodyStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);

            if (read == 0)
            {
                break;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }
}
