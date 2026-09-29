using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Woi.Core;
namespace Woi.Capture;
public static class PipeIdentity
{
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle,out uint pid);
    public static void RequirePeer(PipeStream pipe,int expectedPid,bool server)
    {
        uint pid;bool okay=server?GetNamedPipeClientProcessId(pipe.SafePipeHandle,out pid):
            GetNamedPipeServerProcessId(pipe.SafePipeHandle,out pid);
        if(!okay || pid!=expectedPid)throw new WoiException("CapturePeerMismatch","The capture connection did not match the expected local process.");
    }
}
