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
        foreach (var directory in GetSearchDirectories())
        {
            var candidate = Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in new[]
                 {
                     EnvironmentVariableTarget.Process,
                     EnvironmentVariableTarget.User,
                     EnvironmentVariableTarget.Machine
                 })
        {
            var pathValue = Environment.GetEnvironmentVariable("PATH", target);
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                continue;
            }

            foreach (var rawDirectory in pathValue.Split(Path.PathSeparator))
            {
                var directory = rawDirectory.Trim().Trim('"');
                if (directory.Length > 0 && seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }

        foreach (var directory in GetKnownWingetDirectories())
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }
    }

    private static IEnumerable<string> GetKnownWingetDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
            yield return Path.Combine(localAppData, "Microsoft", "WindowsApps");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "WinGet", "Links");
        }
    }
}
