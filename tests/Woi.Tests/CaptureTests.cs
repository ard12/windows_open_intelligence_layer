using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text.Json;
using System.Xml.Linq;
using Woi.Capture;
using Woi.Core;
using Xunit;
namespace Woi.Tests;
public sealed class CaptureTests
{
    private sealed class Temp:IDisposable {
        private readonly string parent=Path.Combine(Path.GetTempPath(),"woi-capture-"+Guid.NewGuid().ToString("N"));
        public string Root=>Path.Combine(parent,"collector");
        public Temp()=>Directory.CreateDirectory(parent);
        public void Dispose()=>Directory.Delete(parent,true);
    }
    private sealed class Clock:TimeProvider {
        public DateTimeOffset Now{get;set;}=DateTimeOffset.UtcNow;
        private long ticks;
        public override DateTimeOffset GetUtcNow()=>Now;
        public override long GetTimestamp()=>ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public void Advance(int seconds){ticks+=seconds*TimeSpan.TicksPerSecond;Now=Now.AddSeconds(seconds);}
    }
    private sealed class FakeRunner:IWprRunner {
        public List<string[]> Calls{get;}=[];
        public string? Failure{get;set;}
        public bool AlreadyStopped{get;set;}
        public Task<WprResult> Run(IReadOnlyList<string> args,CancellationToken token) {
            token.ThrowIfCancellationRequested();Calls.Add(args.ToArray());
            if(args[0]=="-cancel" && AlreadyStopped)return Task.FromResult(new WprResult(1,"0xC5583000: no recording is running"));
            if(args[0]==Failure)return Task.FromResult(new WprResult(1,"simulated failure"));
            if(args[0]=="-stop")File.WriteAllBytes(args[1],[1,2,3,4,5,6]);
            return Task.FromResult(new WprResult(0,"okay"));
        }
    }
    private static CaptureController Controller(FakeRunner runner,Temp temp,TimeProvider? clock=null) {
        using var current=Process.GetCurrentProcess();
        return new(runner,temp.Root,current.Id,current.StartTime.ToUniversalTime(),clock);
    }
    [Theory][InlineData(4)][InlineData(121)][InlineData(0)][InlineData(int.MaxValue)]
    public void DurationsOutsideTheBoundedRangeAreRejected(int seconds)=>Assert.Throws<WoiException>(()=>CaptureProfiles.Validate(new("Resources",seconds)));
    [Fact] public void ProfilesHaveExplicitFixedBuffersAndProviderCoverage() {
        var xml=XDocument.Parse(CaptureProfiles.Xml());
        Assert.Equal("128",xml.Descendants("Buffers").Single().Attribute("Value")!.Value);
        Assert.Equal("1024",xml.Descendants("BufferSize").Single().Attribute("Value")!.Value);
        Assert.Equal(2,xml.Descendants("Profile").Count());
        Assert.All(xml.Descendants("Profile"),x=>Assert.Equal("Memory",x.Attribute("LoggingMode")!.Value));
        Assert.Contains(xml.Descendants("Keyword"),x=>x.Attribute("Value")!.Value=="FileIO");
        Assert.Throws<WoiException>(()=>CaptureProfiles.Validate(new("../arbitrary.wprp",30)));
    }
    [Fact] public async Task StartStopPreservesCompletedRecordingAndMetadata() {
        using var temp=new Temp();var runner=new FakeRunner();var clock=new Clock();
        await using var controller=Controller(runner,temp,clock);
        var recording=await controller.Start(new("FileActivity",30),default);
        Assert.Equal(CapturePhase.Recording,recording.Phase);
        clock.Advance(8);
        var saved=await controller.Stop(default);
        Assert.Equal(CapturePhase.Saved,saved.Phase);Assert.Equal(8,saved.ElapsedSeconds);
        Assert.NotNull(saved.Sha256);Assert.Equal(6,saved.TraceBytes);
        Assert.Equal(saved,await controller.Stop(default));
        Assert.Equal(CapturePhase.Saved,(await controller.Cancel(default)).Phase);
        var metadata=JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(saved.TracePath!)!,"capture.json")),Wire.Json)!;
        Assert.Equal(saved.Sha256,metadata.Sha256);
        Assert.All(runner.Calls,x=>{Assert.Equal("-instancename",x[^2]);Assert.Equal(recording.Instance,x[^1]);});
        Assert.DoesNotContain(runner.Calls,x=>x[0]=="-cancel");
    }
    [Fact] public async Task DeadlineAutomaticallySavesAndManualCancelPublishesNoTrace() {
        using var temp=new Temp();var runner=new FakeRunner();var clock=new Clock();
        await using(var controller=Controller(runner,temp,clock)) {
            await controller.Start(new("Resources",5),default);clock.Advance(5);
            await controller.Tick(default);Assert.Equal(CapturePhase.Saved,controller.Status.Phase);
        }
        await using(var controller=Controller(runner,temp)) {
            await controller.Start(new(),default);var cancelled=await controller.Cancel(default);
            Assert.Equal(CapturePhase.Cancelled,cancelled.Phase);Assert.Null(cancelled.TracePath);
        }
        Assert.Single(Directory.GetFiles(temp.Root,"recording.etl",SearchOption.AllDirectories));
        Assert.Single(runner.Calls,x=>x[0]=="-cancel");
    }
    [Fact] public async Task DeadlineUsesElapsedTimeEvenWhenWallClockChanges() {
        using var temp=new Temp();var runner=new FakeRunner();var clock=new Clock();
        await using var controller=Controller(runner,temp,clock);await controller.Start(new("Resources",5),default);
        clock.Now=clock.Now.AddHours(1);await controller.Tick(default);
        Assert.Equal(CapturePhase.Recording,controller.Status.Phase);Assert.Equal(0,controller.Status.ElapsedSeconds);
        clock.Now=clock.Now.AddHours(-2);clock.Advance(5);await controller.Tick(default);
        Assert.Equal(CapturePhase.Saved,controller.Status.Phase);Assert.Equal(5,controller.Status.ElapsedSeconds);
    }
    [Theory][InlineData("-start")][InlineData("-stop")]
    public async Task WprFailureNeverAppearsAsSavedAndOnlyCancelsItsOwnInstance(string failure) {
        using var temp=new Temp();var runner=new FakeRunner{Failure=failure};
        await using var controller=Controller(runner,temp);
        if(failure=="-start")await Assert.ThrowsAsync<WoiException>(()=>controller.Start(new(),default));
        else {await controller.Start(new(),default);await Assert.ThrowsAsync<WoiException>(()=>controller.Stop(default));}
        Assert.Equal(CapturePhase.Failed,controller.Status.Phase);Assert.Null(controller.Status.TracePath);
        Assert.All(runner.Calls,x=>Assert.Equal(controller.Status.Instance,x[^1]));
        Assert.Contains(runner.Calls,x=>x[0]=="-cancel");
    }
    [Fact] public async Task SaveFailureKeepsItsOriginalErrorWhenWprAlreadyStopped() {
        using var temp=new Temp();var runner=new FakeRunner{Failure="-stop",AlreadyStopped=true};
        await using var controller=Controller(runner,temp);await controller.Start(new(),default);
        var error=await Assert.ThrowsAsync<WoiException>(()=>controller.Stop(default));
        Assert.Equal("CaptureSaveFailed",error.Code);Assert.Equal(CapturePhase.Failed,controller.Status.Phase);
        Assert.Null(controller.Status.TracePath);
    }
    [Fact] public async Task ParentDisconnectDisposalCancelsRecordingWithoutSaving() {
        using var temp=new Temp();var runner=new FakeRunner();
        var controller=Controller(runner,temp);await controller.Start(new(),default);await controller.DisposeAsync();
        Assert.Single(runner.Calls,x=>x[0]=="-cancel");
        Assert.Empty(Directory.GetFiles(temp.Root,"recording.etl",SearchOption.AllDirectories));
    }
    [Fact] public async Task PipeIdentityAcceptsItsProcessAndRejectsUnexpectedPeers() {
        var name="woi.capture.test."+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected=server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);await connected;
        PipeIdentity.RequirePeer(server,Environment.ProcessId,true);
        PipeIdentity.RequirePeer(client,Environment.ProcessId,false);
        Assert.Equal("CapturePeerMismatch",Assert.Throws<WoiException>(()=>PipeIdentity.RequirePeer(server,int.MaxValue,true)).Code);
        Assert.Equal("CapturePeerMismatch",Assert.Throws<WoiException>(()=>PipeIdentity.RequirePeer(client,int.MaxValue,false)).Code);
    }
    [Fact] public async Task ProtocolRejectsVersionsAndUnknownCommandsWithoutStartingWpr() {
        using var temp=new Temp();var runner=new FakeRunner();await using var controller=Controller(runner,temp);
        var parameters=JsonSerializer.SerializeToElement(new{},Wire.Json);
        var version=await CaptureWire.Dispatch(controller,new("one",99,"start",parameters),default);
        var unknown=await CaptureWire.Dispatch(controller,new("two",1,"sql",parameters),default);
        Assert.Equal("ProtocolError",version.Code);Assert.Equal("UnknownMethod",unknown.Code);Assert.Empty(runner.Calls);
        await Assert.ThrowsAsync<WoiException>(()=>CaptureWire.ReadLine(new StringReader(new string('x',65537)+"\n"),default));
        await Assert.ThrowsAsync<WoiException>(()=>CaptureWire.ReadLine(new StringReader("{}"),default));
    }
    [Fact] public async Task ConcurrentStartsCannotOwnTwoSessions() {
        using var temp=new Temp();var runner=new FakeRunner();await using var controller=Controller(runner,temp);
        var first=controller.Start(new(),default);var second=controller.Start(new(),default);
        await first;await Assert.ThrowsAsync<WoiException>(()=>second);
        Assert.Single(runner.Calls,x=>x[0]=="-start");
    }
    [Fact] public async Task RecoveryRefusesArbitrarySessionNamesAndKeepsLiveOwners() {
        using var temp=new Temp();var runner=new FakeRunner();
        using var storage=new CaptureStorage(temp.Root);
        using var current=Process.GetCurrentProcess();
        var owner=WindowsIdentity.GetCurrent().User!.Value;
        void Write(string id,string instance,int pid,DateTime started) {
            var folder=storage.CreateRecording(id);
            storage.WriteMetadata(folder,JsonSerializer.Serialize(new CaptureMetadata(1,instance,owner,0,DateTime.MinValue,pid,started,new(),null,CapturePhase.Recording,null,null,null),Wire.Json));
        }
        var dead=Guid.NewGuid().ToString("N");Write(dead,"WoiCapture"+dead,999999,DateTime.MinValue);
        var hostile=Guid.NewGuid().ToString("N");Write(hostile,"OtherToolsSession",999999,DateTime.MinValue);
        var live=Guid.NewGuid().ToString("N");Write(live,"WoiCapture"+live,current.Id,current.StartTime.ToUniversalTime());
        Assert.Equal(1,await CaptureRecovery.Recover(temp.Root,runner,default));
        var call=Assert.Single(runner.Calls);Assert.Equal("WoiCapture"+dead,call[^1]);
    }
    [Fact] public void ExistingWritableStorageIsRefusedWithoutChangingIt() {
        using var temp=new Temp();Directory.CreateDirectory(temp.Root);
        var sentinel=Path.Combine(temp.Root,"legacy.txt");File.WriteAllText(sentinel,"preserve legacy evidence");
        Assert.Equal("InvalidRecordingStorage",Assert.Throws<WoiException>(()=>new CaptureStorage(temp.Root)).Code);
        Assert.Equal("preserve legacy evidence",File.ReadAllText(sentinel));
    }
    [Fact] public void StoragePinsDirectoriesAndBindsWritesToVolumeIdentity() {
        using var temp=new Temp();var moved=temp.Root+"-moved";
        using(var storage=new CaptureStorage(temp.Root)) {
            Assert.StartsWith(@"\\?\Volume{",storage.Root);
            Assert.ThrowsAny<IOException>(()=>Directory.Move(temp.Root,moved));
            var parent=Directory.GetParent(temp.Root)!.FullName;
            Assert.ThrowsAny<IOException>(()=>Directory.Move(parent,parent+"-moved"));
            var security=new DirectoryInfo(temp.Root).GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
            Assert.True(security.AreAccessRulesProtected);
            var descriptor=new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(),0);
            var ownerRights=Assert.Single(descriptor.DiscretionaryAcl!.Cast<CommonAce>(),x=>x.SecurityIdentifier.Value=="S-1-3-4");
            Assert.Equal(0x20000,ownerRights.AccessMask);
            Assert.Equal(AceFlags.ContainerInherit|AceFlags.ObjectInherit,ownerRights.AceFlags);
        }
        Directory.Move(temp.Root,moved);
    }
    [Fact] public void ProfileCreationIsExclusiveAndReadLeaseRejectsMutation() {
        using var temp=new Temp();using var storage=new CaptureStorage(temp.Root);
        var folder=storage.CreateRecording(Guid.NewGuid().ToString("N"));
        var first=Path.Combine(folder,"first.tmp");var second=Path.Combine(folder,"second.tmp");
        File.WriteAllText(first,"first");File.WriteAllText(second,"second");File.Move(first,second,true);
        Assert.Equal("first",File.ReadAllText(second));
        storage.WriteProfile(folder,"fixed profile");
        Assert.ThrowsAny<IOException>(()=>storage.WriteProfile(folder,"replacement"));
        using var input=storage.OpenRead(folder,"profile.wprp");
        Assert.ThrowsAny<IOException>(()=>File.WriteAllText(Path.Combine(folder,"profile.wprp"),"changed"));
        using var reader=new StreamReader(input);Assert.Equal("fixed profile",reader.ReadToEnd());
    }
}
