using System.Text;

namespace MeetingRecorder.WebApi.Common;

public static class DotEnv
{
    public static void Load(string? filePath = null)
    {
        var candidates = new[]
        {
            filePath,
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".env"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env")
        };

        string? foundPath = null;
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                foundPath = Path.GetFullPath(candidate);
                break;
            }
        }

        if (foundPath == null) return;

        var lines = File.ReadAllLines(foundPath);
        string? currentKey = null;
        var currentVal = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '"';

        foreach (var line in lines)
        {
            if (!inQuotes)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                    continue;

                var idx = line.IndexOf('=');
                if (idx < 0) continue;

                currentKey = line[..idx].Trim();
                var remainder = line[(idx + 1)..].Trim();

                if (remainder.StartsWith("\"") || remainder.StartsWith("'"))
                {
                    quoteChar = remainder[0];
                    var withoutOpen = remainder[1..];
                    if (withoutOpen.EndsWith(quoteChar.ToString()) && withoutOpen.Length > 0)
                    {
                        var val = withoutOpen[..^1];
                        SetEnvIfNotPresent(currentKey, val);
                        currentKey = null;
                    }
                    else
                    {
                        inQuotes = true;
                        currentVal.Clear();
                        currentVal.Append(withoutOpen);
                    }
                }
                else
                {
                    SetEnvIfNotPresent(currentKey, remainder);
                    currentKey = null;
                }
            }
            else
            {
                if (line.TrimEnd().EndsWith(quoteChar.ToString()))
                {
                    var content = line[..line.LastIndexOf(quoteChar)];
                    currentVal.AppendLine();
                    currentVal.Append(content);
                    if (currentKey != null)
                    {
                        SetEnvIfNotPresent(currentKey, currentVal.ToString());
                    }
                    inQuotes = false;
                    currentKey = null;
                }
                else
                {
                    currentVal.AppendLine();
                    currentVal.Append(line);
                }
            }
        }
    }

    private static void SetEnvIfNotPresent(string key, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
