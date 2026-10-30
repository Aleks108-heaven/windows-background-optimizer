using System.Runtime.InteropServices;

namespace BgOptimizer.Services;

public readonly record struct SystemStatsSnapshot(double CpuPercent, long UsedMemoryBytes, long TotalMemoryBytes);

/// <summary>
/// System-wide CPU and memory usage via direct kernel32 calls, avoiding the
/// System.Diagnostics.PerformanceCounter package this project otherwise has no
/// need for. CPU% is derived from the idle/kernel/user tick deltas between two
/// calls to GetSystemTimes, so the first Sample() after startup reports 0%.
/// </summary>
public class SystemStats
{
    private (ulong Idle, ulong Kernel, ulong User)? _lastSample;

    public SystemStatsSnapshot Sample()
    {
        double cpuPercent = 0;
        if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            var idle = ToTicks(idleFt);
            var kernel = ToTicks(kernelFt);
            var user = ToTicks(userFt);

            if (_lastSample is { } last)
            {
                var idleDelta = idle - last.Idle;
                var totalDelta = (kernel - last.Kernel) + (user - last.User);
                if (totalDelta > 0)
                    cpuPercent = Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100, 0, 100);
            }
            _lastSample = (idle, kernel, user);
        }

        long usedBytes = 0, totalBytes = 0;
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            totalBytes = (long)status.ullTotalPhys;
            usedBytes = totalBytes - (long)status.ullAvailPhys;
        }

        return new SystemStatsSnapshot(cpuPercent, usedBytes, totalBytes);
    }

    private static ulong ToTicks(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
