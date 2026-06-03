public static class BuildInfo
{
    private static readonly Lazy<string> VersionValue = new(ResolveVersion);

    public static string Version => VersionValue.Value;

    private static string ResolveVersion()
    {
        string? configuredVersion = Environment.GetEnvironmentVariable("SKYVAULT_VERSION");

        if (!string.IsNullOrWhiteSpace(configuredVersion))
        {
            return Normalize(configuredVersion);
        }

        string? gitHash = TryReadGitHash();

        if (!string.IsNullOrWhiteSpace(gitHash))
        {
            return $"V.dev-{gitHash}";
        }

        return "V.dev";
    }

    private static string Normalize(string version)
    {
        version = version.Trim();
        return version.StartsWith('V') || version.StartsWith('v') ? version : $"V.{version}";
    }

    private static string? TryReadGitHash()
    {
        try
        {
            string gitDirectory = ".git";
            string headPath = Path.Combine(gitDirectory, "HEAD");

            if (!File.Exists(headPath))
            {
                return null;
            }

            string head = File.ReadAllText(headPath).Trim();

            if (head.StartsWith("ref:", StringComparison.Ordinal))
            {
                string refPath = head["ref:".Length..].Trim();
                string fullRefPath = Path.Combine(gitDirectory, refPath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(fullRefPath))
                {
                    return null;
                }

                head = File.ReadAllText(fullRefPath).Trim();
            }

            return head.Length >= 7 ? head[..7] : head;
        }
        catch
        {
            return null;
        }
    }
}
