using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OctoCut.Models;

namespace OctoCut.Services;

public sealed class FfmpegRenderer
{
    public async Task RenderAsync(
        string ffmpegPath,
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
            var needsTransitionRender = mode == RenderMode.Encode && HasTransitions(clips);
            var needsNormalizedEncode = mode == RenderMode.Encode;
            var renderSize = needsNormalizedEncode
                ? await ProbeVideoSizeAsync(ffmpegPath, clips[0].SourcePath, cancellationToken)
                : null;
            var segmentPaths = new List<string>();

            for (var index = 0; index < clips.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var clip = clips[index];
                var segmentExtension = mode == RenderMode.Encode ? ".mp4" : outputExtension;
                var segmentPath = Path.Combine(tempDirectory, $"segment_{index:000}{segmentExtension}");
                var useSilentAudio = mode == RenderMode.Encode && !await HasAudioStreamAsync(ffmpegPath, clip.SourcePath, cancellationToken);
                progress?.Report(string.Format(text.CreatingSegment, index + 1, clips.Count));

                await CreateSegmentAsync(
                    ffmpegPath,
                    segmentPath,
                    clip,
                    mode,
                    renderSize,
                    useSilentAudio,
                    cancellationToken);

                segmentPaths.Add(segmentPath);
            }

            if (needsTransitionRender)
            {
                progress?.Report(text.MergingSegments);
                await CreateTransitionRenderAsync(ffmpegPath, segmentPaths, clips, outputPath, cancellationToken);
                progress?.Report(text.Complete);
                return;
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
        string segmentPath,
        ClipSegment clip,
        RenderMode mode,
        VideoRenderSize? renderSize,
        bool useSilentAudio,
        CancellationToken cancellationToken)
    {
        var duration = FormatSeconds(clip.Duration);
        var start = FormatSeconds(clip.Start);

        var arguments = new List<string> { "-y" };

        if (mode == RenderMode.StreamCopy)
        {
            arguments.AddRange(new[]
            {
                "-ss", start,
                "-i", clip.SourcePath,
                "-t", duration,
                "-map", "0:v:0?",
                "-map", "0:a?",
                "-sn"
            });
            arguments.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero" });
        }
        else
        {
            arguments.AddRange(new[] { "-i", clip.SourcePath });
            if (useSilentAudio)
            {
                arguments.AddRange(new[]
                {
                    "-f", "lavfi",
                    "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"
                });
            }

            arguments.AddRange(new[]
            {
                "-ss", start,
                "-t", duration,
                "-map", "0:v:0?",
                "-sn"
            });

            arguments.AddRange(useSilentAudio
                ? new[] { "-map", "1:a:0" }
                : new[] { "-map", "0:a:0?" });

            if (renderSize is not null)
            {
                arguments.AddRange(new[]
                {
                    "-vf",
                    $"scale={renderSize.Width}:{renderSize.Height}:force_original_aspect_ratio=decrease,pad={renderSize.Width}:{renderSize.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p"
                });
            }

            arguments.AddRange(new[]
            {
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "20",
                "-c:a", "aac",
                "-ac", "2",
                "-ar", "48000",
                "-b:a", "192k"
            });

            if (useSilentAudio)
            {
                arguments.Add("-shortest");
            }

            if (UsesMovContainer(segmentPath))
            {
                arguments.AddRange(new[] { "-movflags", "+faststart" });
            }
        }

        arguments.Add(segmentPath);
        await RunFfmpegAsync(ffmpegPath, arguments, cancellationToken);
    }

    private static async Task CreateTransitionRenderAsync(
        string ffmpegPath,
        IReadOnlyList<string> segmentPaths,
        IReadOnlyList<ClipSegment> clips,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-y" };
        foreach (var segmentPath in segmentPaths)
        {
            arguments.AddRange(new[] { "-i", segmentPath });
        }

        var filter = new StringBuilder();
        for (var index = 0; index < segmentPaths.Count; index++)
        {
            filter.Append(CultureInfo.InvariantCulture, $"[{index}:v]setpts=PTS-STARTPTS[v{index}];");
            filter.Append(CultureInfo.InvariantCulture, $"[{index}:a]asetpts=PTS-STARTPTS[a{index}];");
        }

        var videoLabel = "v0";
        var audioLabel = "a0";
        var accumulatedDuration = clips[0].Duration;

        for (var index = 1; index < clips.Count; index++)
        {
            var transition = ClampFilterTransition(clips[index].TransitionInDuration, accumulatedDuration, clips[index].Duration);
            var nextVideoLabel = $"vx{index}";
            var nextAudioLabel = $"ax{index}";

            if (transition <= TimeSpan.Zero)
            {
                filter.Append(CultureInfo.InvariantCulture, $"[{videoLabel}][v{index}]concat=n=2:v=1:a=0[{nextVideoLabel}];");
                filter.Append(CultureInfo.InvariantCulture, $"[{audioLabel}][a{index}]concat=n=2:v=0:a=1[{nextAudioLabel}];");
                accumulatedDuration += clips[index].Duration;
            }
            else
            {
                var offset = accumulatedDuration - transition;
                if (offset < TimeSpan.Zero)
                {
                    offset = TimeSpan.Zero;
                }

                filter.Append(CultureInfo.InvariantCulture, $"[{videoLabel}][v{index}]xfade=transition=fade:duration={FormatSeconds(transition)}:offset={FormatSeconds(offset)}[{nextVideoLabel}];");
                filter.Append(CultureInfo.InvariantCulture, $"[{audioLabel}][a{index}]acrossfade=d={FormatSeconds(transition)}:c1=tri:c2=tri[{nextAudioLabel}];");
                accumulatedDuration += clips[index].Duration - transition;
            }

            videoLabel = nextVideoLabel;
            audioLabel = nextAudioLabel;
        }

        arguments.AddRange(new[]
        {
            "-filter_complex", filter.ToString(),
            "-map", $"[{videoLabel}]",
            "-map", $"[{audioLabel}]",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "20",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "192k"
        });

        if (UsesMovContainer(outputPath))
        {
            arguments.AddRange(new[] { "-movflags", "+faststart" });
        }

        arguments.Add(outputPath);
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

    private static bool HasTransitions(IReadOnlyList<ClipSegment> clips)
    {
        return clips.Skip(1).Any(clip => clip.TransitionInDuration > TimeSpan.Zero);
    }

    private static TimeSpan ClampFilterTransition(TimeSpan requested, TimeSpan accumulatedDuration, TimeSpan nextDuration)
    {
        if (requested <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var maxTicks = Math.Min(accumulatedDuration.Ticks, nextDuration.Ticks) - TimeSpan.FromMilliseconds(50).Ticks;
        if (maxTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        return requested.Ticks > maxTicks ? TimeSpan.FromTicks(maxTicks) : requested;
    }

    private static async Task<bool> HasAudioStreamAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken)
    {
        var output = await RunFfmpegProbeAsync(ffmpegPath, inputPath, cancellationToken);
        return output.Contains("Audio:", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<VideoRenderSize?> ProbeVideoSizeAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken)
    {
        var output = await RunFfmpegProbeAsync(ffmpegPath, inputPath, cancellationToken);
        var match = Regex.Match(output, @"Video:.*?,\s*(?<width>\d{2,5})x(?<height>\d{2,5})", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var width = int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture);
        width = Math.Max(2, width / 2 * 2);
        height = Math.Max(2, height / 2 * 2);
        return new VideoRenderSize(width, height);
    }

    private static async Task<string> RunFfmpegProbeAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(inputPath);

        process.Start();

        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return await standardErrorTask + await standardOutputTask;
    }

    private static string FormatSeconds(TimeSpan value)
    {
        return value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
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

internal sealed record VideoRenderSize(int Width, int Height);

public sealed class RenderProgressText
{
    public required string NoClips { get; init; }

    public required string CreatingSegment { get; init; }

    public required string MergingSegments { get; init; }

    public required string Complete { get; init; }
}
