using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OctoCut.Services;

public sealed class FramePreviewRenderer
{
    public async Task<BitmapSource> RenderFrameAsync(
        string ffmpegPath,
        string inputPath,
        TimeSpan sourceTime,
        CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "OctoCut", "frames");
        Directory.CreateDirectory(tempDirectory);

        var outputPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.jpg");

        try
        {
            await RunFfmpegAsync(
                ffmpegPath,
                new[]
                {
                    "-y",
                    "-ss", sourceTime.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", inputPath,
                    "-frames:v", "1",
                    "-q:v", "2",
                    "-an",
                    outputPath
                },
                cancellationToken);

            return LoadBitmap(outputPath);
        }
        finally
        {
            TryDeleteFile(outputPath);
        }
    }

    public async Task<BitmapSource> CaptureFrameAsync(
        string ffmpegPath,
        string inputPath,
        TimeSpan sourceTime,
        CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "OctoCut", "captures");
        Directory.CreateDirectory(tempDirectory);

        var outputPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.png");

        try
        {
            await SaveFrameAsync(ffmpegPath, inputPath, sourceTime, outputPath, cancellationToken);
            return LoadBitmap(outputPath);
        }
        finally
        {
            TryDeleteFile(outputPath);
        }
    }

    public async Task<BitmapSource> RenderTransitionFrameAsync(
        string ffmpegPath,
        string firstInputPath,
        TimeSpan firstSourceTime,
        string secondInputPath,
        TimeSpan secondSourceTime,
        double amount,
        CancellationToken cancellationToken)
    {
        var firstFrameTask = RenderFrameAsync(ffmpegPath, firstInputPath, firstSourceTime, cancellationToken);
        var secondFrameTask = RenderFrameAsync(ffmpegPath, secondInputPath, secondSourceTime, cancellationToken);

        await Task.WhenAll(firstFrameTask, secondFrameTask);
        return BlendFrames(firstFrameTask.Result, secondFrameTask.Result, amount);
    }

    public Task SaveFrameAsync(
        string ffmpegPath,
        string inputPath,
        TimeSpan sourceTime,
        string outputPath,
        CancellationToken cancellationToken)
    {
        return RunFfmpegAsync(
            ffmpegPath,
            new[]
            {
                "-y",
                "-i", inputPath,
                "-ss", sourceTime.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-frames:v", "1",
                "-an",
                "-f", "image2",
                "-update", "1",
                outputPath
            },
            cancellationToken);
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

        try
        {
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var standardError = await standardErrorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(standardError);
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
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

    private static BitmapSource BlendFrames(BitmapSource firstFrame, BitmapSource secondFrame, double amount)
    {
        var opacity = Math.Clamp(amount, 0, 1);
        var width = Math.Max(1, firstFrame.PixelWidth);
        var height = Math.Max(1, firstFrame.PixelHeight);
        var dpiX = firstFrame.DpiX > 0 ? firstFrame.DpiX : 96;
        var dpiY = firstFrame.DpiY > 0 ? firstFrame.DpiY : 96;
        var rect = new Rect(0, 0, width, height);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(firstFrame, rect);
            context.PushOpacity(opacity);
            context.DrawImage(secondFrame, rect);
            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, dpiX, dpiY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between cancellation and kill.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A locked temp frame can be cleaned later.
        }
    }
}
