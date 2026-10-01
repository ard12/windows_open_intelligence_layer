using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainWindow
{
    private async Task RunObservationSoak(int seconds, string output)
    {
        if (seconds is < 10 or > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        var samples = new List<object>(); var clock = Stopwatch.StartNew();
        try {
            await model!.StartObservation(); Views.SelectedItem = MonitorTab;
            using var ui = Process.GetCurrentProcess();
            using var worker = Process.GetProcessById(model.MonitorWorkerProcessId ?? throw new InvalidOperationException("Monitor did not start."));
            using var idleAnalysis = Process.GetProcessById(model.WorkerProcessId);
            long peakCombined = 0, peakTotal = 0;
            while (clock.Elapsed.TotalSeconds < seconds) {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, Math.Max(.1, seconds - clock.Elapsed.TotalSeconds))));
                await model.RefreshMonitor();
                if (model.Monitor == null || worker.HasExited) throw new InvalidOperationException("Observation stopped during the soak: " + model.MonitorStatus);
                ui.Refresh(); worker.Refresh(); idleAnalysis.Refresh();
                peakCombined = Math.Max(peakCombined, ui.WorkingSet64 + worker.WorkingSet64);
                peakTotal = Math.Max(peakTotal, ui.WorkingSet64 + worker.WorkingSet64 + idleAnalysis.WorkingSet64);
                samples.Add(new { elapsedSeconds=clock.Elapsed.TotalSeconds, uiWorkingSetBytes=ui.WorkingSet64,
                    monitorWorkingSetBytes=worker.WorkingSet64, idleAnalysisWorkingSetBytes=idleAnalysis.WorkingSet64,
                    snapshotCount=model.Monitor.SnapshotCount, retainedSeconds=model.Monitor.RetainedSeconds,
                    retainedProcessRows=model.Monitor.RetainedProcessRows, estimatedRetainedBytes=model.Monitor.EstimatedRetainedBytes,
                    diagnostics=model.Monitor.Diagnostics });
            }
            var duration = clock.Elapsed.TotalSeconds;
            var result = new { schema=1, success=true, utc=DateTime.UtcNow, source=model.Monitor!.Descriptor,
                durationSeconds=duration, peakUiMiB=ui.PeakWorkingSet64/1048576d,
                peakMonitorMiB=worker.PeakWorkingSet64/1048576d, maximumSampledCombinedMiB=peakCombined/1048576d,
                maximumSampledTotalAppMiB=peakTotal/1048576d,
                uiCpuPercentOfMachine=ui.TotalProcessorTime.TotalSeconds/duration/Environment.ProcessorCount*100,
                monitorCpuPercentOfMachine=worker.TotalProcessorTime.TotalSeconds/duration/Environment.ProcessorCount*100,
                monitorMemoryTargetMet=worker.PeakWorkingSet64<150L*1024*1024,
                uiPlusMonitorMemoryTargetMet=peakCombined<300L*1024*1024,
                finalView=new { model.Monitor.SnapshotCount,model.Monitor.RetainedSeconds,model.Monitor.RetainedProcessRows,model.Monitor.EstimatedRetainedBytes,model.Monitor.Diagnostics },
                samples, limitations=new[] { "Local desktop soak, not independent Windows 11 or clean-machine validation.",
                    "CPU values include startup; memory samples occur every five seconds and may miss a transient combined peak.",
                    "Background/hidden-window rendering can differ from active interaction.",
                    "Workload slowdown, physical disk-write overhead, sleep/resume and field usefulness are separate gates." } };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(result,new JsonSerializerOptions(Wire.Json){WriteIndented=true}));
        } catch (Exception ex) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output,JsonSerializer.Serialize(new { success=false,error=ex.ToString(),samples },new JsonSerializerOptions(Wire.Json){WriteIndented=true}));
            Application.Current.Shutdown(1);
        } finally { model?.StopObservation(); Close(); }
    }
}
