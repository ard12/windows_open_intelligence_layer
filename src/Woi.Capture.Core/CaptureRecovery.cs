using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Woi.Core;
namespace Woi.Capture;
public static class CaptureRecovery
{
    public static async Task<int> Recover(string root,IWprRunner runner,CancellationToken token)
    {
        CaptureController.EnsureLocalDirectory(root);
        if(!Directory.Exists(root))return 0;
        int recovered=0;var owner=WindowsIdentity.GetCurrent().User!.Value;
        foreach(var folder in Directory.EnumerateDirectories(root)) {
            token.ThrowIfCancellationRequested();
            var name=Path.GetFileName(folder);
            if(!Guid.TryParseExact(name,"N",out _))continue;
            CaptureController.EnsureLocalDirectory(folder);
            CaptureMetadata? metadata;
            try {var file=Path.Combine(folder,"capture.json");if(new FileInfo(file).Length>65536)continue;metadata=JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(file),Wire.Json);}
            catch(Exception ex) when(ex is IOException or JsonException){continue;}
            if(metadata==null || metadata.Schema!=1 || metadata.OwnerSid!=owner || metadata.Instance!="WoiCapture"+name ||
                metadata.Phase is CapturePhase.Saved or CapturePhase.Cancelled ||
                Alive(metadata.HelperPid,metadata.HelperStartUtc))continue;
            var result=await runner.Run(["-cancel","-instancename",metadata.Instance],token);
            // "Not running" is harmless, but a nonzero result remains in metadata for manual review.
            if(result.ExitCode!=0 && !result.Output.Contains("0xc5583000",StringComparison.OrdinalIgnoreCase))
                throw new WoiException("CaptureRecoveryFailed","An interrupted owned session could not be recovered: "+result.Output.Trim());
            File.WriteAllText(Path.Combine(folder,"capture.json"),JsonSerializer.Serialize(
                metadata with{Phase=CapturePhase.Cancelled,Message="Recovered interrupted recording."},Wire.Json));
            recovered++;
        }
        return recovered;
    }
    private static bool Alive(int pid,DateTime startedUtc)
    {
        try{using var process=Process.GetProcessById(pid);return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime()-startedUtc).TotalMilliseconds)<10;}
        catch(ArgumentException){return false;}
        catch(System.ComponentModel.Win32Exception){return true;} // Refuse recovery when ownership cannot be established.
    }
}
