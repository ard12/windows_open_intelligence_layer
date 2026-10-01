using System.Text.Json;
using Woi.Core;

internal static class InvestigationRpc
{
    public static object Handle(InvestigationWorkspace workspace, Request request, CancellationToken token = default)
    {
        var p = request.Parameters;
        string Id() => p.GetProperty("id").GetString() ?? "";
        string Path() => p.GetProperty("path").GetString() ?? "";
        switch (request.Method) {
            case "investigation.open": return workspace.Open(Path());
            case "investigation.attach.report": return workspace.Attach(Id(), p.GetProperty("report").Deserialize<Report>(Wire.Json)!);
            case "investigation.get": return workspace.GetView(Id());
            case "investigation.close": return new { closed = workspace.Close(Id()) };
            case "investigation.save": workspace.Save(Id(), Path(), token); return new { saved = true };
            case "investigation.evidence": return workspace.Evidence(Id(), p.GetProperty("reference").Deserialize<SnapshotReference>(Wire.Json)!);
            case "investigation.compare": return workspace.Compare(p.Deserialize<ComparisonRequest>(Wire.Json)!);
            case "investigation.compare.export":
                workspace.ExportComparison(p.GetProperty("comparison").Deserialize<ComparisonRequest>(Wire.Json)!, Path(), p.GetProperty("format").GetString() ?? ""); return new { exported = true };
            case "investigation.export":
                workspace.Export(Id(), Path(), p.GetProperty("format").GetString() ?? "", p.TryGetProperty("includeLocalPaths", out var paths) && paths.GetBoolean());
                return new { exported = true };
            default: throw new WoiException("UnknownMethod", "The requested investigation operation is not supported.");
        }
    }
}
