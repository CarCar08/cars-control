using System.Runtime.InteropServices;

namespace CarsControl.Core.Util;

/// <summary>Keep explicitly acquired COM interfaces on their owning apartment and release them before returning managed results.</summary>
internal sealed class ComScope : IDisposable
{
    private readonly List<object> _objects = [];
    public dynamic Own(object value)
    {
        if (Marshal.IsComObject(value) && !_objects.Contains(value, ReferenceEqualityComparer.Instance)) _objects.Add(value);
        return value;
    }

    public void Dispose()
    {
        for (var i = _objects.Count - 1; i >= 0; i--)
            Marshal.ReleaseComObject(_objects[i]); // Never FinalRelease: other RCWs may still own their own reference.
        _objects.Clear();
    }
}
