using System.IO;
using System.Runtime.InteropServices;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Power;

public sealed record SystemSnapshot(int CpuPercent, int MemoryPercent, double MemoryUsedGb, double MemoryTotalGb,
    int? BatteryPercent, bool Charging, string Drive, double DiskFreeGb, double DiskTotalGb, double UptimeHours);

/// <summary>
/// Cheap system figures for the "Computer status" widget: CPU from GetSystemTimes deltas, memory from
/// GlobalMemoryStatusEx, battery from GetSystemPowerStatus, free space on the system drive.
/// </summary>
public sealed class SystemMonitor
{
    private long _idle, _kernel, _user;
    private bool _primed;

    public SystemSnapshot Sample()
    {
        int cpu = 0;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            if (_primed)
            {
                long total = (kernel - _kernel) + (user - _user), busy = total - (idle - _idle);
                cpu = total > 0 ? (int)Math.Clamp(Math.Round(100.0 * busy / total), 0, 100) : 0;
            }
            (_idle, _kernel, _user, _primed) = (idle, kernel, user, true);
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        double totalGb = 0, usedGb = 0;
        int memPct = 0;
        if (GlobalMemoryStatusEx(ref mem))
        {
            totalGb = mem.ullTotalPhys / 1073741824.0;
            usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
            memPct = (int)mem.dwMemoryLoad;
        }

        int? battery = null;
        bool charging = false;
        if (GetSystemPowerStatus(out var p) && p.BatteryFlag != 128 && p.BatteryLifePercent <= 100)
        {
            battery = p.BatteryLifePercent;
            charging = p.ACLineStatus == 1;
        }

        string drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        double free = 0, size = 0;
        try { var d = new DriveInfo(drive); free = d.AvailableFreeSpace / 1073741824.0; size = d.TotalSize / 1073741824.0; }
        catch (IOException) { }

        return new SystemSnapshot(cpu, memPct, Math.Round(usedGb, 1), Math.Round(totalGb, 1), battery, charging,
            drive.TrimEnd('\\'), Math.Round(free, 1), Math.Round(size, 1), Math.Round(Environment.TickCount64 / 3600000.0, 1));
    }
}
