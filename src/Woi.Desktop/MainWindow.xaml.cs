using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Woi.Core;

namespace Woi.Desktop;
public partial class MainWindow : Window
{
    private MainViewModel? model;
    private double? dragStart;
    public MainWindow()
    {
        InitializeComponent();
        try {
            model = new MainViewModel(); DataContext = model;
            model.ChartsChanged += RenderCharts;
        } catch(Exception ex) { MessageBox.Show(ex.Message,"Cannot start analysis worker"); Close(); return; }
        CpuPlot.ToolTip = "Hold Shift and drag across the chart to select a recorded interval.";
        CpuPlot.PreviewMouseLeftButtonDown += (_,e)=> {
            if((Keyboard.Modifiers & ModifierKeys.Shift)==0)return;
            var point=e.GetPosition(CpuPlot);
            dragStart=CpuPlot.Plot.GetCoordinates(new ScottPlot.Pixel(point.X,point.Y)).X;
            e.Handled=true; CpuPlot.CaptureMouse();
        };
        CpuPlot.PreviewMouseLeftButtonUp += async (_,e)=> {
            if(dragStart == null || model?.Summary == null)return;
            var point=e.GetPosition(CpuPlot);
            var end=CpuPlot.Plot.GetCoordinates(new ScottPlot.Pixel(point.X,point.Y)).X;
            var start=Math.Clamp(Math.Min(dragStart.Value,end),0,model.Summary.Session.DurationMs/1000);
            end=Math.Clamp(Math.Max(dragStart.Value,end),0,model.Summary.Session.DurationMs/1000);
            dragStart=null; CpuPlot.ReleaseMouseCapture(); e.Handled=true;
            if(end <= start)return;
            model.StartSeconds=start.ToString("0.######",CultureInfo.InvariantCulture);
            model.EndSeconds=end.ToString("0.######",CultureInfo.InvariantCulture);
            await model.Refresh();
        };
        Closed += (_,_)=>model?.Dispose();
        Loaded += LoadedWindow;
        Views.SelectionChanged += (_,e) => {
            if(e.Source == Views) Dispatcher.BeginInvoke(RenderCharts, DispatcherPriority.Render);
        };
        RenderCharts();
    }
    private async void LoadedWindow(object sender,RoutedEventArgs e)
    {
        var args=Environment.GetCommandLineArgs();
        if(args.Length > 2 && args[1]=="--smoke-test") {
            var snapshot=args.Length>3?Path.GetFullPath(args[3]):Path.Combine(AppContext.BaseDirectory,"desktop.png");
            try {
                await model!.Open(args[2]);
                if(!string.IsNullOrEmpty(model.Error))throw new InvalidOperationException(model.Error);
                bool recovered=false;
                if(args.Contains("--verify-recovery")){
                    int previous=model.WorkerProcessId;
                    using(var worker=System.Diagnostics.Process.GetProcessById(previous)){worker.Kill();await worker.WaitForExitAsync();}
                    await Task.Delay(100);await model.Open(args[2]);
                    if(!string.IsNullOrEmpty(model.Error) || model.WorkerProcessId==previous)
                        throw new InvalidOperationException("Worker crash recovery failed: "+model.Error);
                    recovered=true;
                }
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
                await Task.Delay(350); Capture(snapshot);
                Views.SelectedItem=RecordTab;await Task.Delay(100);Capture(Path.ChangeExtension(snapshot,"record.png"));
                Views.SelectedIndex=1; await Task.Delay(200); RenderCharts(); LifetimePlot.Plot.SavePng(Path.ChangeExtension(snapshot,"lifetimes.png"),900,240); Capture(Path.ChangeExtension(snapshot,"explorer.png"));
                var observation=model.Events.FirstOrDefault(x=>x.Category!="Unsupported") ?? model.Events.FirstOrDefault();
                await model.ShowEvidence(observation); Views.SelectedIndex=2; await Task.Delay(200);
                Capture(Path.ChangeExtension(snapshot,"evidence.png"));
                File.WriteAllText(Path.ChangeExtension(snapshot,"json"),JsonSerializer.Serialize(new {
                    success=true, workerRecoveryVerified=recovered, events=model.Events.Count, contributors=model.Contributors.Count,
                    indexedEvents=model.Summary?.Session.IndexedEvents,
                    lifetimes=model.Summary?.Processes.OrderByDescending(x=>x.EndMs-x.StartMs).Take(5),
                    capabilities=model.Summary?.Session.Capabilities
                },new JsonSerializerOptions(Wire.Json){WriteIndented=true}));
            } catch(Exception ex) {
                File.WriteAllText(Path.ChangeExtension(snapshot,"json"),JsonSerializer.Serialize(new{success=false,error=ex.ToString()}));
                Application.Current.Shutdown(1);
            } finally { Close(); }
        }
    }
    private void Capture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(path); encoder.Save(stream);
    }
    private void RenderCharts()
    {
        if(model==null)return;
        var metrics=model.Metrics;
        bool estimated=metrics.Any(x=>x.CpuPercent!=null);
        var cpu=metrics.ToArray();
        bool cpuAvailable=model.Summary?.Session.Capabilities.Any(x=>x.Category=="CPU" && x.Status==Availability.Available)==true;
        Draw(CpuPlot,cpuAvailable ? cpu.Select(x=>x.TimeMs/1000).ToArray() : [],cpuAvailable ? cpu.Select(x=>estimated ? x.CpuPercent ?? double.NaN : (double?)x.CpuSamples ?? double.NaN).ToArray() : [],
            !cpuAvailable?"No captured busy CPU samples":estimated ? "Estimated CPU activity (% of machine)" : "CPU samples per one-second bucket","#168A88");
        string choice=(SecondaryPlotSelector.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()??"Physical disk";
        Func<MetricPoint,double?> measure=choice switch {
            "Logical file"=>x=>x.FileBytes/1048576d,
            "Network"=>x=>x.NetworkBytes/1048576d,
            "Working set"=>x=>x.WorkingSetBytes/1048576d,
            "Disk latency"=>x=>x.DiskP95Ms,
            _=>x=>x.DiskBytes/1048576d
        };
        var data=metrics.Any(x=>measure(x)!=null) ? metrics.ToArray() : [];
        string units=choice=="Disk latency"?"Recorded p95 (ms)":choice=="Working set"?"Captured working sets (MiB; shared pages may overlap)":"Recorded bytes (MiB per one-second bucket)";
        Draw(DiskPlot,data.Select(x=>x.TimeMs/1000).ToArray(),data.Select(x=>measure(x) ?? double.NaN).ToArray(),
            data.Length==0?choice+" · no captured measurements":units,"#516CA6");
        if(model.Summary!=null) {
            try {var interval=model.Interval();CpuPlot.Plot.Axes.SetLimitsX(interval.StartMs/1000,interval.EndMs/1000);DiskPlot.Plot.Axes.SetLimitsX(interval.StartMs/1000,interval.EndMs/1000);CpuPlot.Refresh();DiskPlot.Refresh();} catch(WoiException){}
        }
        LifetimePlot.Plot.Clear();
        var lifetimes=model.SelectedProcess==null ? model.Contributors.Where(x=>x.ProcessId!=null).Take(5)
            .Select(x=>model.Summary?.Processes.FirstOrDefault(p=>p.Id==x.ProcessId)).OfType<EntityLifetime>().ToArray() : [model.SelectedProcess];
        int row=1;
        foreach(var p in lifetimes) {
            var line=LifetimePlot.Plot.Add.Scatter(new[]{p.StartMs/1000,p.EndMs/1000},new[]{(double)row,(double)row});
            line.LineWidth=6; line.MarkerSize=4; row++;
        }
        LifetimePlot.Plot.Title(lifetimes.Length==0?"No recorded lifetimes":"Captured lifetimes · selected interval highlighted");
        LifetimePlot.Plot.XLabel("Seconds from trace start");
        if(model.Summary!=null) {
            try {
                var interval=model.Interval();
                var span=LifetimePlot.Plot.Add.VerticalSpan(interval.StartMs/1000,interval.EndMs/1000);
                span.FillColor=ScottPlot.Color.FromHex("#168A88").WithAlpha(.12);
            } catch(WoiException) {}
        }
        LifetimePlot.Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            Enumerable.Range(1,lifetimes.Length).Select(x=>(double)x).ToArray(),lifetimes.Select(x=>$"{x.Name} ({x.OsId})").ToArray());
        LifetimePlot.Plot.Axes.SetLimitsY(0,lifetimes.Length+1);
        LifetimePlot.Plot.Axes.SetLimitsX(0,Math.Max(.001,model.Summary?.Session.DurationMs/1000 ?? 1));
        LifetimePlot.Refresh();
    }
    private static void Draw(ScottPlot.WPF.WpfPlot plot,double[] x,double[] y,string title,string color)
    {
        plot.Plot.Clear(); plot.Plot.Title(title); plot.Plot.XLabel("Seconds from trace start");
        if(x.Length>0) {
            var line=plot.Plot.Add.Scatter(x,y); line.Color=ScottPlot.Color.FromHex(color); line.MarkerSize=y.Count(double.IsFinite)==1 ? 5 : 0; line.LineWidth=2;
            plot.Plot.Axes.AutoScale();
        }
        plot.Refresh();
    }
    private void RecordActivityClicked(object sender,RoutedEventArgs e)=>Views.SelectedItem=RecordTab;
    private async void StartRecordingClicked(object sender,RoutedEventArgs e)
    {
        if(model==null)return;await model.Record();
        if(string.IsNullOrEmpty(model.Error) && model.Summary?.Session.SourcePath==model.CapturedTracePath)
            Views.SelectedIndex=0;
    }
    private void StopRecordingClicked(object sender,RoutedEventArgs e)=>model?.StopRecording();
    private void CancelRecordingClicked(object sender,RoutedEventArgs e)=>model?.CancelRecording();
    private async void SaveRecordingClicked(object sender,RoutedEventArgs e)
    {
        if(model==null || string.IsNullOrEmpty(model.CapturedTracePath))return;
        var dialog=new SaveFileDialog{Filter="ETW recording (*.etl)|*.etl",FileName="recording.etl"};
        if(dialog.ShowDialog(this)==true)await model.SaveRecording(dialog.FileName);
    }
    private async void OpenClicked(object sender,RoutedEventArgs e)
    {
        if(model?.Busy!=false)return;
        var dialog=new OpenFileDialog{Filter="ETW recordings (*.etl)|*.etl",Title="Open a recorded Windows trace"};
        if(dialog.ShowDialog(this)==true)await model.Open(dialog.FileName);
    }
    private async void ApplyClicked(object sender,RoutedEventArgs e) { if(model!=null)await model.Refresh(); }
    private async void AllClicked(object sender,RoutedEventArgs e) { if(model!=null){model.SelectedProcess=null;await model.Refresh();} }
    private void CancelClicked(object sender,RoutedEventArgs e)=>model?.Cancel();
    private async void FirstClicked(object sender,RoutedEventArgs e) { if(model!=null)await model.FirstPage(); }
    private async void NextClicked(object sender,RoutedEventArgs e) { if(model!=null)await model.NextPage(); }
    private async void BookmarkClicked(object sender,RoutedEventArgs e) { if(model!=null)await model.SaveBookmark(); }
    private async void BookmarkSelected(object sender,System.Windows.Controls.SelectionChangedEventArgs e) { if(model?.Busy==false)await model.SelectBookmark(BookmarkList.SelectedItem as Bookmark); }
    private async void SettingsClicked(object sender,RoutedEventArgs e) { if(model!=null)await model.Configure(); }
    private async void EventClicked(object sender,RoutedEventArgs e)
    {
        if(model==null)return; await model.ShowEvidence(EventGrid.SelectedItem as Observation); Views.SelectedIndex=2;
    }
    private async void EntityClicked(object sender,RoutedEventArgs e)
    {
        if(model==null)return; await model.ShowEntity(model.SelectedProcess); Views.SelectedIndex=2;
    }
    private async void ContributorClicked(object sender,MouseButtonEventArgs e)
    {
        if(model==null)return; await model.SelectContributor(ContributorGrid.SelectedItem as Contributor); Views.SelectedIndex=1;
    }
    private async void IncidentClicked(object sender,MouseButtonEventArgs e)
    {
        if(model!=null)await model.SelectIncident(IncidentList.SelectedItem as Incident);
    }
    private async void SymbolsClicked(object sender,RoutedEventArgs e)
    {
        if(model==null || EventGrid.SelectedItem is not Observation observation)return;
        var folder=new OpenFolderDialog{Title="Choose a local folder of matching PDB files"};
        if(folder.ShowDialog(this)==true)await model.ShowEvidence(observation,folder.FolderName);
    }
    private async void ExportClicked(object sender,RoutedEventArgs e)
    {
        if(model?.Summary==null || model.Busy)return;
        var dialog=new SaveFileDialog{Filter="Markdown report (*.md)|*.md|JSON evidence report (*.json)|*.json",FileName="incident-report.md"};
        if(dialog.ShowDialog(this)==true)await model.Export(dialog.FileName,dialog.FilterIndex==2?"json":"markdown");
    }
    private void ChartSelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)=>RenderCharts();
}
