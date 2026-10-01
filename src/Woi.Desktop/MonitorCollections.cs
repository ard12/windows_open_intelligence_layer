using System.Collections.ObjectModel;
using System.ComponentModel;
using Woi.Core;

namespace Woi.Desktop;

// A stable display row holds the latest immutable measurement for one lifetime.
// It never joins by PID or mutates the evidence received from the worker.
public sealed class MonitorProcessRow(MonitorProcess process) : INotifyPropertyChanged
{
    public MonitorProcess Process { get; private set; } = process;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(MonitorProcess value)
    {
        RequireIdentity(value);
        if (Process == value) return;
        Process = value;
        PropertyChanged?.Invoke(this, new(nameof(Process)));
    }
    internal void RequireIdentity(MonitorProcess value)
    {
        if (Process.LifetimeId != value.LifetimeId || Process.Pid != value.Pid ||
            Process.SessionId != value.SessionId || Process.CreationFileTime != value.CreationFileTime)
            throw new ArgumentException("A display row cannot change its recorded process lifetime.");
    }
}

internal static class MonitorCollections
{
    // Immutable rolling windows normally share an old suffix/new prefix.
    // Preserve those objects and containers; incompatible/downsampled windows
    // still replace their full contents, without dropping or altering points.
    public static void Window<T>(ObservableCollection<T> current, IReadOnlyList<T> next)
    {
        var comparer = EqualityComparer<T>.Default;
        var overlap = Math.Min(current.Count, next.Count);
        while (overlap > 0) {
            var offset = current.Count - overlap;
            var matches = true;
            for (var i = 0; i < overlap; i++)
                if (!comparer.Equals(current[offset + i], next[i])) { matches=false; break; }
            if (matches) break;
            overlap--;
        }
        if (overlap == 0) {
            if (current.Count > 0) current.Clear();
        } else {
            var removed = current.Count - overlap;
            for (var i = 0; i < removed; i++) current.RemoveAt(0);
        }
        for (var i = overlap; i < next.Count; i++) current.Add(next[i]);
    }

    public static void Processes(ObservableCollection<MonitorProcessRow> current, IReadOnlyList<MonitorProcess> next)
    {
        var previous = current.ToDictionary(x => x.Process.LifetimeId, StringComparer.Ordinal);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in next) {
            if (!wanted.Add(value.LifetimeId))
                throw new ArgumentException("The process view contains duplicate lifetime identities.");
            if (previous.TryGetValue(value.LifetimeId, out var row)) row.RequireIdentity(value);
        }
        for (var i = current.Count - 1; i >= 0; i--)
            if (!wanted.Contains(current[i].Process.LifetimeId)) current.RemoveAt(i);
        for (var i = 0; i < next.Count; i++) {
            var value = next[i];
            if (previous.TryGetValue(value.LifetimeId, out var row)) {
                var position = current.IndexOf(row);
                if (position != i) current.Move(position, i);
                row.Update(value);
            } else current.Insert(i, new(value));
        }
    }
}
