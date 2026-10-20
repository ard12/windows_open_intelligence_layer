namespace Woi.Core;

public record ProcessHistoryQuery(double StartMs, double EndMs, string? FocusId = null, int Offset = 0, int Limit = 250);
public record ProcessHistoryRow(EntityLifetime Entity, int Depth, string Parent, string State, Contributor? Activity);
public record ProcessHistoryTotals(long? CpuSamples, double? CpuMs, long? DiskBytes, long? FileBytes, long? NetworkBytes);
public record ProcessHistoryPage(string TraceHash, double StartMs, double EndMs, EntityLifetime? Focus,
    string? FocusParentId, int TotalLifetimes, int UnresolvedParents, int Offset,
    IReadOnlyList<ProcessHistoryRow> Rows, bool More, ProcessHistoryTotals Totals, ProcessHistoryTotals UnattributedTotals);

// The index describes captured parentage, not execution dependencies. It retains exited lifetimes.
internal sealed class ProcessHistoryIndex
{
    private readonly Dictionary<string, EntityLifetime> processes;
    private readonly Dictionary<string, string?> parents = new(StringComparer.Ordinal);
    private readonly List<(EntityLifetime Entity, int Depth)> ordered = [];
    private readonly Dictionary<string, (int Start, int End)> ranges = new(StringComparer.Ordinal);

    public ProcessHistoryIndex(IReadOnlyList<EntityLifetime> entities, CancellationToken token)
    {
        processes = entities.Where(x => x.Kind == "Process").ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var process in processes.Values) {
            token.ThrowIfCancellationRequested();
            var parent = process.ParentId == null ? null : processes.GetValueOrDefault(process.ParentId);
            parents[process.Id] = parent != null && parent.Id != process.Id && process.StartKnown &&
                parent.StartMs <= process.StartMs && parent.EndMs > process.StartMs ? parent.Id : null;
        }
        // Corrupt/equal-time relationships must not create cycles or disappear from the forest.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in processes.Keys) {
            var path = new List<string>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            string? current = id;
            while (current != null && !visited.Contains(current)) {
                token.ThrowIfCancellationRequested();
                if (positions.TryGetValue(current, out var cycleStart)) {
                    foreach (var cyclic in path.Skip(cycleStart)) parents[cyclic] = null;
                    break;
                }
                positions[current] = path.Count; path.Add(current); current = parents[current];
            }
            visited.UnionWith(path);
        }
        var children = processes.Values.GroupBy(x => parents[x.Id] ?? "")
            .ToDictionary(x => x.Key, x => x.OrderBy(p => p.StartMs).ThenBy(p => p.OsId)
                .ThenBy(p => p.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var pending = new Stack<(EntityLifetime Entity, int Depth, bool Exit)>();
        foreach (var root in children.GetValueOrDefault("")?.Reverse() ?? []) pending.Push((root, 0, false));
        while (pending.TryPop(out var item)) {
            token.ThrowIfCancellationRequested();
            if (item.Exit) { ranges[item.Entity.Id] = (ranges[item.Entity.Id].Start, ordered.Count); continue; }
            ranges[item.Entity.Id] = (ordered.Count, ordered.Count);
            ordered.Add((item.Entity, item.Depth));
            pending.Push((item.Entity, item.Depth, true));
            foreach (var child in children.GetValueOrDefault(item.Entity.Id)?.Reverse() ?? [])
                pending.Push((child, item.Depth + 1, false));
        }
    }

    public void Validate(ProcessHistoryQuery query)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 500)
            throw new WoiException("InvalidPage", "Choose a nonnegative offset and a page size from 1 to 500.");
        if (query.FocusId != null && !processes.ContainsKey(query.FocusId))
            throw new WoiException("EntityMissing", "The requested process lifetime is unavailable in this trace.");
    }

    public ProcessHistoryPage Page(string traceHash, ProcessHistoryQuery query,
        IReadOnlyList<Contributor> contributors, CancellationToken token)
    {
        Validate(query);
        var range = query.FocusId == null ? (Start: 0, End: ordered.Count) : ranges[query.FocusId];
        var count = range.End - range.Start;
        var first = range.Start + Math.Min(query.Offset, count);
        var end = first + Math.Min(query.Limit, range.End - first);
        var depth = query.FocusId == null ? 0 : ordered[range.Start].Depth;
        var activity = contributors.Where(x => x.ProcessId != null).ToDictionary(x => x.ProcessId!, StringComparer.Ordinal);
        List<ProcessHistoryRow> rows = [];
        for (int i = first; i < end; i++) {
            token.ThrowIfCancellationRequested();
            var (entity, level) = ordered[i];
            var parentId = parents[entity.Id];
            rows.Add(new(entity, level - depth,
                parentId == null ? "Unresolved / not captured" : processes[parentId].DisplayLabel,
                State(entity, query), activity.GetValueOrDefault(entity.Id)));
        }
        List<Contributor> selected = [];
        int unresolved = 0;
        for (int i = range.Start; i < range.End; i++) {
            token.ThrowIfCancellationRequested();
            var id = ordered[i].Entity.Id;
            if (parents[id] == null) unresolved++;
            if (activity.TryGetValue(id, out var value)) selected.Add(value);
        }
        return new(traceHash, query.StartMs, query.EndMs,
            query.FocusId == null ? null : processes[query.FocusId],
            query.FocusId == null ? null : parents[query.FocusId], count, unresolved, query.Offset,
            rows, end < range.End, Totals(selected),
            Totals(contributors.Where(x => x.ProcessId == null || !processes.ContainsKey(x.ProcessId)).ToArray()));
    }

    private static ProcessHistoryTotals Totals(IReadOnlyList<Contributor> values)
    {
        long? Sum(Func<Contributor, long?> metric) => values.Any(x => metric(x) != null)
            ? values.Sum(x => metric(x) ?? 0) : null;
        var cpu = values.Where(x => x.CpuSamples > 0).ToArray();
        return new(Sum(x => x.CpuSamples),
            cpu.Length > 0 && cpu.All(x => x.CpuMs != null) ? cpu.Sum(x => x.CpuMs!.Value) : null,
            Sum(x => x.DiskBytes), Sum(x => x.FileBytes), Sum(x => x.NetworkBytes));
    }

    private static string State(EntityLifetime entity, ProcessHistoryQuery query)
    {
        if (entity.StartMs >= query.EndMs) return "Starts after interval";
        if (entity.EndMs <= query.StartMs) return entity.EndKnown ? "Exited before interval" : "Boundary before interval";
        if (entity.EndKnown && entity.EndMs <= query.EndMs) return "Exit recorded in interval";
        return entity.StartKnown && entity.EndKnown ? "Overlaps interval" : "Overlaps; boundary incomplete";
    }
}
