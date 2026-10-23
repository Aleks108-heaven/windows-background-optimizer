using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BgOptimizer.Services;

/// <summary>
/// A Windows Performance Data Helper (PDH) query: the cheap way to read performance counters. The "formatted
/// performance data" WMI classes the app used before take 0.3-0.5 s of wall time and a few hundred milliseconds
/// of CPU per call (measured on a fast desktop-class laptop CPU; far worse on an old dual-core), which on a weak
/// laptop is a constant drain when polled every few seconds. A PDH query keeps its previous sample, so each
/// reading costs about 5 ms. Counters are added by their English path (PdhAddEnglishCounter), so the same code
/// works on every Windows language, unlike the localized PerformanceCounter category names.
/// The query handle is a SafeHandle, every call is serialized by one lock (a sampler runs on a worker thread
/// while the window may dispose it on exit), and the buffers PDH fills are bounds-checked before they are read.
/// pdh.dll is resolved from System32 only (on each import below), never from the application folder or the current
/// directory, so a planted copy of it could not be loaded into this elevated process.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint MoreData = 0x800007D2;
    private const uint ValidData = 0, NewData = 1; // PDH_CSTATUS_VALID_DATA, PDH_CSTATUS_NEW_DATA

    internal sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public QueryHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out QueryHandle query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhAddEnglishCounterW(QueryHandle query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCollectQueryData(QueryHandle query);

    [DllImport("pdh.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    private readonly object _gate = new();
    private readonly QueryHandle _handle;
    private bool _disposed;

    private PdhQuery(QueryHandle handle) => _handle = handle;

    /// <summary>Null when PDH is unavailable (for example corrupted performance counters); callers then report "unavailable".</summary>
    public static PdhQuery? TryOpen()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out var handle) != 0 || handle.IsInvalid)
            {
                handle?.Dispose();
                return null;
            }
            return new PdhQuery(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Adds a counter by its English path, wildcards allowed (they follow new instances on later samples). Null if this PC has no such counter.</summary>
    public PdhCounter? AddCounter(string englishPath)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            return PdhAddEnglishCounterW(_handle, englishPath, IntPtr.Zero, out var counter) == 0
                ? new PdhCounter(this, counter)
                : null;
        }
    }

    /// <summary>Takes one sample of every counter. Rates need two samples, so the first reading after the first Collect is empty.</summary>
    public bool Collect()
    {
        lock (_gate) return !_disposed && PdhCollectQueryData(_handle) == 0;
    }

    internal double? ReadValue(IntPtr counter)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (PdhGetFormattedCounterValue(counter, FormatDouble, out _, out var v) != 0) return null;
            return IsUsable(v) ? v.Value : null;
        }
    }

    internal IReadOnlyList<(string Instance, double Value)> ReadArray(IntPtr counter)
    {
        lock (_gate)
        {
            if (_disposed) return Array.Empty<(string, double)>();

            uint size = 0, count = 0;
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble, ref size, ref count, IntPtr.Zero) != MoreData || size == 0)
                return Array.Empty<(string, double)>();

            // Instances can appear between the size query and the read (a program starting), which asks for more room.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal((nint)size);
                try
                {
                    var status = PdhGetFormattedCounterArrayW(counter, FormatDouble, ref size, ref count, buffer);
                    if (status == MoreData) continue; // size now holds the larger requirement
                    return status == 0 ? Parse(buffer, size, count) : Array.Empty<(string, double)>();
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return Array.Empty<(string, double)>();
        }
    }

    // The item array and the instance-name strings share one buffer; nothing outside it is dereferenced.
    private static List<(string, double)> Parse(IntPtr buffer, uint size, uint count)
    {
        var items = new List<(string, double)>();
        var itemSize = Marshal.SizeOf<CounterValueItem>();
        if ((long)count * itemSize > size) return items;

        var start = (long)buffer;
        var end = start + size;
        for (var i = 0; i < count; i++)
        {
            var item = Marshal.PtrToStructure<CounterValueItem>(buffer + i * itemSize);
            if (!IsUsable(item.Value)) continue;
            var name = (long)item.Name;
            if (name < start || name >= end) continue;
            items.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Value));
        }
        return items;
    }

    private static bool IsUsable(CounterValue v) =>
        (v.Status == ValidData || v.Status == NewData) && double.IsFinite(v.Value);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose(); // closing the query also closes its counters
        }
    }
}

/// <summary>One counter inside a <see cref="PdhQuery"/>; it is owned by the query and invalid once the query is disposed.</summary>
internal sealed class PdhCounter
{
    private readonly PdhQuery _query;
    private readonly IntPtr _handle;

    internal PdhCounter(PdhQuery query, IntPtr handle)
    {
        _query = query;
        _handle = handle;
    }

    /// <summary>The value of a single-instance counter, or null when it has none yet.</summary>
    public double? Value() => _query.ReadValue(_handle);

    /// <summary>Every instance of a multi-instance counter with its value (empty when there is nothing yet).</summary>
    public IReadOnlyList<(string Instance, double Value)> Values() => _query.ReadArray(_handle);
}
