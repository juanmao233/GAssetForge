using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace GAssetForge.App.Converters;

/// <summary>
/// Loads a bounded local preview into memory without holding the source file open.
/// Frozen previews are shared by absolute path, file version and decode constraints.
/// </summary>
public sealed class FilePathToImageSourceConverter : IValueConverter
{
    private const int DefaultDecodeEdge = 320;
    private const int MaximumDecodeEdge = 4096;
    private const long MaximumDecodedPixels = 8L * 1024L * 1024L;
    private const int MaximumCacheEntries = 128;
    private const int MaximumStrongCacheEntries = 24;
    private const long MaximumStrongCachePixels = 16L * 1024L * 1024L;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<PreviewCacheKey, PreviewCacheEntry> Cache = [];
    private static readonly LinkedList<PreviewCacheKey> CacheLru = [];
    private static readonly Dictionary<PreviewCacheKey, Lazy<BitmapSource?>> InFlight = [];
    private static int _strongCacheEntries;
    private static long _strongCachePixels;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var pathKey = OperatingSystem.IsWindows()
                ? fullPath.ToUpperInvariant()
                : fullPath;
            var constraints = ParseConstraints(parameter);

            // A replace can race a conversion. Retry once against the new file
            // version, but never cache or return a preview read from an obsolete path slot.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!TryReadFileVersion(fullPath, out var version))
                {
                    return null;
                }

                var key = new PreviewCacheKey(pathKey, version, constraints);
                RemoveSupersededVersions(pathKey, version);
                if (TryGetCached(key, out var cached))
                {
                    if (TryReadFileVersion(fullPath, out var currentVersion) && currentVersion == version)
                    {
                        return cached;
                    }

                    RemoveCached(key);
                    continue;
                }

