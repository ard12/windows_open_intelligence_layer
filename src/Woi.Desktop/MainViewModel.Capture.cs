using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Woi.Capture;
using Woi.Core;
namespace Woi.Desktop;
public partial class MainViewModel
{
    private CaptureClient? captureClient;
    private CancellationTokenSource? captureCancellation;
    private TaskCompletionSource? stopRecording;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRecord))] private bool captureActive;
    [ObservableProperty] private string captureMessage="Choose a profile and duration, then start a bounded local recording.";
    [ObservableProperty] private string recordingSeconds="30";
    [ObservableProperty] private CaptureProfile selectedCaptureProfile=CaptureProfiles.All[0];
    [ObservableProperty] private string capturedTracePath="";
    public IReadOnlyList<CaptureProfile> RecordingProfiles=>CaptureProfiles.All;
    public bool CanRecord=>!CaptureActive && !Busy;
    public async Task Record()
    {
        if(!CanRecord)return;
        Error="";
        if(!int.TryParse(RecordingSeconds,NumberStyles.Integer,CultureInfo.InvariantCulture,out var duration)){
            Error="Enter a recording duration from 5 to 120 seconds.";return;
        }
        var options=new CaptureOptions(SelectedCaptureProfile.Id,duration);
        try{CaptureProfiles.Validate(options);}catch(Exception ex){Error=ex.Message;return;}
        CaptureActive=true;captureCancellation=new();stopRecording=new(TaskCreationOptions.RunContinuationsAsynchronously);
        string? saved=null;
        try {
            CaptureMessage="Waiting for Windows administrator elevation. The analyzer stays unprivileged.";
            captureClient=await CaptureClient.Connect(CaptureClient.ResolveHelper(),captureCancellation.Token);
            var state=await captureClient.Send("start",options,captureCancellation.Token);
            while(true) {
                CaptureMessage=state.Phase==CapturePhase.Saving?state.Message:
                    $"{state.Phase} · {state.ElapsedSeconds:F0} / {duration} seconds · {state.Message}";
                if(state.Phase==CapturePhase.Saved){saved=state.TracePath;CapturedTracePath=saved??"";break;}
                if(state.Phase is CapturePhase.Failed or CapturePhase.Cancelled)
                    throw new WoiException("CaptureFailed",state.Message);
                if(stopRecording.Task.IsCompleted && state.Phase==CapturePhase.Recording)
                    state=await captureClient.Send("stop",new{},captureCancellation.Token);
                else {
                    await Task.Delay(500,captureCancellation.Token);
                    state=await captureClient.Send("status",new{},captureCancellation.Token);
                }
            }
            CaptureMessage="Recording saved. Importing its actual coverage and recorded evidence.";
        } catch(OperationCanceledException){CaptureMessage="Recording cancelled. Existing recordings were preserved.";}
        catch(Exception ex){Error=ex.Message;CaptureMessage="The recording could not be completed.";}
        finally {
            if(captureClient!=null)await captureClient.DisposeAsync();
            captureClient=null;captureCancellation.Dispose();captureCancellation=null;stopRecording=null;CaptureActive=false;
        }
        if(saved!=null) {
            await Open(saved);
            CaptureMessage=string.IsNullOrEmpty(Error)?"Recording saved and opened. Review Capture quality for actual provider coverage.":"Recording saved, but import failed. The ETL remains available.";
        }
    }
    public void StopRecording()=>stopRecording?.TrySetResult();
    public void CancelRecording()=>captureCancellation?.Cancel();
    public Task SaveRecording(string destination)=>Run(async token=>{
        if(string.IsNullOrEmpty(CapturedTracePath))throw new WoiException("NoRecording","Make a recording first.");
        destination=Path.GetFullPath(destination);
        if(destination.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(destination)!).DriveType==DriveType.Network)
            throw new WoiException("LocalInputRequired","Choose a local destination for the recording.");
        if(destination.Equals(Path.GetFullPath(CapturedTracePath),StringComparison.OrdinalIgnoreCase))return;
        string pending=destination+".pending-"+Guid.NewGuid().ToString("N");
        try {
            await using(var input=File.OpenRead(CapturedTracePath))
            await using(var output=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true))
                await input.CopyToAsync(output,token);
            token.ThrowIfCancellationRequested();File.Move(pending,destination,true);Status="Recording copy saved.";
        }finally{if(File.Exists(pending))File.Delete(pending);}
    });
}
