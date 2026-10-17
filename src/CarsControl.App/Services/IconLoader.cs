using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CarsControl.Core.Models;

namespace CarsControl.App.Services;

/// <summary>Loads app icons off the UI thread: ARP DisplayIcon via ExtractIconEx, MSIX logos from
/// the package folder (resolving the .scale-NNN / .targetsize-NN variants the manifest omits).</summary>
public sealed class IconLoader
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(4);

    public async Task<ImageSource?> LoadAsync(AppEntry app)
    {
        var path = app.MsixLogoPath ?? app.DisplayIcon;
        if (path is null) return null;
        if (_cache.TryGetValue(path, out var cached)) return cached;

        await _gate.WaitAsync();
        try
        {
            var img = await Task.Run(() => app.MsixLogoPath is not null ? LoadLogo(app.MsixLogoPath) : LoadIcon(path));
            _cache[path] = img;
            return img;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ImageSource? LoadIcon(string path)
    {
        if (!File.Exists(path)) return null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".bmp") return LoadBitmap(path);

        var large = new IntPtr[1];
        try
        {
            if (ExtractIconEx(path, 0, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
        }
    }

    private static ImageSource? LoadLogo(string logoPath)
    {
        var resolved = ResolveScaledAsset(logoPath);
        return resolved is null ? null : LoadBitmap(resolved);
    }

    /// <summary>Manifests reference Assets\StoreLogo.png but ship StoreLogo.scale-100.png etc.</summary>
    internal static string? ResolveScaledAsset(string path)
    {
        if (File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var candidates = Directory.GetFiles(dir, $"{stem}.*{ext}");
        return candidates.FirstOrDefault(c => c.Contains(".scale-100", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(c => c.Contains(".targetsize-32", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(c => c.Contains(".scale-200", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    private static ImageSource? LoadBitmap(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 32;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
