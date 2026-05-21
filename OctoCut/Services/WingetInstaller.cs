using System.Diagnostics;
using System.IO;

namespace OctoCut.Services;

public static class WingetInstaller
{
    public const string FfmpegPackageId = "Gyan.FFmpeg";

    private const string InstallCommand =
        "winget install --id Gyan.FFmpeg -e --source winget --accept-package-agreements --accept-source-agreements";

    public static bool IsAvailable()
    {
        return FindExecutable("winget.exe") is not null;
    }

    public static async Task InstallFfmpegAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable())
        {
            throw new InvalidOperationException("winget.exe를 찾을 수 없습니다.");
        }

        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandProcessor))
        {
            commandProcessor = "cmd.exe";
        }

        using var process = new Process();
        process.StartInfo.FileName = commandProcessor;
        process.StartInfo.Arguments =
            $"/v:on /c \"{InstallCommand} & set OCTOCUT_WINGET_EXIT=!ERRORLEVEL! & echo. & echo Close this window after installation, then return to OctoCut. & pause & exit /b !OCTOCUT_WINGET_EXIT!\"";
        process.StartInfo.UseShellExecute = true;
        process.StartInfo.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!process.Start())
        {
            throw new InvalidOperationException("winget 설치 창을 열 수 없습니다.");
        }

        await process.WaitForExitAsync(cancellationToken);
    }

    private static string? FindExecutable(string executableName)
    {
        foreach (var directory in GetSearchDirectories())
        {
            var candidate = Path.Combine(directory, executableName);
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

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windowsApps = Path.Combine(localAppData, "Microsoft", "WindowsApps");
        if (seen.Add(windowsApps))
        {
            yield return windowsApps;
        }
    }
}
