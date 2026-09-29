using System.Text;
using System.Text.Json;
using Woi.Core;
namespace Woi.Capture;
public sealed record CaptureRequest(string Id,int Version,string Method,JsonElement Parameters);
public sealed record CaptureReply(string Id,int Version,CaptureStatus? Status=null,string? Error=null,string? Code=null);
public static class CaptureWire
{
    public const int Version=1,MaximumCharacters=65536;
    public static async Task<string?> ReadLine(TextReader reader,CancellationToken token)
    {
        StringBuilder value=new();char[] buffer=new char[1];
        while(await reader.ReadAsync(buffer.AsMemory(),token)!=0) {
            if(buffer[0]=='\n')return value.ToString().TrimEnd('\r');
            if(value.Length==MaximumCharacters)throw new WoiException("ProtocolError","Capture request exceeds 64 KiB.");
            value.Append(buffer[0]);
        }
        return value.Length==0?null:throw new WoiException("ProtocolError","Capture message ended before its newline.");
    }
    public static async Task<CaptureReply> Dispatch(CaptureController controller,CaptureRequest request,CancellationToken token)
    {
        if(request.Version!=Version || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length>128 ||
            request.Parameters.ValueKind!=JsonValueKind.Object)
            return new(request.Id??"",Version,Error:"Invalid capture request.",Code:"ProtocolError");
        try {
            var state=request.Method switch {
                "start"=>await controller.Start(request.Parameters.Deserialize<CaptureOptions>(Wire.Json)!,token),
                "stop"=>await controller.Stop(token),
                "cancel"=>await controller.Cancel(token),
                "status"=>controller.Status,
                _=>throw new WoiException("UnknownMethod","This capture command is unavailable.")
            };
            return new(request.Id,Version,state);
        } catch(OperationCanceledException){return new(request.Id,Version,Error:"Capture operation cancelled.",Code:"Cancelled");}
        catch(WoiException ex){return new(request.Id,Version,Error:ex.Message,Code:ex.Code);}
        catch(Exception ex){return new(request.Id,Version,Error:ex.Message,Code:"CaptureFailed");}
    }
}
