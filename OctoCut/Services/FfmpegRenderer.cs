using System.Diagnostics;
using System.IO;
using OctoCut.Models;

namespace OctoCut.Services;

public sealed class FfmpegRenderer
{
    public async Task RenderAsync(
        string ffmpegPath,
        string inputPath,
        IReadOnlyList<ClipSegment> clips,
        string outputPath,
        RenderMode mode,
        RenderProgressText text,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (clips.Count == 0)
        {
            throw new InvalidOperationException(text.NoClips);
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "OctoCut", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var outputExtension = NormalizeExtension(Path.GetExtension(outputPath), ".mp4");
            var segmentPaths = new List<string>();

            for (var index = 0; index < clips.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var clip = clips[index];
                var segmentPath = Path.Combine(tempDirectory, $"segment_{index:000}{outputExtension}");
                progress?.Report(string.Format(text.CreatingSegment, index + 1, clips.Count));

                await CreateSegmentAsync(
                    ffmpegPath,
                    inputPath,
                    segmentPath,
                    clip,
                    mode,
                    cancellationToken);

                segmentPaths.Add(segmentPath);
            }

            progress?.Report(text.MergingSegments);
            var listPath = Path.Combine(tempDirectory, "segments.txt");
            await File.WriteAllLinesAsync(
                listPath,
                segmentPaths.Select(path => $"file '{EscapeConcatPath(path)}'"),
                cancellationToken);

            await RunFfmpegAsync(
                ffmpegPath,
                new[]
                {
                    "-y",
                    "-f", "concat",
                    "-safe", "0",
                    "-i", listPath,
                    "-c", "copy",
                    outputPath
                },
                cancellationToken);

            progress?.Report(text.Complete);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static async Task CreateSegmentAsync(
        string ffmpegPath,
        string inputPath,
        string segmentPath,
        ClipSegment clip,
        RenderMode mode,
        CancellationToken cancellationToken)
    {
        var duration = clip.Duration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var start = clip.Start.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        var arguments = new List<string>
        {
            "-y",
            "-ss", start,
            "-i", inputPath,
            "-t", duration,
            "-map", "0:v:0?",
            "-map", "0:a?",
            "-sn"
        };

        if (mode == RenderMode.StreamCopy)
        {
            arguments.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero" });
        }
        else
        {
            arguments.AddRange(new[]
            {
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "20",
                "-c:a", "aac",
                "-b:a", "192k"
            });

            if (UsesMovContainer(segmentPath))
            {
                arguments.AddRange(new[] { "-movflags", "+faststart" });
            }
        }

        arguments.Add(segmentPath);
        await RunFfmpegAsync(ffmpegPath, arguments, cancellationToken);
    }

    private static async Task RunFfmpegAsync(
        string ffmpegPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.CreateNoWindow = true;

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var standardError = await standardErrorTask;
        var standardOutput = await standardOutputTask;

        if (process.ExitCode == 0)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        throw new InvalidOperationException(TrimForDialog(message));
    }

    private static string NormalizeExtension(string? extension, string fallback)
    {
        return string.IsNullOrWhiteSpace(extension) ? fallback : extension;
    }

    private static bool UsesMovContainer(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private static string TrimForDialog(string value)
    {
        const int maxLength = 4000;
        return value.Length <= maxLength ? value : value[^maxLength..];
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
            // Temporary render files can be cleaned by the OS later if a process still holds them.
        }
    }
}

public sealed class RenderProgressText
{
    public required string NoClips { get; init; }

    public required string CreatingSegment { get; init; }

    public required string MergingSegments { get; init; }

    public required string Complete { get; init; }
}
