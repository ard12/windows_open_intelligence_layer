namespace Woi.Core;

public enum CpuStackView { Functions, CallTree, Callers, Callees }
public enum CpuStackGrouping { Function, Module }
public record CpuStackQuery(double StartMs = 0, double EndMs = double.MaxValue, string? ProcessId = null,
    CpuStackView View = CpuStackView.Functions, CpuStackGrouping Grouping = CpuStackGrouping.Function,
    string? FocusKey = null, IReadOnlyList<string>? Path = null, int Offset = 0, int Limit = 200,
    string? ThreadId = null, bool HottestPath = false);
public record CpuStackFrame(string Key, string Name, string ModuleKey, string Module, bool Resolved);
public record CpuStackSample(Observation Event, IReadOnlyList<CpuStackFrame> Frames, bool Truncated = false);
public record CpuStackMetric(long Samples, long WeightedSamples, double KnownCpuMs)
{
    public double? CpuMs => Samples == WeightedSamples ? KnownCpuMs : null;
}
public record CpuStackRow(string Key, string Name, string Module, CpuStackMetric Inclusive, CpuStackMetric Exclusive,
    double SamplePercent, bool HasChildren, EvidenceReference Evidence);
public record CpuStackPage(string SourceHash, CpuStackQuery Query, CpuStackMetric Selected, CpuStackMetric WithStacks,
    CpuStackMetric WithoutStacks, long UnresolvedSamples, long TruncatedSamples, CpuStackMetric Context,
    CpuStackMetric ContextSelf, int TotalRows, IReadOnlyList<CpuStackRow> Rows, bool More, CpuHotPath? HotPath = null,
    IReadOnlyList<CpuThreadMetric>? Threads = null);
public record CpuThreadMetric(string? ThreadId, CpuStackMetric Metric);
public record CpuHotPath(IReadOnlyList<string> Keys, IReadOnlyList<string> Names,
    EvidenceReference? Evidence, string Basis = "Selected CPU sample counts; not elapsed execution or waiting time.");

