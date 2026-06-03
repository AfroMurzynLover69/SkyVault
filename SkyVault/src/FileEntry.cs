public sealed record FileEntry(string Name, long SizeBytes, DateTimeOffset ModifiedAt, bool IsFolder = false);
