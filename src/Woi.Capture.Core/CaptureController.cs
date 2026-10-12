using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Woi.Core;

namespace Woi.Capture;

public sealed class CaptureController : IAsyncDisposable
{
    private readonly IWprRunner runner;
    private readonly CaptureStorage storage;
    private readonly int parentPid;
    private readonly DateTime parentStartUtc;
    private readonly SemaphoreSlim gate=new(1);
    private readonly TimeProvider time;
    private FileStream? profileLease;
    private string? directory;
    private CaptureOptions? options;
    private long? recordingTimestamp;
    private volatile CaptureStatus status=new(CapturePhase.Idle,"","",0,null,0,null,null,null,"Ready to record.");
    public CaptureController(IWprRunner runner,string root,int parentPid,DateTime parentStartUtc,TimeProvider? time=null)
    {
        this.runner=runner;storage=new(root);this.parentPid=parentPid;this.parentStartUtc=parentStartUtc;
        this.time=time??TimeProvider.System;
    }
    public static string RecordingRoot=>CaptureStorage.DefaultRoot;
    public CaptureStatus Status {
        get {
            var snapshot=status;
            return snapshot with{ElapsedSeconds=snapshot.Phase==CapturePhase.Recording && recordingTimestamp is {} start?
                Math.Max(0,time.GetElapsedTime(start,time.GetTimestamp()).TotalSeconds):snapshot.ElapsedSeconds};
        }
    }

