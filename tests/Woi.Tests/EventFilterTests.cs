using System.Text.Json;
using Woi.Core;
using Woi.Trace;
using Xunit;

namespace Woi.Tests;

public sealed class EventFilterTests
{
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "woi-filter-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private static EventFilterRule Rule(EventFilterField field, string value, EventFilterAction action = EventFilterAction.Include,
        EventFilterOperator op = EventFilterOperator.Equals) => new(field, op, value, action);
    [Fact] public void IncludesGroupByFieldAndExclusionsOverrideRegardlessOfOrder()
    {
        var value = CoreTests.Obs(1, 10) with { ProcessName = "Editor.exe", Operation = "Read", Target = @"C:\temp\file.txt" };
        EventFilterRule[] rules = [Rule(EventFilterField.ProcessName, "other.exe"), Rule(EventFilterField.ProcessName, "EDITOR.EXE"),
            Rule(EventFilterField.Operation, "read"), Rule(EventFilterField.Target, "temp", op: EventFilterOperator.Contains)];
        Assert.True(new EventFilterMatcher(rules).Matches(value));
        Assert.False(new EventFilterMatcher(rules).Matches(value with { Operation = "Write" }));
        var exclude = Rule(EventFilterField.Target, ".txt", EventFilterAction.Exclude, EventFilterOperator.EndsWith);
        Assert.False(new EventFilterMatcher(rules.Append(exclude).ToArray()).Matches(value));
        Assert.False(new EventFilterMatcher(rules.Prepend(exclude).ToArray()).Matches(value));
        Assert.True(new EventFilterMatcher([exclude]).Matches(value with { Target = "other.bin" }));
        Assert.True(new EventFilterMatcher(null).Matches(value));
        Assert.True(new EventFilterMatcher([Rule(EventFilterField.Target, "c:\\TEMP", op: EventFilterOperator.StartsWith)]).Matches(value));
    }
    [Fact] public void InvalidRulesFailClosedAndQueryIdentityIsStructural()
    {
        EventFilterRule[] rules = [Rule(EventFilterField.Category, "CPU")];
        var query = new EventQuery(0, 100, Rules: rules);
        var roundtrip = JsonSerializer.Deserialize<EventQuery>(JsonSerializer.Serialize(query, Wire.Json), Wire.Json)!;
        Assert.True(EventFilterMatcher.SameQuery(query, roundtrip with { Cursor = 12 }));
        Assert.False(EventFilterMatcher.SameQuery(query, roundtrip with { Rules = [Rule(EventFilterField.Category, "Disk")] }));
        Assert.False(EventFilterMatcher.SameQuery(query, roundtrip with { ProcessId = "another-lifetime" }));
        foreach (var bad in new[] { rules[0] with { Value = "" }, rules[0] with { Value = new string('a', 513) }, rules[0] with { Field = (EventFilterField)99 },
            rules[0] with { Operator = (EventFilterOperator)99 }, rules[0] with { Action = (EventFilterAction)99 }, null! })
            Assert.Throws<WoiException>(() => new EventFilterMatcher([bad]));
        Assert.Throws<WoiException>(() => new EventFilterMatcher(Enumerable.Repeat(rules[0], 33).ToArray()));
    }
    [Fact] public void PresetsSurviveRestartReplaceByNameDeleteAndPreserveDamagedFiles()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "filters.json");
        var store = new EventFilterPresetStore(path);
        var preset = new EventFilterPreset("Work", "CPU", "sample", null, [Rule(EventFilterField.ProcessName, "editor")]);
        store.Save(preset);
        Assert.Equal(preset.Rules, new EventFilterPresetStore(path).Load().Single().Rules);
        store.Save(preset with { Name = "work", Operation = "changed" });
        Assert.Equal("changed", store.Load().Single().Operation);
        store.Delete("WORK"); Assert.Empty(new EventFilterPresetStore(path).Load());
        foreach (var damaged in new[] { "not-json", "{\"version\":999,\"presets\":[]}", "{\"version\":1,\"presets\":[null]}",
            "{\"version\":1,\"presets\":[{\"name\":\"damaged\",\"rules\":[{\"value\":\"editor\"}]}]}" }) {
            File.WriteAllText(path, damaged);
            Assert.Throws<WoiException>(() => store.Save(preset));
            Assert.Equal(damaged, File.ReadAllText(path));
        }
    }
    [FixtureFact] public void FilteredEtlPagingMatchesIndependentSelectionAndRecoversAfterCancellation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(Environment.GetEnvironmentVariable("WOI_FIXTURES")!, "net452", "net.4.5.2.x64.etl");
        var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
        var session = EtlImporter.Import(path, directory.Path, null, default);
        using var reader = new EtlReader(session);
        var all = reader.ReadObservations(new(0, session.DurationMs), default).ToArray();
        var name = all.Where(x => x.Category == "CPU").GroupBy(x => x.ProcessName).OrderByDescending(x => x.Count()).First().Key;
        EventFilterRule[] rules = [Rule(EventFilterField.Category, "CPU"), Rule(EventFilterField.Category, "Process"),
            Rule(EventFilterField.ProcessName, name), Rule(EventFilterField.Category, "Process", EventFilterAction.Exclude)];
        var expected = all.Where(x => x.Category == "CPU" && x.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Index).ToArray();
        Assert.True(expected.Length > 500);
        var query = new EventQuery(0, session.DurationMs, Rules: rules);
        var first = reader.QueryEvents(query, default);
        List<long> actual = first.Events.Select(x => x.Index).ToList();
        var page = first;
        while (page.More) {
            var sent = JsonSerializer.Deserialize<EventQuery>(JsonSerializer.Serialize(query with { Cursor = page.NextCursor }, Wire.Json), Wire.Json)!;
            page = reader.QueryEvents(sent, default);
            Assert.True(reader.LastEventPageProfile!.ContinuedIterator);
            actual.AddRange(page.Events.Select(x => x.Index));
        }
        Assert.Equal(expected, actual);
        Assert.Equal(first.Events, reader.QueryEvents(query, default).Events);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.QueryEvents(query with { Cursor = first.NextCursor }, cancelled.Token));
        var retry = reader.QueryEvents(query with { Cursor = first.NextCursor }, default);
        Assert.Equal(expected.Skip(500).Take(500), retry.Events.Select(x => x.Index));
        Assert.Empty(reader.QueryEvents(query with { Rules = rules.Append(Rule(EventFilterField.ProcessName, name, EventFilterAction.Exclude)).ToArray() }, default).Events);
        Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    }
}
