using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Woi.Core;

namespace Woi.Monitor;

public sealed class WindowsSampler : IRawSampler
{
    private int[] processIds = new int[4096];
    private readonly char[] imageBuffer = new char[32768];
    public static int LogicalProcessorCount => Native.GetActiveProcessorCount(ushort.MaxValue);

    public RawSnapshot Sample(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var startedTick = Stopwatch.GetTimestamp();
        var processes = new List<RawProcess>();
        var gaps = new List<ObservationGap>();
        var unreadable = 0;
        var imageUnavailable = 0;
        var count = EnumerateProcesses(token);
        for (var index = 0; index < count; index++) {
            token.ThrowIfCancellationRequested();
            var pid = processIds[index];
            try {
                using var handle = Native.OpenProcess(0x1000, false, pid);
                token.ThrowIfCancellationRequested();
                if (handle.IsInvalid ||
                    !Native.GetProcessTimes(handle, out var creation, out _, out var kernel, out var user) ||
                    creation <= 0) {
                    unreadable++;
                    continue;
                }
                // Missing optional counters stay unavailable rather than becoming zero.
                token.ThrowIfCancellationRequested();
                ulong? read = null, write = null;
                if (Native.GetProcessIoCounters(handle, out var io)) {
                    read = io.ReadTransferCount;
                    write = io.WriteTransferCount;
                }
                token.ThrowIfCancellationRequested();
                long? privateBytes = null, workingSet = null;
                var memory = new Native.ProcessMemoryCountersEx {
                    Size = (uint)Marshal.SizeOf<Native.ProcessMemoryCountersEx>()
                };
                if (Native.GetProcessMemoryInfo(handle, ref memory, memory.Size)) {
                    privateBytes = checked((long)memory.PrivateUsage);
                    workingSet = checked((long)memory.WorkingSetSize);
                }
                // Obtain the image from the opened lifetime and bracket the
                // PID-based session lookup with matching creation times.
                token.ThrowIfCancellationRequested();
                if (!Native.ProcessIdToSessionId(pid, out var session) || session > int.MaxValue) {
                    unreadable++;
                    continue;
                }
                token.ThrowIfCancellationRequested();
                using var identityCheck = Native.OpenProcess(0x1000, false, pid);
                token.ThrowIfCancellationRequested();
                if (identityCheck.IsInvalid ||
                    !Native.GetProcessTimes(identityCheck, out var checkedCreation, out _, out _, out _) ||
                    checkedCreation != creation) {
                    unreadable++;
                    continue;
                }
                token.ThrowIfCancellationRequested();
                uint imageLength = (uint)imageBuffer.Length;
                var name = "Image unavailable";
                if (Native.QueryFullProcessImageNameW(handle, 0, imageBuffer, ref imageLength))
                    name = Path.GetFileNameWithoutExtension(new string(imageBuffer, 0, checked((int)imageLength)));
                else imageUnavailable++;
                processes.Add(new(pid, checked((int)session), creation, name, checked(kernel + user),
                    read, write, privateBytes, workingSet));
            } catch (Win32Exception) { unreadable++; }
            catch (InvalidOperationException) { unreadable++; }
            catch (OverflowException) { unreadable++; }
        }

        token.ThrowIfCancellationRequested();
        long? available = null, commit = null;
        var info = new Native.PerformanceInformation { Size = (uint)Marshal.SizeOf<Native.PerformanceInformation>() };
        if (Native.GetPerformanceInfo(ref info, info.Size)) {
            available = checked((long)(info.PhysicalAvailable * info.PageSize));
            commit = checked((long)(info.CommitTotal * info.PageSize));
        } else gaps.Add(new(DateTime.UtcNow, "SystemMemoryUnavailable",
            "Windows did not provide system available-memory or commit counters."));
        var utc = DateTime.UtcNow;
        if (unreadable > 0) gaps.Add(new(utc, "ProcessAccess",
            $"{unreadable} enumerated processes could not be identified or read this tick."));
        if (imageUnavailable > 0) gaps.Add(new(utc, "ProcessImageUnavailable",
            $"{imageUnavailable} identified process lifetimes had no readable image name; their readable counters are retained."));
        token.ThrowIfCancellationRequested();
        var completedTick = Stopwatch.GetTimestamp();
        return new(completedTick, utc, available, commit, count, unreadable,
            processes, gaps, (completedTick - startedTick) * 1000d / Stopwatch.Frequency);
    }

    private int EnumerateProcesses(CancellationToken token)
    {
        while (true) {
            token.ThrowIfCancellationRequested();
            var capacity = checked((uint)(processIds.Length * sizeof(int)));
            if (!Native.EnumProcesses(processIds, capacity, out var needed))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not enumerate process IDs.");
            if (needed % sizeof(int) != 0 || needed > capacity)
                throw new InvalidOperationException("Windows returned an invalid process-ID buffer.");
            if (needed < capacity) return checked((int)(needed / sizeof(int)));
            // A full buffer may be truncated. Grow and retry; never treat it as complete.
            if (processIds.Length >= 131072)
                throw new InvalidOperationException("Process enumeration exceeds the supported 131,072-ID bound.");
            processIds = new int[processIds.Length * 2];
        }
    }

    internal static class Native
    {
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumProcesses([Out] int[] ids, uint bytes, out uint needed);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation,
            out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ProcessIdToSessionId(int pid, out uint sessionId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags,
            [Out] char[] imageName, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessIoCounters(SafeProcessHandle handle, out IoCounters counters);
        [DllImport("kernel32.dll")]
        internal static extern int GetActiveProcessorCount(ushort groupNumber);
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessMemoryInfo(SafeProcessHandle handle,
            ref ProcessMemoryCountersEx counters, uint size);
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessMemoryCountersEx
        {
            public uint Size, PageFaultCount;
            public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage,
                QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage,
                PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct PerformanceInformation
        {
            public uint Size;
            public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
                SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }
    }
}
