using System.Xml.Linq;
using Woi.Core;

namespace Woi.Capture;

public sealed record CaptureProfile(string Id,string Name,string Description,IReadOnlyList<string> RequestedCategories);
public sealed record CaptureOptions(string Profile="Resources",int DurationSeconds=30);
public enum CapturePhase { Idle, Starting, Recording, Saving, Saved, Cancelled, Failed }
public sealed record CaptureStatus(CapturePhase Phase,string Instance,string Profile,int DurationSeconds,
    DateTime? StartedUtc,double ElapsedSeconds,string? TracePath,string? Sha256,long? TraceBytes,string Message);
public sealed record CaptureMetadata(int Schema,string Instance,string OwnerSid,int ParentPid,DateTime ParentStartUtc,
    int HelperPid,DateTime HelperStartUtc,CaptureOptions Options,DateTime? StartedUtc,CapturePhase Phase,
    string? Sha256,long? Bytes,string? Message);
public static class CaptureProfiles
{
    public const int MinimumSeconds=5,MaximumSeconds=120;
    public const int BufferSizeKiB=1024,BufferCount=128;
    public static IReadOnlyList<CaptureProfile> All {get;}=[
        new("Resources","Resource activity","CPU, physical disk, network, lifetimes, and available memory snapshots.",
            ["CPU","Disk","Network","Process","Thread","Module","Memory","HardFault"]),
        new("FileActivity","Resource and file activity","Adds logical file operations and captured path resolution. Higher event volume.",
            ["CPU","Disk","File","Network","Process","Thread","Module","Memory","HardFault"])
    ];
    public static CaptureProfile Validate(CaptureOptions options)
    {
        var profile=All.FirstOrDefault(x=>x.Id==options.Profile) ??
            throw new WoiException("InvalidProfile","Choose a built-in recording profile.");
        if(options.DurationSeconds<MinimumSeconds || options.DurationSeconds>MaximumSeconds)
            throw new WoiException("InvalidDuration","Choose a recording duration from 5 to 120 seconds.");
        return profile;
    }
    public static string Xml()
    {
        string[] standard=["CpuConfig","ProcessThread","Loader","SampledProfile","DiskIO","DiskIOInit",
            "HardFaults","MemoryInfo","MemoryInfoWS","NetworkTrace"];
        XElement Provider(string id,bool files)=>new("SystemProvider",new XAttribute("Id",id),
            new XElement("Keywords",standard.Concat(files?["FileIO","FileIOInit"]:Array.Empty<string>())
                .Select(x=>new XElement("Keyword",new XAttribute("Value",x)))));
        var root=new XElement("WindowsPerformanceRecorder",new XAttribute("Version","1.0"),
            new XAttribute("Author","Windows Open Intelligence"),
            new XElement("Profiles",
                new XElement("SystemCollector",new XAttribute("Id","WoiSystem"),new XAttribute("Name","WOI System Collector"),
                    new XElement("BufferSize",new XAttribute("Value",BufferSizeKiB)),
                    new XElement("Buffers",new XAttribute("Value",BufferCount))),
                Provider("ResourceProvider",false),Provider("FileProvider",true),
                All.Select(profile=>new XElement("Profile",new XAttribute("Id",profile.Id+".Light.Memory"),
                    new XAttribute("Name",profile.Id),new XAttribute("Description",profile.Description),
                    new XAttribute("DetailLevel","Light"),new XAttribute("LoggingMode","Memory"),
                    new XElement("Collectors",new XElement("SystemCollectorId",new XAttribute("Value","WoiSystem"),
                        new XElement("SystemProviderId",new XAttribute("Value",profile.Id=="Resources"?"ResourceProvider":"FileProvider"))))))));
        return new XDocument(new XDeclaration("1.0","utf-8",null),root).ToString();
    }
}
