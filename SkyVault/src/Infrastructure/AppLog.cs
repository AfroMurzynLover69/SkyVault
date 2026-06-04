using System.Globalization;
using System.Text;

public static class AppLog
{
    private static readonly object Sync = new();
    private static string? logPath;

    public static void Initialize(string path)
    {
        lock (Sync)
        {
            logPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(logPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(logPath))
            {
                File.WriteAllText(logPath, "SkyVault log" + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    private static void Write(string level, string message, Exception? exception = null)
    {
        string line = $"[{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}] {level}: {message}";

        lock (Sync)
        {
            if (logPath is null)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine(line);

            if (exception is not null)
            {
                builder.AppendLine(exception.GetType().FullName + ": " + exception.Message);
                builder.AppendLine(exception.StackTrace ?? "");
            }

            File.AppendAllText(logPath, builder.ToString(), Encoding.UTF8);
        }

        Console.WriteLine(line);

        if (exception is not null)
        {
            Console.WriteLine(exception.GetType().FullName + ": " + exception.Message);
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                Console.WriteLine(exception.StackTrace);
            }
        }
    }
}