                var loader = GetOrCreateLoader(
                    key,
                    () => LoadBoundedBitmap(fullPath, constraints));
                BitmapSource? bitmap;
                try
                {
                    try
                    {
                        bitmap = loader.Value;
                    }
                    catch (Exception)
                    {
                        bitmap = null;
                    }

                    if (!TryReadFileVersion(fullPath, out var loadedVersion) || loadedVersion != version)
                    {
                        continue;
                    }

                    if (bitmap is null)
                    {
                        return null;
                    }

                    AddCached(key, bitmap);
                    return bitmap;
                }
                finally
                {
                    // Keep the shared loader registered until the decoded result
                    // is in Cache, otherwise a racing caller can decode it twice.
                    RemoveLoader(key, loader);
                }
            }
        }
        // Broken, inaccessible or concurrently rewritten image files must not
        // take down the dock pane; the card keeps its neutral placeholder.
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static PreviewDecodeConstraints ParseConstraints(object? parameter)
    {
        var requestedEdge = parameter is string text &&
                            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : DefaultDecodeEdge;
        var edge = Math.Clamp(requestedEdge, 1, MaximumDecodeEdge);
        return new PreviewDecodeConstraints(edge, edge, MaximumDecodedPixels);
    }

    private static BitmapSource? LoadBoundedBitmap(
        string path,
        PreviewDecodeConstraints constraints)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        var sourceWidth = decoder.Frames[0].PixelWidth;
        var sourceHeight = decoder.Frames[0].PixelHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return null;
        }

        var (targetWidth, targetHeight) = CalculateDecodeSize(
            sourceWidth,
            sourceHeight,
            constraints);
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (targetWidth < sourceWidth || targetHeight < sourceHeight)
        {
            // Set only the dominant dimension so WPF preserves aspect ratio.
            if (sourceWidth >= sourceHeight)
            {
                bitmap.DecodePixelWidth = targetWidth;
            }
            else
            {
                bitmap.DecodePixelHeight = targetHeight;
            }
        }

        bitmap.StreamSource = stream;
        bitmap.EndInit();

        var decodedPixels = checked((long)bitmap.PixelWidth * bitmap.PixelHeight);
        if (bitmap.PixelWidth > constraints.MaximumWidth ||
            bitmap.PixelHeight > constraints.MaximumHeight ||
            decodedPixels > constraints.MaximumPixels)
        {
            return null;
        }

        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    private static (int Width, int Height) CalculateDecodeSize(
        int sourceWidth,
        int sourceHeight,
        PreviewDecodeConstraints constraints)
    {
        var scale = Math.Min(
            1d,
            Math.Min(
                (double)constraints.MaximumWidth / sourceWidth,
                (double)constraints.MaximumHeight / sourceHeight));
        var sourcePixels = (double)sourceWidth * sourceHeight;
        if (sourcePixels * scale * scale > constraints.MaximumPixels)
        {
            scale = Math.Min(scale, Math.Sqrt(constraints.MaximumPixels / sourcePixels));
        }

        var width = Math.Max(1, (int)Math.Floor(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Floor(sourceHeight * scale));
        while ((long)width * height > constraints.MaximumPixels)
        {
            if (width >= height && width > 1)
            {
                width--;
            }
            else if (height > 1)
            {
                height--;
            }
            else
            {
                break;
            }
        }

        return (width, height);
    }

    private static bool TryReadFileVersion(string path, out PreviewFileVersion version)
    {
        version = default;
        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists || file.Length <= 0)
            {
                return false;
            }

            version = new PreviewFileVersion(
                file.Length,
                file.LastWriteTimeUtc.Ticks,
                file.CreationTimeUtc.Ticks);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Lazy<BitmapSource?> GetOrCreateLoader(
        PreviewCacheKey key,
        Func<BitmapSource?> factory)
    {
        lock (CacheGate)
        {
            if (InFlight.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = new Lazy<BitmapSource?>(
                factory,
                LazyThreadSafetyMode.ExecutionAndPublication);
            InFlight.Add(key, created);
            return created;
        }
    }

    private static void RemoveLoader(PreviewCacheKey key, Lazy<BitmapSource?> loader)
    {
        lock (CacheGate)
        {
            if (InFlight.TryGetValue(key, out var current) && ReferenceEquals(current, loader))
            {
                InFlight.Remove(key);
            }
        }
    }

    private static bool TryGetCached(PreviewCacheKey key, out BitmapSource? bitmap)
    {
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(key, out var entry) ||
                !entry.WeakReference.TryGetTarget(out bitmap))
            {
                if (entry is not null)
                {
                    RemoveCachedCore(key, entry);
                }

                bitmap = null;
                return false;
            }

            TouchEntry(key, entry, bitmap);
            return true;
        }
    }

    private static void AddCached(PreviewCacheKey key, BitmapSource bitmap)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var existing))
            {
                TouchEntry(key, existing, bitmap);
                return;
            }

            var pixels = checked((long)bitmap.PixelWidth * bitmap.PixelHeight);
            var node = CacheLru.AddLast(key);
            var entry = new PreviewCacheEntry(bitmap, pixels, node);
            Cache.Add(key, entry);
            _strongCacheEntries++;
            _strongCachePixels += pixels;
            TrimCache();
        }
    }

    private static void TouchEntry(
        PreviewCacheKey key,
        PreviewCacheEntry entry,
        BitmapSource bitmap)
    {
        CacheLru.Remove(entry.LruNode);
        entry.LruNode = CacheLru.AddLast(key);
        entry.WeakReference.SetTarget(bitmap);
        if (entry.StrongReference is null)
        {
            entry.StrongReference = bitmap;
            _strongCacheEntries++;
            _strongCachePixels += entry.PixelCount;
        }

        TrimStrongCache();
    }

    private static void RemoveSupersededVersions(string pathKey, PreviewFileVersion currentVersion)
    {
        lock (CacheGate)
        {
            foreach (var pair in Cache
                         .Where(pair =>
                             string.Equals(pair.Key.PathKey, pathKey, StringComparison.Ordinal) &&
                             pair.Key.Version != currentVersion)
                         .ToArray())
            {
                RemoveCachedCore(pair.Key, pair.Value);
            }
        }
    }

    private static void RemoveCached(PreviewCacheKey key)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var entry))
            {
                RemoveCachedCore(key, entry);
            }
        }
    }

    private static void TrimCache()
    {
        while (Cache.Count > MaximumCacheEntries && CacheLru.First is { } oldest)
        {
            var key = oldest.Value;
            if (Cache.TryGetValue(key, out var entry))
            {
                RemoveCachedCore(key, entry);
            }
            else
            {
                CacheLru.RemoveFirst();
            }
        }

        TrimStrongCache();
    }

    private static void TrimStrongCache()
    {
        while (_strongCacheEntries > MaximumStrongCacheEntries ||
               _strongCachePixels > MaximumStrongCachePixels)
        {
            var node = CacheLru.First;
            while (node is not null &&
                   (!Cache.TryGetValue(node.Value, out var entry) || entry.StrongReference is null))
            {
                node = node.Next;
            }

            if (node is null || !Cache.TryGetValue(node.Value, out var candidate))
            {
                break;
            }

            candidate.StrongReference = null;
            _strongCacheEntries--;
            _strongCachePixels -= candidate.PixelCount;
        }
    }

    private static void RemoveCachedCore(PreviewCacheKey key, PreviewCacheEntry entry)
    {
        Cache.Remove(key);
        CacheLru.Remove(entry.LruNode);
        if (entry.StrongReference is not null)
        {
            _strongCacheEntries--;
            _strongCachePixels -= entry.PixelCount;
        }
    }

    private readonly record struct PreviewDecodeConstraints(
        int MaximumWidth,
        int MaximumHeight,
        long MaximumPixels);

    private readonly record struct PreviewFileVersion(
        long Length,
        long LastWriteUtcTicks,
        long CreationUtcTicks);

    private readonly record struct PreviewCacheKey(
        string PathKey,
        PreviewFileVersion Version,
        PreviewDecodeConstraints Constraints);

    private sealed class PreviewCacheEntry
    {
        public PreviewCacheEntry(
            BitmapSource bitmap,
            long pixelCount,
            LinkedListNode<PreviewCacheKey> lruNode)
        {
            WeakReference = new WeakReference<BitmapSource>(bitmap);
            StrongReference = bitmap;
            PixelCount = pixelCount;
            LruNode = lruNode;
        }

        public WeakReference<BitmapSource> WeakReference { get; }

        public BitmapSource? StrongReference { get; set; }

        public long PixelCount { get; }

        public LinkedListNode<PreviewCacheKey> LruNode { get; set; }
    }
}
