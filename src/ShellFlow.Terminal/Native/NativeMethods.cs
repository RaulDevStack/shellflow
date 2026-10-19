using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShellFlow.Terminal.Native;

/// <summary>Win32 declarations for ConPTY, process creation and job objects.</summary>
internal static unsafe partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint CREATE_SUSPENDED = 0x00000004;
    internal const int STARTF_USESTDHANDLES = 0x00000100;
    internal const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    internal const int JobObjectBasicAccountingInformation = 1;
    internal const uint INFINITE = 0xFFFFFFFF;
    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint STILL_ACTIVE = 259;
    internal const int S_OK = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD
    {
        public short X;
        public short Y;

        public COORD(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [LibraryImport(Kernel32)]
    internal static partial int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [LibraryImport(Kernel32)]
    internal static partial int ResizePseudoConsole(IntPtr hPC, COORD size);

    [LibraryImport(Kernel32)]
    internal static partial void ClosePseudoConsole(IntPtr hPC);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref nint lpSize);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, nint cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [LibraryImport(Kernel32)]
    internal static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport(Kernel32, EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcess(
        string? lpApplicationName,
        char* lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        char* lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial uint ResumeThread(IntPtr hThread);

    /// <summary>With a null handler, FALSE clears this process's inherited "ignore Ctrl+C" flag (children inherit it).</summary>
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleCtrlHandler(IntPtr handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [LibraryImport(Kernel32, EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(SafeJobHandle hJob, SafeProcessHandle hProcess);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryInformationJobObject(SafeJobHandle hJob, int jobObjectInformationClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION lpJobObjectInformation, int cbJobObjectInformationLength, IntPtr lpReturnLength);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);
}

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

/// <summary>Owns an HPCON. Releasing it closes the pseudo console, which signals attached console clients.</summary>
internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePseudoConsoleHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ClosePseudoConsole(handle);
        return true;
    }
}

/// <summary>Lets the thread pool wait on a raw process handle without taking ownership of it.</summary>
internal sealed class ProcessWaitHandle : WaitHandle
{
    public ProcessWaitHandle(SafeProcessHandle process)
    {
        SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
