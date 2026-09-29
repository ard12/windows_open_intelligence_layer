using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainViewModel
{
    private WorkerClient? monitorClient;
    private DispatcherTimer? monitorTimer;
    private bool monitorRefreshPending;
    public ObservableCollection<MonitorPoint> MonitorHistory { get; } = [];
    public ObservableCollection<MonitorProcess> MonitorTop { get; } = [];
    public ObservableCollection<MonitorMark> MonitorMarks { get; } = [];
    public ObservableCollection<MonitorLifetime> MonitorLifetimes { get; } = [];
    [ObservableProperty] private MonitorView? monitor;
    [ObservableProperty] private string monitorStatus = "Start observing to keep recent local history. No administrator elevation is needed.";
    [ObservableProperty] private string monitorMarkLabel = "Slowdown noticed";
    public event Action? MonitorChartsChanged;

    public async Task StartObservation()
    {
        if (monitorClient?.IsAlive == true) return;
        StopObservation();
        try {
            monitorClient = new WorkerClient(monitor: true);
            var startedClient = monitorClient;
            await RefreshMonitor();
            if (!ReferenceEquals(monitorClient, startedClient)) return;
            monitorTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
                async (_, _) => await RefreshMonitor(), System.Windows.Application.Current.Dispatcher);
            monitorTimer.Start();
        } catch (Exception ex) {
            StopObservation();
            MonitorStatus = ex.Message;
        }
    }
    public async Task RefreshMonitor()
    {
        if (monitorRefreshPending || monitorClient == null) return;
        var activeClient = monitorClient;
        monitorRefreshPending = true;
        try {
            var view = await activeClient.Send<MonitorView>("monitor.status", new {});
            if (!ReferenceEquals(monitorClient, activeClient)) return;
            Monitor = view;
            MonitorHistory.Clear(); foreach (var point in view.History.TakeLast(120)) MonitorHistory.Add(point);
            MonitorTop.Clear(); foreach (var process in view.TopProcesses) MonitorTop.Add(process);
            MonitorMarks.Clear(); foreach (var mark in view.Marks) MonitorMarks.Add(mark);
            MonitorStatus = $"Observing locally · {view.SnapshotCount:n0} snapshots · {view.RetainedSeconds:n0}s retained · {view.RetainedProcessRows:n0} process rows · {view.History.LastOrDefault()?.UnreadableProcesses ?? 0:n0} processes unreadable this tick";
            MonitorChartsChanged?.Invoke();
        } catch (Exception ex) {
            if (!ReferenceEquals(monitorClient, activeClient)) return;
            StopObservation();
            MonitorStatus = "Observation stopped: " + ex.Message;
        } finally { monitorRefreshPending = false; }
    }
    public async Task LoadMonitorLifetimes()
    {
        if (monitorClient?.IsAlive != true) return;
        var activeClient = monitorClient;
        try {
            var lifetimes = await activeClient.Send<List<MonitorLifetime>>("monitor.lifetimes", new {});
            if (!ReferenceEquals(monitorClient, activeClient)) return;
            MonitorLifetimes.Clear();
            foreach (var lifetime in lifetimes) MonitorLifetimes.Add(lifetime);
        } catch (Exception ex) { if (ReferenceEquals(monitorClient, activeClient)) MonitorStatus = ex.Message; }
    }
    public async Task MarkSlowdown()
    {
        if (monitorClient?.IsAlive != true) {
            MonitorStatus = "Start observing before marking a slowdown.";
            return;
        }
        var activeClient = monitorClient;
        try {
            await activeClient.Send<MonitorMark>("monitor.mark", new { label = MonitorMarkLabel });
            if (!ReferenceEquals(monitorClient, activeClient)) return;
            await RefreshMonitor();
        } catch (Exception ex) { if (ReferenceEquals(monitorClient, activeClient)) MonitorStatus = ex.Message; }
    }
    public void StopObservation()
    {
        var wasObserving = monitorClient != null || Monitor != null;
        monitorTimer?.Stop(); monitorTimer = null;
        monitorClient?.Dispose(); monitorClient = null;
        Monitor = null;
        MonitorHistory.Clear(); MonitorTop.Clear(); MonitorMarks.Clear(); MonitorLifetimes.Clear();
        MonitorChartsChanged?.Invoke();
        if (wasObserving) MonitorStatus = "Observation stopped. Unsaved history was discarded.";
    }
}
