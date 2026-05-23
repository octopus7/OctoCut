using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OctoCut.Services;

public sealed class FramePreviewRenderer
{
    private const int MaxCachedPreviewCount = 100;
    private const long MaxCachedPreviewBytes = 256L * 1024 * 1024;

    private readonly PreviewFrameCache _previewCache = new(MaxCachedPreviewCount, MaxCachedPreviewBytes);

    public async Task<BitmapSource> RenderFrameAsync(
        string ffmpegPath,
        string inputPath,
        TimeSpan sourceTime,
        int maxPixelWidth,
        int maxPixelHeight,
        CancellationToken cancellationToken)
    {
        var previewSize = NormalizePreviewSize(maxPixelWidth, maxPixelHeight);
        var cacheKey = PreviewCacheKey.Frame(inputPath, sourceTime, previewSize.Width, previewSize.Height);
        if (_previewCache.TryGet(cacheKey, out var cachedFrame))
        {
            return cachedFrame;
        }

        var frame = await RenderFrameCoreAsync(
            ffmpegPath,
            inputPath,
            sourceTime,
            previewSize.Width,
            previewSize.Height,
            cancellationToken);
        _previewCache.Set(cacheKey, frame);
        return frame;
    }

    private async Task<BitmapSource> RenderFrameCoreAsync(
        string ffmpegPath,
        string inputPath,
        TimeSpan sourceTime,
        int maxPixelWidth,
        int maxPixelHeight,
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
                    "-i", inputPath,
                    "-ss", sourceTime.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    "-frames:v", "1",
                    "-vf", $"scale={maxPixelWidth}:{maxPixelHeight}:force_original_aspect_ratio=decrease,pad={maxPixelWidth}:{maxPixelHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1",
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
        int maxPixelWidth,
        int maxPixelHeight,
        CancellationToken cancellationToken)
    {
        var previewSize = NormalizePreviewSize(maxPixelWidth, maxPixelHeight);
        var normalizedAmount = Math.Clamp(amount, 0, 1);
        var cacheKey = PreviewCacheKey.Transition(
            firstInputPath,
            firstSourceTime,
            secondInputPath,
            secondSourceTime,
            normalizedAmount,
            previewSize.Width,
            previewSize.Height);
        if (_previewCache.TryGet(cacheKey, out var cachedFrame))
        {
            return cachedFrame;
        }

        var firstFrameTask = RenderFrameCoreAsync(
            ffmpegPath,
            firstInputPath,
            firstSourceTime,
            previewSize.Width,
            previewSize.Height,
            cancellationToken);
        var secondFrameTask = RenderFrameCoreAsync(
            ffmpegPath,
            secondInputPath,
            secondSourceTime,
            previewSize.Width,
            previewSize.Height,
            cancellationToken);

        await Task.WhenAll(firstFrameTask, secondFrameTask);
        var frame = BlendFrames(firstFrameTask.Result, secondFrameTask.Result, normalizedAmount);
        _previewCache.Set(cacheKey, frame);
        return frame;
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

    private static (int Width, int Height) NormalizePreviewSize(int width, int height)
    {
        width = width <= 0 ? 960 : width;
        height = height <= 0 ? 540 : height;
        width = Math.Max(2, width / 2 * 2);
        height = Math.Max(2, height / 2 * 2);
        return (width, height);
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

internal static class PreviewCacheKey
{
    public static string Frame(string inputPath, TimeSpan sourceTime, int width, int height)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"frame|{NormalizePath(inputPath)}|{QuantizeTime(sourceTime)}|{width}x{height}");
    }

    public static string Transition(
        string firstInputPath,
        TimeSpan firstSourceTime,
        string secondInputPath,
        TimeSpan secondSourceTime,
        double amount,
        int width,
        int height)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"transition|{NormalizePath(firstInputPath)}|{QuantizeTime(firstSourceTime)}|{NormalizePath(secondInputPath)}|{QuantizeTime(secondSourceTime)}|{QuantizeAmount(amount)}|{width}x{height}");
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).ToUpperInvariant();
    }

    private static long QuantizeTime(TimeSpan time)
    {
        return (long)Math.Round(time.TotalMilliseconds, MidpointRounding.AwayFromZero);
    }

    private static int QuantizeAmount(double amount)
    {
        return (int)Math.Round(Math.Clamp(amount, 0, 1) * 10000, MidpointRounding.AwayFromZero);
    }
}

internal sealed class PreviewFrameCache(int maxCount, long maxBytes)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<PreviewFrameCacheEntry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<PreviewFrameCacheEntry> _lru = new();
    private long _currentBytes;

    public bool TryGet(string key, out BitmapSource frame)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                frame = null!;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            frame = node.Value.Frame;
            return true;
        }
    }

    public void Set(string key, BitmapSource frame)
    {
        var bytes = EstimateBytes(frame);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existingNode))
            {
                _currentBytes -= existingNode.Value.EstimatedBytes;
                _lru.Remove(existingNode);
                _entries.Remove(key);
            }

            var entry = new PreviewFrameCacheEntry(key, frame, bytes);
            var node = new LinkedListNode<PreviewFrameCacheEntry>(entry);
            _lru.AddFirst(node);
            _entries[key] = node;
            _currentBytes += bytes;
            Trim();
        }
    }

    private void Trim()
    {
        while (_entries.Count > maxCount || _currentBytes > maxBytes)
        {
            var last = _lru.Last;
            if (last is null)
            {
                return;
            }

            _lru.RemoveLast();
            _entries.Remove(last.Value.Key);
            _currentBytes -= last.Value.EstimatedBytes;
        }
    }

    private static long EstimateBytes(BitmapSource frame)
    {
        var bitsPerPixel = frame.Format.BitsPerPixel > 0 ? frame.Format.BitsPerPixel : 32;
        var bytesPerPixel = Math.Max(4, (bitsPerPixel + 7) / 8);
        return (long)Math.Max(1, frame.PixelWidth) * Math.Max(1, frame.PixelHeight) * bytesPerPixel;
    }
}

internal sealed record PreviewFrameCacheEntry(string Key, BitmapSource Frame, long EstimatedBytes);
