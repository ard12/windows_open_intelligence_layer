using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Woi.Core;
using Woi.Desktop;
using Xunit;

namespace Woi.Tests;

public sealed class MonitorCollectionTests
{
    private static readonly DateTime Utc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static MonitorPoint Point(int tick) =>
        new(Utc.AddSeconds(tick), 1, tick % 3 == 0 ? null : tick, null, 0, 4096, 8192, 10, 1, tick);
    private static MonitorProcess Process(int pid, long creation, double? cpu) =>
        new($"1:{pid}:{creation}", pid, 1, creation, "recorded", cpu, null, 0, null, 4096);

    [Fact]
    public void UnchangedWindowPreservesSelectionObjectsAndEmitsNoChanges()
    {
        var mark = new MonitorMark("mark", 123, Utc, "Slowdown");
        var current = new ObservableCollection<MonitorMark>([mark]);
        var changes = 0; current.CollectionChanged += (_, _) => changes++;
        MonitorCollections.Window(current, [mark with {}]);
        Assert.Same(mark, Assert.Single(current));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void RollingHistoryOnlyRemovesEvictedPointsAndAddsNewPoints()
    {
        var old = Enumerable.Range(0, 120).Select(Point).ToArray();
        var current = new ObservableCollection<MonitorPoint>(old);
        var changes = new List<NotifyCollectionChangedAction>();
        current.CollectionChanged += (_, e) => changes.Add(e.Action);
        var next = Enumerable.Range(2, 120).Select(Point).ToArray();
        MonitorCollections.Window(current, next);
        Assert.Equal(next, current);
        Assert.Same(old[2], current[0]);
        Assert.Same(old[^1], current[117]);
        Assert.Equal(4, changes.Count);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Null(current[1].CpuPercent); // Missing measurements remain unavailable.
    }

    [Fact]
    public void WindowMatchesIncomingDuplicatesGapsClockRollbackAndReplacements()
    {
        var current = new ObservableCollection<MonitorPoint>();
        for (var round = 0; round < 40; round++) {
            var next = Enumerable.Range(round, 12).Select(i => Point(i) with {
                Utc = Utc.AddSeconds(i >= 20 ? i - 3600 : i),
                MonotonicTick = i / 2, CpuPercent = i % 4 == 0 ? null : i
            }).ToArray();
            MonitorCollections.Window(current, next);
            Assert.Equal(next, current);
        }
        MonitorCollections.Window(current, [Point(0), Point(0), Point(1)]);
        Assert.Equal(3, current.Count);
        MonitorCollections.Window(current, [Point(0), Point(1), Point(1)]);
        Assert.Equal(new[] {Point(0), Point(1), Point(1)}, current);
        var revised = Point(1) with { CpuPercent=null, UnreadableProcesses=9 };
        MonitorCollections.Window(current, [revised]);
        Assert.Equal(revised, Assert.Single(current));
        MonitorCollections.Window(current, []);
        Assert.Empty(current);
    }

    [Fact]
    public void ProcessReorderingAndMeasurementsPreserveRowsAndNotifyBindings()
    {
        var first = Process(42, 100, 1); var second = Process(43, 200, 2);
        var current = new ObservableCollection<MonitorProcessRow>();
        MonitorCollections.Processes(current, [first, second]);
        var firstRow = current[0]; var secondRow = current[1];
        var notifications = new List<string?>();
        firstRow.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        secondRow.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        var changes = new List<NotifyCollectionChangedAction>();
        current.CollectionChanged += (_, e) => changes.Add(e.Action);
        var updatedSecond = second with { Name="recorded rename", CpuPercent=null, WorkingSetBytes=null };
        var updatedFirst = first with { CpuPercent=80 };
        MonitorCollections.Processes(current, [updatedSecond, updatedFirst]);
        Assert.Same(secondRow, current[0]); Assert.Same(firstRow, current[1]);
        Assert.Equal(new[] { updatedSecond, updatedFirst }, current.Select(x => x.Process));
        Assert.Equal(new[] { "Process", "Process" }, notifications);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Add, changes);
        Assert.Null(secondRow.Process.CpuPercent); Assert.Null(secondRow.Process.WorkingSetBytes);
        changes.Clear(); notifications.Clear();
        MonitorCollections.Processes(current, [updatedSecond with {}, updatedFirst with {}]);
        Assert.Empty(changes); Assert.Empty(notifications);
    }

    [Fact]
    public void ReusedPidGetsANewRowAndInvalidIdentityChangesFailBeforeMutation()
    {
        var first = Process(42, 100, 1); var next = Process(42, 200, null);
        var current = new ObservableCollection<MonitorProcessRow>();
        MonitorCollections.Processes(current, [first]);
        var row = Assert.Single(current);
        Assert.Throws<ArgumentException>(() => MonitorCollections.Processes(current, [first with { CreationFileTime=200 }]));
        Assert.Same(row, Assert.Single(current)); Assert.Equal(first, row.Process);
        Assert.Throws<ArgumentException>(() => MonitorCollections.Processes(current, [first, first]));
        Assert.Same(row, Assert.Single(current));
        MonitorCollections.Processes(current, [next]);
        Assert.NotSame(row, Assert.Single(current));
        Assert.Equal(next, current[0].Process);
        Assert.Equal(first, row.Process);
    }
}
