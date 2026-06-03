using System.Net;
using System.Text;

public sealed class HttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Form { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];

    public static async Task<HttpRequest?> ReadAsync(Stream stream)
    {
        string? headerText = await ReadHeaderTextAsync(stream);

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

        byte[] body = [];

        if (headers.TryGetValue("Content-Length", out string? lengthText)
            && int.TryParse(lengthText, out int length)
            && length > 0)
        {
            body = new byte[length];
            int offset = 0;

            while (offset < length)
            {
                int read = await stream.ReadAsync(body.AsMemory(offset, length - offset));

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

        string[] targetParts = requestParts[1].Split('?', 2);
        string bodyText = Encoding.UTF8.GetString(body);

        return new HttpRequest
        {
            Method = requestParts[0],
            Path = targetParts[0],
            Headers = headers,
            Query = targetParts.Length == 2 ? ParseForm(targetParts[1]) : new Dictionary<string, string>(),
            Form = IsUrlEncoded(headers) ? ParseForm(bodyText) : new Dictionary<string, string>(),
            Body = body
        };
    }

    private static async Task<string?> ReadHeaderTextAsync(Stream stream)
    {
        var bytes = new List<byte>();
        byte[] buffer = new byte[1];

        while (true)
        {
            int read = await stream.ReadAsync(buffer);

            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add(buffer[0]);

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

    public MultipartFile? GetUploadedFile(string fieldName)
    {
        if (!Headers.TryGetValue("Content-Type", out string? contentType)
            || !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? boundary = GetBoundary(contentType);

        if (boundary is null)
        {
            return null;
        }

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

            if (!headerText.Contains("name=\"" + fieldName + "\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? fileName = ExtractQuotedValue(headerText, "filename");

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            byte[] content = Encoding.Latin1.GetBytes(dataText);
            return new MultipartFile(fileName, content);
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
}
