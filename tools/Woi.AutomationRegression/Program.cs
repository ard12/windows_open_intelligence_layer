using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using Woi.Desktop;
class Program {
 static List<object> providers=new();static List<WeakReference> retired=new();static List<object> samples=new();
 static int completed;static int? monitorPid,analysisPid;static DateTime? monitorStart,analysisStart;static bool monitoringExpected;static string stage="startup",output="";static bool listener,effective;static Exception? error;
 const string Key="Switch.System.Windows.Automation.Peers.ItemAutomationPeerKeepsItsItemAlive";
 [STAThread] static void Main(string[] args) {
  output=args[1];
  if(args[0]=="--verify-memory-guard") { VerifyMemoryGuard(); return; }
  AppContext.SetSwitch(Key,bool.Parse(args[2]));
  effective=(bool)typeof(DataGrid).Assembly.GetType("MS.Internal.FrameworkAppContextSwitches")!.GetProperty("ItemAutomationPeerKeepsItsItemAlive",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(null)!;
  var refs=Run(args);monitoringExpected=false;Collect();Sample("after window close");
  Save(new {windowCollected=!refs.Window.IsAlive,viewModelCollected=!refs.Vm.IsAlive});
 }
 static void VerifyMemoryGuard() {
  bool Reject(int pid, DateTime started) {
   monitorPid=pid;monitorStart=started;monitoringExpected=true;
   try { Sample("invalid monitor negative control");return false; }
   catch(InvalidOperationException ex) when(ex.Message.Contains("combined-memory gate is unknown")) { return true; }
  }
  using var current=Process.GetCurrentProcess();
  bool missingRejected=Reject(int.MaxValue,DateTime.UtcNow);
  bool identityMismatchRejected=Reject(current.Id,current.StartTime.ToUniversalTime().AddSeconds(-1));
  monitoringExpected=false;monitorPid=null;monitorStart=null;Sample("unknown after shutdown");
  Save(new {missingRejected,identityMismatchRejected});
  Environment.ExitCode=missingRejected&&identityMismatchRejected?0:1;
 }
 static void Collect(){GC.Collect(2,GCCollectionMode.Forced,true,true);GC.WaitForPendingFinalizers();GC.Collect(2,GCCollectionMode.Forced,true,true);}
 static DateTime? Started(int? id){try {using var child=Process.GetProcessById(id??-1);return child.StartTime.ToUniversalTime();}catch{return null;}}
 static long? Working(int? id,DateTime? started){if(id==null||started==null)return null;try {using var child=Process.GetProcessById(id.Value);return !child.HasExited && child.StartTime.ToUniversalTime()==started ? child.WorkingSet64 : null;}catch{return null;}}
 static void Sample(string label){using var p=Process.GetCurrentProcess();p.Refresh();long? monitorWorkingSet=Working(monitorPid,monitorStart),analysisWorkingSet=Working(analysisPid,analysisStart);samples.Add(new {label,completed,monitoringExpected,monitorWorkingSet,analysisWorkingSet,uiPlusMonitorWorkingSet=monitorWorkingSet.HasValue ? (long?)(p.WorkingSet64+monitorWorkingSet.Value) : null,managedBytes=GC.GetTotalMemory(true),privateBytes=p.PrivateMemorySize64,workingSet=p.WorkingSet64,retiredTracked=retired.Count,retiredAlive=retired.Count(x=>x.IsAlive),heldProviders=providers.Count});if(monitoringExpected&&!monitorWorkingSet.HasValue)throw new InvalidOperationException("Expected owned monitor memory is unavailable; combined-memory gate is unknown.");}
 static void Save(object? recovery=null)=>File.WriteAllText(output,JsonSerializer.Serialize(new {completed,stage,listener,effective,error=error?.ToString(),runtime=Environment.Version.ToString(),samples,recovery},new JsonSerializerOptions {WriteIndented=true}));
 [MethodImpl(MethodImplOptions.NoInlining)] static (WeakReference Window,WeakReference Vm) Run(string[] args) {
  bool held=args[3]=="held";
  var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
  app.DispatcherUnhandledException+=(_,e)=> {error=e.Exception;Save();app.Shutdown(1);};
  var window=new MainWindow();((MainViewModel)window.DataContext).Dispose();
  var vm=new MainViewModel(Path.ChangeExtension(output,"presets.json"),Path.Combine(Path.GetDirectoryName(output)!,"cache"));
  typeof(MainWindow).GetField("model",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,vm);window.DataContext=vm;
  vm.ChartsChanged+=(Action)Delegate.CreateDelegate(typeof(Action),window,typeof(MainWindow).GetMethod("RenderCharts",BindingFlags.Instance|BindingFlags.NonPublic)!);
  vm.MonitorChartsChanged+=(Action)Delegate.CreateDelegate(typeof(Action),window,typeof(MainWindow).GetMethod("RenderMonitorCharts",BindingFlags.Instance|BindingFlags.NonPublic)!);
  var memoryTimer=new DispatcherTimer(TimeSpan.FromSeconds(1),DispatcherPriority.Background,(_,_)=>Sample("periodic"),app.Dispatcher);memoryTimer.Start();
  DataGrid Grid(string name)=>(DataGrid)window.FindName(name);
  void Tab(string name)=>((TabControl)window.FindName("Views")).SelectedItem=window.FindName(name);
  void Remember(string name){foreach(var item in Grid(name).Items)retired.Add(new WeakReference(item));}
  void Activate(string name) {
   var g=Grid(name);g.UpdateLayout();if(g.Items.Count==0)return;
   var peer=(DataGridAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(g);
   if(held&&completed==0)providers.AddRange(peer.GetChildren().OfType<DataGridItemAutomationPeer>());
   var provider=((IGridProvider)peer.GetPattern(PatternInterface.Grid)).GetItem(0,completed%g.Columns.Count);if(held)providers.Add(provider);
   g.SelectedIndex=0;g.CurrentCell=new DataGridCellInfo(g.Items[0],g.Columns[completed%g.Columns.Count]);g.ScrollIntoView(g.Items[^1]);
  }
  window.Loaded+=async(_,_)=> {try {
   stage="client attach";File.WriteAllText(output+".ready",Environment.ProcessId.ToString());
   for(int i=0;i<200&&!File.Exists(output+".go");i++)await Task.Delay(100);
   listener=AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected);if(!listener)throw new Exception("No external UIA listener");
   stage="monitor";await vm.StartObservation();monitorPid=(int?)typeof(MainViewModel).GetProperty("MonitorWorkerProcessId",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm);if(monitorPid==null)throw new Exception("Monitor unavailable: "+vm.MonitorStatus);monitorStart=Started(monitorPid);monitoringExpected=true;stage="open";await vm.Open(args[0]);analysisPid=(int?)typeof(MainViewModel).GetProperty("AnalysisWorkerProcessId",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm);analysisStart=Started(analysisPid);if(vm.Error.Length>0)throw new Exception(vm.Error);Collect();Sample("opened");
   for(int i=0;i<20;i++) {
    stage=$"events first {i}";((TabControl)window.FindName("Views")).SelectedIndex=1;Remember("EventGrid");await vm.FirstPage();Activate("EventGrid");Collect();
    stage=$"events next {i}";Remember("EventGrid");await vm.NextPage();Activate("EventGrid");Collect();Remember("EventGrid");await vm.PreviousPage();Activate("EventGrid");
    stage=$"history {i}";Tab("ProcessHistoryTab");Remember("HistoryGrid");await vm.LoadProcessHistory();Activate("HistoryGrid");Remember("HistoryGrid");await vm.FocusHistoryFamily();Collect();Activate("HistoryGrid");Remember("HistoryGrid");await vm.AllHistoryFamilies();Activate("HistoryGrid");
    stage=$"process scope {i}";vm.SelectedProcess=vm.Summary!.Processes.FirstOrDefault();Remember("ContributorGrid");await vm.Refresh();Collect();Activate("ContributorGrid");vm.SelectedProcess=null;Remember("ContributorGrid");await vm.Refresh();
    stage=$"stacks {i}";Tab("CpuStacksTab");Remember("CpuStackGrid");await vm.LoadCpuStacks();Activate("CpuStackGrid");Remember("CpuStackGrid");await vm.ShowHottestPath();Collect();Activate("CpuStackGrid");Remember("CpuStackGrid");await vm.StackParent();Remember("CpuStackGrid");await vm.LoadCpuStacks();Activate("CpuStackGrid");
    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Collect();window.UpdateLayout();
    if(vm.Error.Length>0)throw new Exception(vm.Error);completed++;await vm.RefreshMonitor();if(completed%5==0){Sample("batch");Save();}
   }
   stage="accessibility";await (Task)typeof(MainWindow).GetMethod("VerifyAccessibility",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,new object[]{Path.ChangeExtension(output,"png")})!;
   stage="detach";File.WriteAllText(output+".detach","detach");for(int i=0;i<100&&!File.Exists(output+".detached");i++)await Task.Delay(100);
   providers.Clear();Collect();Sample("after detach and provider release");
   stage="complete";Save();memoryTimer.Stop();monitoringExpected=false;window.Close();app.MainWindow=null;await Task.Delay(300);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Collect();Sample("closed, dispatcher drained");app.Shutdown(0);
  }catch(Exception e){error=e;Save();memoryTimer.Stop();monitoringExpected=false;window.Close();app.MainWindow=null;app.Shutdown(1);} };
  app.Run(window);return (new WeakReference(window),new WeakReference(vm));
 }
}
