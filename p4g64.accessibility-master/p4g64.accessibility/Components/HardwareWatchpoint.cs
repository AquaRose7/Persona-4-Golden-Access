using System;
using System.Runtime.InteropServices;
using System.Threading;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// In-mod hardware-watchpoint mechanism for finding the canonical position
/// writer that all our prior static analysis missed. Sets DR0/DR7 on every
/// thread in the process to break on writes to a target address, installs a
/// vectored exception handler that logs RIP + integer registers on each hit.
///
/// Usage: HardwareWatchpoint.Toggle(targetAddr) — call once to arm, again to
/// disarm. The user arms, triggers a known event (e.g. CALL_DUNGEON), and the
/// log captures every instruction that wrote to the watched address during
/// that window. The "spawn-write" — which static analysis kept missing — is
/// the rare hit during the floor reload, distinguishable from the per-frame
/// frame-copy writes by its low count.
/// </summary>
internal static class HardwareWatchpoint
{
    // ── Win32 imports ────────────────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Thread32First(IntPtr h, ref THREADENTRY32 te);

    [DllImport("kernel32.dll")]
    private static extern bool Thread32Next(IntPtr h, ref THREADENTRY32 te);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInherit, uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint SuspendThread(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern bool GetThreadContext(IntPtr h, IntPtr lpCtx);

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadContext(IntPtr h, IntPtr lpCtx);

    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint First, IntPtr handler);

    [DllImport("kernel32.dll")]
    private static extern uint RemoveVectoredExceptionHandler(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    // ── Constants ────────────────────────────────────────────────────────────
    private const uint TH32CS_SNAPTHREAD = 0x4;
    private const uint THREAD_GET_CONTEXT = 0x0008;
    private const uint THREAD_SET_CONTEXT = 0x0010;
    private const uint THREAD_SUSPEND_RESUME = 0x0002;
    private const uint THREAD_QUERY_INFO = 0x0040;
    private const uint THREAD_ACCESS = THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_SUSPEND_RESUME | THREAD_QUERY_INFO;

    private const uint CONTEXT_AMD64 = 0x100000;
    private const uint CONTEXT_DEBUG_REGISTERS = CONTEXT_AMD64 | 0x10;

    // CONTEXT64 field offsets
    private const int CTX_ContextFlags = 0x30;
    private const int CTX_Dr0 = 0x48;
    private const int CTX_Dr6 = 0x68;
    private const int CTX_Dr7 = 0x70;
    private const int CTX_Rax = 0x78;
    private const int CTX_Rcx = 0x80;
    private const int CTX_Rdx = 0x88;
    private const int CTX_Rsi = 0xA8;
    private const int CTX_Rdi = 0xB0;
    private const int CTX_R8 = 0xB8;
    private const int CTX_Rip = 0xF8;
    private const int CONTEXT_SIZE = 0x4D0;  // 1232 bytes incl XMM area

    private const uint EXCEPTION_CONTINUE_SEARCH = 0;
    private const uint EXCEPTION_CONTINUE_EXECUTION = 0xFFFFFFFF;
    private const uint STATUS_SINGLE_STEP = 0x80000004;

    // ── State ─────────────────────────────────────────────────────────────────
    private static IntPtr _vehHandle = IntPtr.Zero;
    private static VectoredExceptionHandlerDelegate? _handler;
    private static IntPtr _handlerPtr = IntPtr.Zero;
    private static long _watchAddr = 0;
    private static int _hitCount = 0;
    private static bool _breakOnRead = false;   // true = break on read OR write (catch draw)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _seenRips = new();
    private static readonly object _lock = new();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint VectoredExceptionHandlerDelegate(IntPtr exceptionInfo);

    public static bool IsActive => _vehHandle != IntPtr.Zero;

    public static void Toggle(long watchAddr) => Toggle(watchAddr, false);

    public static void Toggle(long watchAddr, bool breakOnRead)
    {
        lock (_lock)
        {
            if (IsActive) Disable();
            else { _breakOnRead = breakOnRead; Enable(watchAddr); }
        }
    }

    private static void Enable(long watchAddr)
    {
        if (IsActive) return;
        _watchAddr = watchAddr;
        _hitCount = 0;
        _seenRips.Clear();

        _handler = OnException;
        _handlerPtr = Marshal.GetFunctionPointerForDelegate(_handler);
        _vehHandle = AddVectoredExceptionHandler(1, _handlerPtr);
        if (_vehHandle == IntPtr.Zero)
        {
            Log("[HW] AddVectoredExceptionHandler failed");
            Speech.Say("Watchpoint install failed.", true);
            _handler = null;
            return;
        }

        int armed = SetOrClearBreakpointOnAllThreads(watchAddr, enable: true);
        Log($"[HW] watchpoint armed on {armed} threads, watch=0x{watchAddr:X12}");
        Speech.Say($"Watchpoint enabled on {armed} threads. Trigger a teleport now.", true);
    }

    private static void Disable()
    {
        if (!IsActive) return;
        int cleared = SetOrClearBreakpointOnAllThreads(0, enable: false);
        if (_vehHandle != IntPtr.Zero)
        {
            RemoveVectoredExceptionHandler(_vehHandle);
            _vehHandle = IntPtr.Zero;
        }
        _handler = null;
        _handlerPtr = IntPtr.Zero;
        Log($"[HW] watchpoint cleared on {cleared} threads, total hits={_hitCount}");
        Speech.Say($"Watchpoint disabled. Captured {_hitCount} hits.", true);
    }

    private static int SetOrClearBreakpointOnAllThreads(long watchAddr, bool enable)
    {
        uint pid = GetCurrentProcessId();
        uint myTid = GetCurrentThreadId();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snap == IntPtr.Zero || snap.ToInt64() == -1)
        {
            Log($"[HW] thread snapshot failed: {Marshal.GetLastWin32Error()}");
            return 0;
        }

        var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
        if (!Thread32First(snap, ref te))
        {
            CloseHandle(snap);
            return 0;
        }

        int count = 0;
        do
        {
            if (te.th32OwnerProcessID != pid) continue;
            if (te.th32ThreadID == myTid) continue;

            IntPtr h = OpenThread(THREAD_ACCESS, false, te.th32ThreadID);
            if (h == IntPtr.Zero) continue;

            try
            {
                SuspendThread(h);
                IntPtr ctxBuf = Marshal.AllocHGlobal(CONTEXT_SIZE + 16);
                try
                {
                    for (int i = 0; i < CONTEXT_SIZE; i++) Marshal.WriteByte(ctxBuf, i, 0);
                    Marshal.WriteInt32(ctxBuf, CTX_ContextFlags, (int)CONTEXT_DEBUG_REGISTERS);
                    if (GetThreadContext(h, ctxBuf))
                    {
                        Marshal.WriteInt64(ctxBuf, CTX_Dr0, watchAddr);
                        // Clear Dr1..Dr3 (and Dr6 status)
                        Marshal.WriteInt64(ctxBuf, CTX_Dr0 + 8, 0);
                        Marshal.WriteInt64(ctxBuf, CTX_Dr0 + 16, 0);
                        Marshal.WriteInt64(ctxBuf, CTX_Dr0 + 24, 0);
                        Marshal.WriteInt64(ctxBuf, CTX_Dr6, 0);
                        // DR7: L0 | LE | RW0 | LEN0. RW=11 read/write (1 byte) when
                        // _breakOnRead (catches the per-frame DRAW); else 01 write, 4 bytes.
                        ulong rw = _breakOnRead ? (3UL << 16) : (1UL << 16);
                        ulong len = _breakOnRead ? (0UL << 18) : (3UL << 18);
                        ulong dr7 = enable ? (1UL | (1UL << 8) | rw | len) : 0UL;
                        Marshal.WriteInt64(ctxBuf, CTX_Dr7, (long)dr7);
                        if (SetThreadContext(h, ctxBuf)) count++;
                    }
                }
                finally { Marshal.FreeHGlobal(ctxBuf); }
            }
            finally
            {
                ResumeThread(h);
                CloseHandle(h);
            }
        } while (Thread32Next(snap, ref te));

        CloseHandle(snap);
        return count;
    }

