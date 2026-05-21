using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using OctoCut.Controls;

namespace OctoCut.Services;

public sealed class TimelinePreviewGenerator
{
    public async Task<TimelinePreviewAssets> GenerateAsync(
        string ffmpegPath,
        string inputPath,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "OctoCut", "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var thumbnails = await GenerateThumbnailsAsync(ffmpegPath, inputPath, duration, tempDirectory, cancellationToken);
            var waveform = await TryGenerateWaveformAsync(ffmpegPath, inputPath, tempDirectory, cancellationToken);
            return new TimelinePreviewAssets(thumbnails, waveform);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static async Task<IReadOnlyList<TimelineThumbnail>> GenerateThumbnailsAsync(
        string ffmpegPath,
        string inputPath,
        TimeSpan duration,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
        {
            return Array.Empty<TimelineThumbnail>();
        }

        var count = Math.Clamp((int)Math.Ceiling(duration.TotalSeconds / 5), 12, 72);
        var interval = Math.Max(duration.TotalSeconds / count, 0.5);
        var pattern = Path.Combine(tempDirectory, "thumb_%03d.jpg");

        await RunFfmpegAsync(
            ffmpegPath,
            new[]
            {
                "-y",
                "-i", inputPath,
                "-vf", $"fps=1/{interval.ToString("0.###", CultureInfo.InvariantCulture)},scale=160:-1",
                "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                "-q:v", "3",
                pattern
            },
            cancellationToken);

        var files = Directory.GetFiles(tempDirectory, "thumb_*.jpg")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var thumbnails = new List<TimelineThumbnail>(files.Length);
        for (var index = 0; index < files.Length; index++)
        {
            var image = LoadBitmap(files[index]);
            var sourceTime = TimeSpan.FromSeconds(Math.Min(duration.TotalSeconds, index * interval));
            thumbnails.Add(new TimelineThumbnail(sourceTime, image));
        }

        return thumbnails;
    }

    private static async Task<BitmapSource?> TryGenerateWaveformAsync(
        string ffmpegPath,
        string inputPath,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(tempDirectory, "waveform.png");

        try
        {
            await RunFfmpegAsync(
                ffmpegPath,
                new[]
                {
                    "-y",
                    "-i", inputPath,
                    "-filter_complex", "aformat=channel_layouts=mono,showwavespic=s=2400x160:colors=2f80ed",
                    "-frames:v", "1",
                    outputPath
                },
                cancellationToken);

            return File.Exists(outputPath) ? LoadBitmap(outputPath) : null;
        }
        catch
        {
            return null;
        }
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
        await process.WaitForExitAsync(cancellationToken);
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(standardError);
        }
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
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
            // Preview images are loaded into memory. Leftover temp files are safe to clean later.
        }
    }
}
