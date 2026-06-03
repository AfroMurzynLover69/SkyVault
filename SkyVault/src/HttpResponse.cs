using System.Text;

public sealed class HttpResponse
{
    private readonly int statusCode;
    private readonly string reason;
    private readonly string body;

    public HttpResponse(int statusCode, string reason, string body)
    {
        this.statusCode = statusCode;
        this.reason = reason;
        this.body = body;
    }

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task WriteAsync(Stream stream)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

        Headers["Content-Type"] = "text/html; charset=utf-8";
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
