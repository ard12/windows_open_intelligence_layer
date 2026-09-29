using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Woi.Core;

[assembly: InternalsVisibleTo("Woi.Tests")]
namespace Woi.Trace;

internal sealed record ImportPolicy(long SourceBytes = EtlImporter.MaximumSourceBytes,
    long DerivedBytes = EtlImporter.MaximumDerivedBytes, int IndexedEvents = EtlImporter.MaximumEvents,
    long WorkerBytes = 2L*1024*1024*1024, long MinimumFreeBytes = 32L*1024*1024)
{
    public void Validate() {
        if(SourceBytes<=0 || DerivedBytes<=0 || IndexedEvents<=0 || WorkerBytes<=0 || MinimumFreeBytes<0)
            throw new ArgumentOutOfRangeException(nameof(ImportPolicy));
    }
}
internal sealed class ImportEnvironment(Func<string,long>? freeBytes = null)
{
    public long FreeBytes(string path) => freeBytes?.Invoke(path) ??
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
}
internal sealed record DerivedFile(string Name,long Length,string Sha256);
internal sealed record CacheManifest(string SourceHash,string ParserVersion,string AnalysisVersion,
    long IndexedEvents,CacheStatistics Statistics,IReadOnlyList<DerivedFile> Files);

internal static class Fingerprints
{
    public static string Hash(Stream stream,CancellationToken token,Action<long>? progress=null) {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer=new byte[1024*1024];long processed=0;
        while(true) {
            token.ThrowIfCancellationRequested();
            int read=stream.Read(buffer,0,buffer.Length);if(read==0)break;
            hash.AppendData(buffer,0,read);processed+=read;progress?.Invoke(processed);
        }
        token.ThrowIfCancellationRequested();return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public static DerivedFile File(string path,CancellationToken token) {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        return new(Path.GetFileName(path),input.Length,Hash(input,token));
    }
}
public static class LocalPaths
{
    public static string RequireLocal(string path) {
        if(string.IsNullOrWhiteSpace(path))throw new WoiException("InvalidPath","Choose a local file or directory.");
        var full=Path.GetFullPath(path);
        if(full.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(full)!).DriveType==DriveType.Network)
            throw new WoiException("LocalInputRequired","Copy the recording or symbols to a local folder first.");
        return full;
    }
}
