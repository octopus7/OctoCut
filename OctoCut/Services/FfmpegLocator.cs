using System.IO;

namespace OctoCut.Services;

public static class FfmpegLocator
{
    public static string? Resolve(string? configuredPath)
    {
        if (IsUsableExecutable(configuredPath))
        {
            return Path.GetFullPath(configuredPath!);
        }

        return FindOnPath();
    }

    public static bool IsUsableExecutable(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static string? FindOnPath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var rawDirectory in pathValue.Split(Path.PathSeparator))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
