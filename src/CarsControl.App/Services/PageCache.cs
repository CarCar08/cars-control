using System.Runtime;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Services;

/// <summary>
/// Hands pages to the NavigationView. A page is built on its first visit and reused while the window is in use, so switching is
/// instant and keeps scroll position and the open tab. When the window has been in the tray for a while, <see cref="Release"/>
/// drops every page except the one on screen: their visual trees are most of what C.A.R.S. holds in memory, and the view models
/// (the actual data) stay. A page dropped this way is simply built again on its next visit.
/// </summary>
public sealed class PageCache : IServiceProvider
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<Type, Page> _pages = [];
    private readonly List<WeakReference<Page>> _released = [];

    public PageCache(IServiceProvider services) => _services = services;

    public object? GetService(Type serviceType) =>
        typeof(Page).IsAssignableFrom(serviceType) ? Get(serviceType) : _services.GetService(serviceType);

    public T Get<T>() where T : Page => (T)Get(typeof(T));

    public Page Get(Type type)
    {
        if (!_pages.TryGetValue(type, out var page))
        {
            page = (Page)ActivatorUtilities.GetServiceOrCreateInstance(_services, type);
            _pages[type] = page;
        }
        return page;
    }

    /// <summary>Drops every cached page except <paramref name="keep"/>, then compacts the heap once so the memory really goes back.</summary>
    public int Release(Type? keep)
    {
        var dropped = 0;
        foreach (var type in _pages.Keys.Where(t => t != keep).ToList())
        {
            _released.Add(new WeakReference<Page>(_pages[type]));
            _pages.Remove(type);
            dropped++;
        }
        // Collect once the UI is idle: work queued on the dispatcher can still reference a page for a moment.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (dropped > 0)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            _released.RemoveAll(w => !w.TryGetTarget(out _));
            PerfLog.Mark($"pages released: {dropped}; still alive after release (should be 0): {_released.Count}");
            // Hand the now-idle pages back to Windows so games and other apps can use that RAM while C.A.R.S. sits in the
            // tray. Nothing is lost: pages come back on demand when the window is opened. (Most of the remaining ~230 MB is
            // .NET, WPF and Windows' own libraries, shared with other programs.)
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
        });
        return dropped;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
