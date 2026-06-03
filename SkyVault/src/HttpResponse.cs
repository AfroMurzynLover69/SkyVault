using System.Text;

public sealed class HttpResponse
{
    private readonly int statusCode;
    private readonly string reason;
    private readonly byte[] bodyBytes;
    private readonly string contentType;

    public HttpResponse(int statusCode, string reason, string body)
    {
        this.statusCode = statusCode;
        this.reason = reason;
        bodyBytes = Encoding.UTF8.GetBytes(body);
        contentType = "text/html; charset=utf-8";
    }

    public HttpResponse(int statusCode, string reason, byte[] bodyBytes, string contentType)
    {
        this.statusCode = statusCode;
        this.reason = reason;
        this.bodyBytes = bodyBytes;
        this.contentType = contentType;
    }

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task WriteAsync(Stream stream)
    {
        Headers["Content-Type"] = contentType;
        Headers["Content-Length"] = bodyBytes.Length.ToString();
        Headers["Connection"] = "close";

        var headerBuilder = new StringBuilder();
        headerBuilder.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n");

        foreach ((string name, string value) in Headers)
        {
            headerBuilder.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        headerBuilder.Append("\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(headerBuilder.ToString());
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
    }
}
