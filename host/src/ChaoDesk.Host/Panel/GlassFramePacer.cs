using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChaoDesk.Host.Panel;

// Process-local high-resolution waits: no timeBeginPeriod/global timer-resolution change.
internal sealed class GlassFramePacer : WaitHandle
{
    private readonly WaitHandle[] _waits;
    internal GlassFramePacer(WaitHandle stop, WaitHandle wake)
    {
        IntPtr timer=CreateWaitableTimerEx(IntPtr.Zero,null,2,0x1F0003);
        if(timer==IntPtr.Zero) timer=CreateWaitableTimerEx(IntPtr.Zero,null,0,0x1F0003);
        if(timer==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        SafeWaitHandle=new SafeWaitHandle(timer,true);
        _waits=[stop,wake,this];
    }
    internal bool Wait(double milliseconds)
    {
        long due=-(long)Math.Max(1,milliseconds*10000);
        if(!SetWaitableTimer(SafeWaitHandle,ref due,0,IntPtr.Zero,IntPtr.Zero,false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return WaitAny(_waits)==0;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer,ref long due,int period,IntPtr completion,IntPtr state,[MarshalAs(UnmanagedType.Bool)] bool resume);
}
