using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Woi.Core;

namespace Woi.Monitor;

public sealed class WindowsSampler : IRawSampler
{
    public static int LogicalProcessorCount => Native.GetActiveProcessorCount(ushort.MaxValue);

    public RawSnapshot Sample()
    {
        var processes = new List<RawProcess>();
        var gaps = new List<ObservationGap>();
        var unreadable = 0;
        var all = Process.GetProcesses();
        try {
            foreach (var process in all) {
                try {
                    var pid = process.Id;
                    using var handle = Native.OpenProcess(0x1000, false, pid);
                    if (handle.IsInvalid ||
                        !Native.GetProcessTimes(handle, out var creation, out _, out var kernel, out var user) ||
                        creation <= 0) {
                        unreadable++;
                        continue;
                    }
                    // Missing optional counters stay unavailable rather than becoming zero.
                    ulong? read = null, write = null;
                    if (Native.GetProcessIoCounters(handle, out var io)) {
                        read = io.ReadTransferCount;
                        write = io.WriteTransferCount;
                    }
                    long? privateBytes = null, workingSet = null;
                    var memory = new Native.ProcessMemoryCountersEx {
                        Size = (uint)Marshal.SizeOf<Native.ProcessMemoryCountersEx>()
                    };
                    if (Native.GetProcessMemoryInfo(handle, ref memory, memory.Size)) {
                        privateBytes = checked((long)memory.PrivateUsage);
                        workingSet = checked((long)memory.WorkingSetSize);
                    }
                    var name = process.ProcessName;
                    var session = process.SessionId;
                    processes.Add(new(pid, session, creation, name, checked(kernel + user),
                        read, write, privateBytes, workingSet));
                } catch (Win32Exception) { unreadable++; }
                catch (InvalidOperationException) { unreadable++; }
                catch (OverflowException) { unreadable++; }
            }
        } finally {
            foreach (var process in all) process.Dispose();
        }
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
        return new(Stopwatch.GetTimestamp(), utc, available, commit, all.Length, unreadable,
            processes, gaps);
    }

    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation,
            out long exit, out long kernel, out long user);
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