    private static uint OnException(IntPtr exceptionInfo)
    {
        try
        {
            IntPtr recPtr = Marshal.ReadIntPtr(exceptionInfo, 0);
            IntPtr ctxPtr = Marshal.ReadIntPtr(exceptionInfo, 8);
            if (recPtr == IntPtr.Zero || ctxPtr == IntPtr.Zero) return EXCEPTION_CONTINUE_SEARCH;

            uint code = (uint)Marshal.ReadInt32(recPtr, 0);
            if (code != STATUS_SINGLE_STEP) return EXCEPTION_CONTINUE_SEARCH;

            ulong dr6 = (ulong)Marshal.ReadInt64(ctxPtr, CTX_Dr6);
            // Only handle if one of B0..B3 (lowest 4 bits) is set — that's our breakpoint.
            if ((dr6 & 0xFUL) == 0) return EXCEPTION_CONTINUE_SEARCH;

            int n = Interlocked.Increment(ref _hitCount);
            ulong rip = (ulong)Marshal.ReadInt64(ctxPtr, CTX_Rip);
            // The draw fires every frame -> dedupe by RIP so the log stays readable
            // (each distinct accessing instruction logged once, cap 16).
            if (_seenRips.Count < 16 && _seenRips.TryAdd(rip, 0))
            {
                ulong rax = (ulong)Marshal.ReadInt64(ctxPtr, CTX_Rax);
                ulong rcx = (ulong)Marshal.ReadInt64(ctxPtr, CTX_Rcx);
                ulong rdx = (ulong)Marshal.ReadInt64(ctxPtr, CTX_Rdx);
                ulong rsi = (ulong)Marshal.ReadInt64(ctxPtr, CTX_Rsi);
                ulong rdi = (ulong)Marshal.ReadInt64(ctxPtr, CTX_Rdi);
                ulong r8 = (ulong)Marshal.ReadInt64(ctxPtr, CTX_R8);
                ulong baseAddr = (ulong)BaseAddress;
                Log($"[HW HIT {n}] RIP=0x{rip:X} (=P4G+0x{rip - baseAddr:X}) RAX=0x{rax:X} RCX=0x{rcx:X} RDX=0x{rdx:X} RSI=0x{rsi:X} RDI=0x{rdi:X} R8=0x{r8:X}");
            }

            // Clear DR6 status bits — they're sticky.
            Marshal.WriteInt64(ctxPtr, CTX_Dr6, 0);
            return EXCEPTION_CONTINUE_EXECUTION;
        }
        catch
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }
    }
}