public static class CpuStackAggregation
{
    public const int MaximumFrames = 256;
    public const int MaximumRows = 100000;
    private static readonly CpuStackFrame Missing = new("missing-stack", "[No captured stack]", "missing-stack", "[No captured stack]", false);
    private static readonly CpuStackFrame Truncated = new("truncated-stack", "[Older frames truncated]", "truncated-stack", "[Older frames truncated]", false);
    private sealed class Counter
    {
        public long Samples, Weighted;
        public double Ms;
        public void Add(Observation item) { Samples++; if (item.CpuWeightMs is { } ms && double.IsFinite(ms) && ms >= 0) { Weighted++; Ms += ms; } }
        public CpuStackMetric Metric => new(Samples, Weighted, Ms);
    }
    private sealed class Row(CpuStackFrame frame, EvidenceReference evidence)
    {
        public CpuStackFrame Frame = frame;
        public EvidenceReference Evidence = evidence;
        public Counter Inclusive = new(), Exclusive = new();
        public bool Children;
    }
    private sealed class PathNode(CpuStackFrame? frame = null, EvidenceReference? evidence = null)
    {
        public CpuStackFrame? Frame = frame;
        public EvidenceReference? Evidence = evidence, SelfEvidence;
        public Counter Inclusive = new(), Exclusive = new();
        public Dictionary<string, PathNode> Children = new(StringComparer.Ordinal);
    }
    public static void Validate(CpuStackQuery query)
    {
        if (!Enum.IsDefined(query.View) || !Enum.IsDefined(query.Grouping) || query.Offset < 0 || query.Offset > MaximumRows || query.Limit is < 1 or > 500 ||
            query.Path?.Count > MaximumFrames + 1 || query.Path?.Any(x => string.IsNullOrEmpty(x) || x.Length > 256) == true || query.FocusKey?.Length > 256 ||
            (query.View is CpuStackView.Callers or CpuStackView.Callees && string.IsNullOrEmpty(query.FocusKey)) ||
            (query.View == CpuStackView.Functions && query.Path?.Count > 0) ||
            query.ThreadId is { Length: 0 or > 100 } ||
            (query.HottestPath && (query.View != CpuStackView.CallTree || query.Path?.Count > 0 || query.Offset != 0 || query.FocusKey != null)))
            throw new WoiException("InvalidStackQuery", "Choose a supported stack view, focus and path, with a page size from 1 to 500.");
    }
    public static CpuStackPage Build(string sourceHash, CpuStackQuery query, IEnumerable<CpuStackSample> samples, CancellationToken token)
    {
        Validate(query);
        var selected = new Counter(); var captured = new Counter(); var missing = new Counter(); var context = new Counter(); var self = new Counter();
        long unresolved = 0, truncated = 0;
        Dictionary<string, Row> rows = new(StringComparer.Ordinal);
        var tree = query.HottestPath ? new PathNode() : null;
        int treeNodes = 0;
        Dictionary<string, Counter> threads = new(StringComparer.Ordinal);
        var prefix = query.Path ?? [];
        foreach (var sample in samples) {
            token.ThrowIfCancellationRequested();
            if (sample.Event.Category != "CPU" || sample.Event.TimeMs < query.StartMs || sample.Event.TimeMs >= query.EndMs ||
                (query.ProcessId != null && sample.Event.ProcessId != query.ProcessId)) continue;
            var threadKey = sample.Event.ThreadId ?? "";
            if (!threads.TryGetValue(threadKey, out var thread)) {
                if (threads.Count >= MaximumRows) throw new WoiException("StackLimit", "Too many thread lifetimes. Narrow the interval or process.");
                threads.Add(threadKey, thread = new());
            }
            thread.Add(sample.Event);
            if (query.ThreadId != null && sample.Event.ThreadId != query.ThreadId) continue;
            if (selected.Samples >= 1000000) throw new WoiException("StackLimit", "Stack analysis is limited to one million samples. Select a shorter interval or one process.");
            selected.Add(sample.Event);
            bool hasStack = sample.Frames.Count > 0;
            (hasStack ? captured : missing).Add(sample.Event);
            if (sample.Frames.Any(x => !x.Resolved)) unresolved++;
            bool cut = sample.Truncated || sample.Frames.Count > MaximumFrames;
            if (cut) truncated++;
            var frames = sample.Frames.Take(MaximumFrames).ToList();
            if (!hasStack) frames.Add(Missing);
            if (cut) frames.Add(Truncated);
            if (query.Grouping == CpuStackGrouping.Module) {
                var modules = new List<CpuStackFrame>();
                foreach (var frame in frames) {
                    if (modules.Count == 0 || modules[^1].Key != frame.ModuleKey)
                        modules.Add(new(frame.ModuleKey, frame.Module, frame.ModuleKey, frame.Module, frame.Resolved));
                }
                frames = modules;
            }
            if (tree != null) {
                var node = tree; node.Inclusive.Add(sample.Event);
                foreach (var frame in frames.AsEnumerable().Reverse()) {
                    if (!node.Children.TryGetValue(frame.Key, out var child)) {
                        if (++treeNodes > MaximumRows) throw new WoiException("StackLimit", "Too many contextual stack nodes. Narrow the interval or process/thread.");
                        node.Children.Add(frame.Key, child = new(frame, sample.Event.Evidence));
                    }
                    node = child; node.Inclusive.Add(sample.Event);
                }
                node.Exclusive.Add(sample.Event); node.SelfEvidence ??= sample.Event.Evidence;
                continue;
            }
            void AddRow(CpuStackFrame frame, bool exclusive, bool children) {
                if (!rows.TryGetValue(frame.Key, out var row)) {
                    if (rows.Count >= MaximumRows) throw new WoiException("StackLimit", "Too many distinct stack rows. Use module grouping or narrow the interval/process.");
                    rows.Add(frame.Key, row = new(frame, sample.Event.Evidence));
                }
                row.Inclusive.Add(sample.Event); if (exclusive) row.Exclusive.Add(sample.Event); row.Children |= children;
            }
            if (query.View == CpuStackView.Functions) {
                context.Add(sample.Event);
                HashSet<string> seen = new(StringComparer.Ordinal);
                for (int i = 0; i < frames.Count; i++)
                    if (seen.Add(frames[i].Key)) AddRow(frames[i], i == 0, false);
                continue;
            }
            // A recursive sample contributes once, at the occurrence closest to the sampled leaf.
            List<CpuStackFrame> path;
            if (query.View == CpuStackView.CallTree) path = frames.AsEnumerable().Reverse().ToList();
            else {
                var focus = frames.FindIndex(x => x.Key == query.FocusKey);
                if (focus < 0) continue;
                path = query.View == CpuStackView.Callers ? frames.Skip(focus).ToList() : frames.Take(focus + 1).Reverse().ToList();
            }
            if (prefix.Count > path.Count || prefix.Where((key, index) => path[index].Key != key).Any()) continue;
            context.Add(sample.Event);
            if (path.Count == prefix.Count) self.Add(sample.Event);
            else AddRow(path[prefix.Count], query.View == CpuStackView.Callers ? prefix.Count == 0 && frames[0].Key == query.FocusKey : path.Count == prefix.Count + 1,
                path.Count > prefix.Count + 1);
        }
        token.ThrowIfCancellationRequested();
        var threadMetrics = threads.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new CpuThreadMetric(x.Key.Length == 0 ? null : x.Key, x.Value.Metric)).ToArray();
        if (tree != null) {
            List<PathNode> chosen = []; var node = tree;
            while (node.Children.Count > 0) {
                token.ThrowIfCancellationRequested();
                var child = node.Children.Values.OrderByDescending(x => x.Inclusive.Samples).ThenBy(x => x.Frame!.Key, StringComparer.Ordinal).First();
                // A sampled leaf can dominate the children of the same contextual node.
                if (node.Frame != null && node.Exclusive.Samples >= child.Inclusive.Samples) break;
                chosen.Add(child); node = child;
            }
            var parent = chosen.Count > 1 ? chosen[^2] : tree;
            var children = parent.Children.Values.OrderByDescending(x => x.Inclusive.Samples).ThenBy(x => x.Frame!.Key, StringComparer.Ordinal).ToArray();
            var pageRows = children.Take(query.Limit).Select(x => new CpuStackRow(x.Frame!.Key, x.Frame.Name, x.Frame.Module,
                x.Inclusive.Metric, x.Exclusive.Metric, selected.Samples == 0 ? 0 : 100d * x.Inclusive.Samples / selected.Samples,
                x.Children.Count > 0, x.SelfEvidence ?? x.Evidence!)).ToArray();
            var keys = chosen.Select(x => x.Frame!.Key).ToArray();
            var names = chosen.Select(x => x.Frame!.Name).ToArray();
            var applied = query with { HottestPath = false, Path = keys.SkipLast(1).ToArray() };
            return new(sourceHash, applied, selected.Metric, captured.Metric, missing.Metric, unresolved, truncated,
                parent.Inclusive.Metric, parent.Exclusive.Metric, children.Length, pageRows, children.Length > pageRows.Length,
                new(keys, names, node.SelfEvidence ?? node.Evidence), threadMetrics);
        }
        var sorted = rows.Values.OrderByDescending(x => query.View == CpuStackView.Functions ? x.Exclusive.Samples : x.Inclusive.Samples)
            .ThenByDescending(x => x.Inclusive.Samples).ThenBy(x => x.Frame.Key, StringComparer.Ordinal).ToArray();
        var page = sorted.Skip(query.Offset).Take(query.Limit).Select(x => new CpuStackRow(x.Frame.Key, x.Frame.Name, x.Frame.Module,
            x.Inclusive.Metric, x.Exclusive.Metric, selected.Samples == 0 ? 0 : 100d * x.Inclusive.Samples / selected.Samples, x.Children, x.Evidence)).ToArray();
        return new(sourceHash, query, selected.Metric, captured.Metric, missing.Metric, unresolved, truncated, context.Metric, self.Metric,
            sorted.Length, page, query.Offset + page.Length < sorted.Length, Threads: threadMetrics);
    }
}
