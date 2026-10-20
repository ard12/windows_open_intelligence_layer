using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Woi.Core;

namespace Woi.Trace;

public sealed partial class EtlReader
{
    public CpuStackPage QueryCpuStacks(CpuStackQuery query, CancellationToken token)
    {
        CpuStackAggregation.Validate(query);
        if (!double.IsFinite(query.StartMs) || !double.IsFinite(query.EndMs) || query.StartMs < 0 || query.EndMs <= query.StartMs || query.StartMs >= Session.DurationMs)
            throw new WoiException("InvalidInterval", "Choose a finite recorded interval with end after start.");
        if (query.ProcessId != null && !Entities.Any(x => x.Kind == "Process" && x.Id == query.ProcessId))
            throw new WoiException("EntityMissing", "The requested process lifetime is unavailable.");
        if (query.ThreadId != null) {
            var thread = Entities.FirstOrDefault(x => x.Kind == "Thread" && x.Id == query.ThreadId)
                ?? throw new WoiException("EntityMissing", "The requested thread lifetime is unavailable.");
            if (query.ProcessId != null && thread.OwnerId != query.ProcessId)
                throw new WoiException("IncompatibleScope", "Choose a thread belonging to the selected process lifetime.");
        }
        query = query with { EndMs = Math.Min(query.EndMs, Session.DurationMs) };
        return CpuStackAggregation.Build(Session.SourceHash, query, ReadCpuStacks(new(query.StartMs, query.EndMs, query.ProcessId), token, query.ThreadId), token);
    }
    public IEnumerable<CpuStackSample> ReadCpuStacks(IntervalQuery query, CancellationToken token, string? threadId = null)
    {
        foreach (var data in log.Events.FilterByTime(query.StartMs, query.EndMs)) {
            token.ThrowIfCancellationRequested();
            if (data is not SampledProfileTraceData || data.TimeStampRelativeMSec < query.StartMs || data.TimeStampRelativeMSec >= query.EndMs) continue;
            var value = Normalize(data, resolveTargets: false);
            if (value == null || (query.ProcessId != null && value.ProcessId != query.ProcessId)) continue;
            // Retain all-thread attribution totals without decoding other threads' frames.
            if (threadId != null && value.ThreadId != threadId) { yield return new(value, []); continue; }
            List<CpuStackFrame> frames = [];
            var stack = data.CallStack();
            for (; stack != null && frames.Count < CpuStackAggregation.MaximumFrames; stack = stack.Caller) {
                token.ThrowIfCancellationRequested();
                var address = stack.CodeAddress;
                var module = address.ModuleFile;
                var moduleKey = module != null ? "module:" + (int)module.ModuleFileIndex : "unknown-module:" + (value.ProcessId ?? "unattributed");
                var moduleName = module?.Name ?? "[Unknown module]";
                var methodName = address.FullMethodName;
                bool resolved = !string.IsNullOrEmpty(methodName);
                var key = resolved && address.Method != null ? "method:" + (int)address.Method.MethodIndex :
                    "address:" + (value.ProcessId ?? "unattributed") + ":" + (int)address.CodeAddressIndex;
                var name = resolved ? moduleName + "!" + methodName : module != null && address.Address >= module.ImageBase ?
                    $"{moduleName}+0x{address.Address - module.ImageBase:x} [unresolved]" : $"0x{address.Address:x} [unresolved]";
                frames.Add(new(key, name.Length > 1024 ? name[..1024] + "…" : name, moduleKey, moduleName, resolved));
            }
            yield return new(value, frames, stack != null);
        }
    }
}
