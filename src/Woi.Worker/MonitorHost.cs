using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Woi.Core;
using Woi.Monitor;

internal static class MonitorHost
{
    public static async Task<int> Run(string[] args)
    {
        var parentIndex = Array.IndexOf(args, "--parent");
        var startIndex = Array.IndexOf(args, "--parent-start");
        if (parentIndex < 0 || startIndex < 0 || parentIndex + 1 >= args.Length ||
            startIndex + 1 >= args.Length || !int.TryParse(args[parentIndex + 1], out var parentPid) ||
            !long.TryParse(args[startIndex + 1], out var parentStart))
            return 2;
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        using var stop = new CancellationTokenSource();
        var investigations = new InvestigationWorkspace();
        var session = new MonitorSession(new WindowsSampler(), WindowsSampler.LogicalProcessorCount);
        var sampling = Task.Run(async () => {
            try { await session.Run(stop.Token); }
            catch (OperationCanceledException) { }
        });
        var watchdog = Task.Run(async () => {
            try {
                while (!stop.IsCancellationRequested) {
                    using var parent = Process.GetProcessById(parentPid);
                    if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != parentStart) break;
                    await Task.Delay(1000, stop.Token);
                }
            } catch (OperationCanceledException) { return; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            stop.Cancel();
            Environment.Exit(0); // Redirected stdin reads may not cancel on every Windows pipe handle.
        });
        try {
            var lines = new BoundedLines(Console.In, 1024 * 1024);
            while (!stop.IsCancellationRequested && await lines.Read(stop.Token) is { } line) {
                Request? request = null;
                try {
                    if (line.Length > 1024 * 1024) throw new WoiException("ProtocolError", "Request exceeds 1 MiB.");
                    request = JsonSerializer.Deserialize<Request>(line, Wire.Json);
                    if (request == null || request.Version != Wire.Version ||
                        string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Method) || request.Parameters.ValueKind != JsonValueKind.Object)
                        throw new WoiException("ProtocolError", "Invalid monitor request.");
                    object result = request.Method switch {
                        "monitor.status" => session.View(),
                        "monitor.investigate" => investigations.Add(session.Investigate(request.Parameters.Deserialize<InvestigationRequest>(Wire.Json)!)),
                        "monitor.lifetimes" => session.Lifetimes(),
                        "monitor.mark" => session.Mark(request.Parameters.GetProperty("label").GetString() ?? ""),
                        "monitor.stop" => new { stopped = true },
                        _ when request.Method.StartsWith("investigation.", StringComparison.Ordinal) => InvestigationRpc.Handle(investigations, request, stop.Token),
                        _ => throw new WoiException("UnknownMethod", "The requested monitor operation is not supported.")
                    };
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                        new Response(request.Id, Wire.Version, "result", result), Wire.Json));
                    await Console.Out.FlushAsync();
                    if (request.Method == "monitor.stop") break;
                } catch (Exception ex) when (ex is WoiException or JsonException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NullReferenceException) {
                    var code = ex is WoiException woi ? woi.Code : "ProtocolError";
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                        new Response(request?.Id ?? "", Wire.Version, "error", Error: ex.Message, Code: code), Wire.Json));
                    await Console.Out.FlushAsync();
                }
            }
        } finally {
            stop.Cancel();
            try { await Task.WhenAll(sampling, watchdog); } catch (OperationCanceledException) { }
        }
        return 0;
    }
}