    public async Task<CaptureStatus> Start(CaptureOptions requested,CancellationToken token)
    {
        CaptureProfiles.Validate(requested);
        await gate.WaitAsync(token);
        try {
            if(status.Phase!=CapturePhase.Idle)throw new WoiException("CaptureState","This helper can own only one recording.");
            var drive=new DriveInfo(Path.GetPathRoot(storage.DisplayRoot)!);
            if(drive.AvailableFreeSpace<2L*1024*1024*1024)throw new WoiException("InsufficientDiskSpace","Recording requires at least 2 GiB of free space for saving and merging.");
            var id=Guid.NewGuid().ToString("N");directory=storage.CreateRecording(id);
            options=requested;status=new(CapturePhase.Starting,"WoiCapture"+id,requested.Profile,requested.DurationSeconds,null,0,null,null,null,"Starting recording.");
            var profile=Path.Combine(directory,"profile.wprp");
            storage.WriteProfile(directory,CaptureProfiles.Xml());
            profileLease=storage.OpenRead(directory,"profile.wprp");
            WriteMetadata();
            var result=await runner.Run(["-start",profile+"!"+requested.Profile+".Light","-instancename",status.Instance],token);
            if(result.ExitCode!=0) {
                await CancelOwned();
                status=status with{Phase=CapturePhase.Failed,Message="WPR could not start this recording. "+result.Output.Trim()};
                WriteMetadata();throw new WoiException("CaptureStartFailed",status.Message);
            }
            recordingTimestamp=time.GetTimestamp();
            status=status with{Phase=CapturePhase.Recording,StartedUtc=time.GetUtcNow().UtcDateTime,Message="Recording. Stop to preserve the trace or cancel to discard it."};
            WriteMetadata();return Status;
        } catch {
            if(status.Phase==CapturePhase.Starting){await CancelOwned();status=status with{Phase=CapturePhase.Failed,Message="Recording did not start."};WriteMetadata();}
            throw;
        } finally {gate.Release();}
    }
    public async Task<CaptureStatus> Stop(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try {
            if(status.Phase==CapturePhase.Saved)return Status;
            if(status.Phase!=CapturePhase.Recording)throw new WoiException("CaptureState","There is no owned recording to save.");
            status=status with{ElapsedSeconds=Status.ElapsedSeconds,Phase=CapturePhase.Saving,Message="Saving and merging the captured recording."};WriteMetadata();
            var path=Path.Combine(directory!,"recording.etl");
            try {
                var result=await runner.Run(["-stop",path,"Windows Open Intelligence bounded recording","-instancename",status.Instance],token);
                if(result.ExitCode!=0)throw new WoiException("CaptureSaveFailed","WPR could not save this recording. "+result.Output.Trim());
                await using var input=storage.OpenRead(directory!,"recording.etl");
                var bytes=input.Length;
                if(bytes==0)throw new WoiException("CaptureSaveFailed","WPR produced no usable recording.");
                if(bytes>1024L*1024*1024)throw new WoiException("SizeLimit","The captured ETL exceeds the analyzer's 1 GiB limit. It remains on disk for inspection.");
                var hash=Convert.ToHexString(await SHA256.HashDataAsync(input,token)).ToLowerInvariant();
                status=status with{Phase=CapturePhase.Saved,TracePath=storage.DisplayPath(directory!,"recording.etl"),TraceBytes=bytes,Sha256=hash,Message="Recording saved read-only in collector storage. Use Save recording to keep a user-owned copy."};
                WriteMetadata();ReleaseProfile();return Status;
            } catch(Exception ex) {
                await CancelOwned();
                status=status with{Phase=CapturePhase.Failed,Message=ex.Message};WriteMetadata();ReleaseProfile();throw;
            }
        } finally {gate.Release();}
    }
    public async Task<CaptureStatus> Cancel(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try {
            if(status.Phase is CapturePhase.Recording or CapturePhase.Starting or CapturePhase.Saving) {
                var elapsed=Status.ElapsedSeconds;await CancelOwned();status=status with{ElapsedSeconds=elapsed,Phase=CapturePhase.Cancelled,Message="Recording cancelled. No completed ETL was published."};WriteMetadata();
            }
            ReleaseProfile();return Status;
        } finally {gate.Release();}
    }
    public async Task Tick(CancellationToken token)
    {
        if(Status is {Phase:CapturePhase.Recording} current && current.ElapsedSeconds>=current.DurationSeconds)
            await Stop(token);
    }
    private async Task CancelOwned()
    {
        if(string.IsNullOrEmpty(status.Instance))return;
        var result=await runner.Run(["-cancel","-instancename",status.Instance],CancellationToken.None);
        if(result.ExitCode!=0 && !result.Output.Contains("0xc5583000",StringComparison.OrdinalIgnoreCase) &&
            status.Phase is CapturePhase.Recording or CapturePhase.Starting or CapturePhase.Saving)
            throw new WoiException("CaptureCleanupFailed","The owned session could not be cancelled. Recovery is required. "+result.Output.Trim());
    }
    private void WriteMetadata()
    {
        if(directory==null || options==null)return;
        using var helper=Process.GetCurrentProcess();
        using var identity=WindowsIdentity.GetCurrent();
        var metadata=new CaptureMetadata(1,status.Instance,identity.User!.Value,parentPid,parentStartUtc,
            helper.Id,helper.StartTime.ToUniversalTime(),options,status.StartedUtc,status.Phase,status.Sha256,status.TraceBytes,status.Message);
        storage.WriteMetadata(directory,JsonSerializer.Serialize(metadata,new JsonSerializerOptions(Wire.Json){WriteIndented=true}));
    }
    public static void EnsureLocalDirectory(string path)
    {
        var full=Path.GetFullPath(path);
        if(full.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(full)!).DriveType==DriveType.Network)
            throw new WoiException("LocalInputRequired","Recording storage must be local.");
        for(var candidate=new DirectoryInfo(full);candidate!=null;candidate=candidate.Parent)
            if(candidate.Exists && candidate.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new WoiException("InvalidRecordingPath","Recording storage cannot contain redirected directories or junctions.");
    }
    private void ReleaseProfile(){profileLease?.Dispose();profileLease=null;}
    public async ValueTask DisposeAsync()
    {
        try{await Cancel(default);}finally{ReleaseProfile();storage.Dispose();gate.Dispose();}
    }
}
