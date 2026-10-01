using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Woi.Core;
using Woi.Desktop;

namespace Woi.Validation;

// Development-only native ETL scale check. Enables only this tool's provider.
// This is not a shipping capture profile or representative resource workload.
internal static class NativeEnvelope
{
    public static async Task<int> Run(string directory, string executable, int count)
    {
        if (count < 1000 || count > EtlEnvelopeLimit)
            throw new ArgumentOutOfRangeException(nameof(count), "Choose 1,000 through 5,010,000 generated events.");
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "native-envelope.json");
        var source = Path.Combine(directory, "generated-" + Guid.NewGuid().ToString("N") + ".etl");
        var captureName = "WoiValidationEnvelope" + Guid.NewGuid().ToString("N");
        var clock = Stopwatch.StartNew();
        object? capture = null;
        Guid providerId = Guid.Empty;
        string? capturedEventName = null;
        try {
            if (TraceEventSession.IsElevated() != true)
                throw new WoiException("ElevationRequired", "The development native ETL generator requires an administrator test runner.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using (var provider = new EnvelopeProvider())
            using (var recording = new TraceEventSession(captureName, source) {
                BufferSizeMB=64, BufferQuantumKB=64, StopOnDispose=true
            }) {
                providerId = provider.Guid;
                recording.EnableProvider(providerId, TraceEventLevel.Verbose);
                for (var i = 0; i < 200 && !provider.IsEnabled(); i++)
                    await Task.Delay(10, timeout.Token);
                if (!provider.IsEnabled()) throw new IOException("The generated-event provider did not enable.");
                for (var i = 0; i < count; i++) {
                    provider.Pulse(i);
                    if ((i & 8191) == 0) timeout.Token.ThrowIfCancellationRequested();
                    // Keep generation bounded and allow the file logger to drain.
                    if ((i & 65535) == 65535) await Task.Delay(1, timeout.Token);
                }
                await Task.Delay(100, timeout.Token);
            }
            var generationMs = clock.Elapsed.TotalMilliseconds;
            var bytes = new FileInfo(source).Length;
            var sourceHash = Hash(source);
            if (bytes > Woi.Trace.EtlImporter.MaximumSourceBytes)
                throw new IOException("Generated native ETL exceeded the source-byte envelope.");
            var seen = new BitArray(count);
            long recorded = 0, sequenceSum = 0;
            using (var reference = new ETWTraceEventSource(source)) {
                reference.Dynamic.All += data => {
                    if (data.ProviderGuid != providerId || (int)data.ID != 1) return;
                    capturedEventName ??= data.EventName;
                    var sequence = Convert.ToInt32(data.PayloadByName("sequence"), CultureInfo.InvariantCulture);
                    if (sequence < 0 || sequence >= count || seen[sequence])
                        throw new IOException("A generated sequence was out of range or duplicated.");
                    seen[sequence] = true; recorded++; sequenceSum += sequence;
                    if ((recorded & 8191) == 0) timeout.Token.ThrowIfCancellationRequested();
                };
                reference.Process();
                if (reference.EventsLost != 0 || recorded != count || sequenceSum != (long)count * (count - 1) / 2)
                    throw new IOException("The native generator lost events; no completeness claim is valid.");
            }
            capture = new { generated=count, recorded, generationMs, sourceBytes=bytes, sourceHash, reportedLoss=0 };
            var start = new ProcessStartInfo(Path.GetFullPath(executable)) {
                UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true,
                RedirectStandardOutput=true, RedirectStandardError=true
            };
            start.ArgumentList.Add("--cache-root");
            start.ArgumentList.Add(Path.Combine(directory, "cache-" + Guid.NewGuid().ToString("N")));
            using var client = new WorkerClient(start);
            clock.Restart();
            Summary summary;
            try { summary = await client.Send<Summary>("import", new { path=source }, timeout.Token); }
            catch (WoiException error) when (count > Woi.Trace.EtlImporter.MaximumEvents && error.Code == "SizeLimit") {
                if (Directory.EnumerateFiles(start.ArgumentList[1], "complete.json", SearchOption.AllDirectories).Any())
                    throw new IOException("A rejected oversized import published a completed cache.");
                if (Hash(source) != sourceHash) throw new IOException("Import changed the original ETL.");
                Write(output, new { schema=1, success=true, kind="generated native ETL over-limit rejection",
                    capture, importMs=clock.Elapsed.TotalMilliseconds, rejectedCode=error.Code, originalPreserved=true,
                    limitations=Limitations });
                return 0;
            }
            var coldMs = clock.Elapsed.TotalMilliseconds;
            var session = summary.Session;
            if (session.IndexedEvents < count || session.IndexedEvents > Woi.Trace.EtlImporter.MaximumEvents ||
                session.ReportedLostEvents != 0 || session.Capabilities.Single(x => x.Category == "UnknownSchemas").Events < count)
                throw new IOException("The indexed import omitted generated events or reported loss.");
            if (summary.Incidents.Count != 0 || session.Capabilities.Single(x => x.Category == "CPU").Status != Availability.NotCaptured)
                throw new IOException("Unsupported generated events produced an unsupported resource diagnosis.");
            clock.Restart();
            var warm = await client.Send<Summary>("import", new { path=source }, timeout.Token);
            var warmMs = clock.Elapsed.TotalMilliseconds;
            if (warm.Session.IndexedEvents != session.IndexedEvents)
                throw new IOException("Cached reopening changed indexed counts.");
            var pages = new List<double>();
            var indices = new HashSet<long>();
            long? cursor = null;
            Observation? selected = null;
            for (var pageIndex = 0; pageIndex < 20; pageIndex++) {
                clock.Restart();
                var page = await client.Send<EventPage>("events", new { startMs=0, endMs=session.DurationMs, limit=500, cursor }, timeout.Token);
                pages.Add(clock.Elapsed.TotalMilliseconds);
                foreach (var value in page.Events) {
                    if (!indices.Add(value.Index)) throw new IOException("Paged native evidence repeated an index.");
                    if (value.Evidence.Provider == providerId.ToString() && value.Operation == capturedEventName && value.Category == "Unsupported")
                        selected ??= value;
                }
                if (!page.More) break;
                cursor = page.NextCursor ?? throw new IOException("Missing event cursor.");
            }
            if (selected == null) throw new IOException("Generated evidence was not reachable.");
            var evidence = await client.Send<EvidenceDetail>("evidence", new { index=selected.Index }, timeout.Token);
            if (!evidence.Payload.TryGetValue("sequence", out var recordedSequence) ||
                !int.TryParse(recordedSequence, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequenceValue) ||
                sequenceValue < 0 || sequenceValue >= count || !seen[sequenceValue] || evidence.Observation.Evidence.TraceHash != sourceHash ||
                evidence.Observation.Index != selected.Index)
                throw new IOException("Evidence did not preserve the generated payload and source coordinates.");
            var diagnostics = await client.Send<JsonElement>("diagnostics", new { }, timeout.Token);
            var peak = diagnostics.GetProperty("peakWorkingSetBytes").GetInt64();
            var derivedBytes = Directory.EnumerateFiles(session.CacheDirectory).Sum(x => new FileInfo(x).Length);
            if (Hash(source) != sourceHash) throw new IOException("Import changed the original ETL.");
            pages.Sort();
            var p95 = pages[Math.Max(0, (int)Math.Ceiling(pages.Count * .95) - 1)];
            Write(output, new { schema=1, success=true, kind="generated native ETL import and evidence scale",
                capture, session.IndexedEvents, coldImportMs=coldMs, verifiedCachedImportMs=warmMs,
                sequentialPageP95Ms=p95, pages=pages.Count, pagedEvents=indices.Count,
                peakWorkerBytes=peak, derivedBytes, originalPreserved=true, evidenceVerified=true,
                gates=new { coldUnderTwoMinutes=coldMs < 120000, cachedUnderFiveSeconds=warmMs < 5000,
                    pagesUnder300Ms=p95 < 300, workerUnderTwoGiB=peak < 2L*1024*1024*1024,
                    derivedUnderTwoGiB=derivedBytes <= Woi.Trace.EtlImporter.MaximumDerivedBytes },
                limitations=Limitations });
            return 0; // Measured budget failures remain in gates rather than hiding the report.
        } catch (Exception error) {
            Write(output, new { schema=1, success=false, kind="generated native ETL scale", capture,
                errorType=error.GetType().Name, error=error.Message, limitations=Limitations });
            return 1;
        }
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private const int EtlEnvelopeLimit = 5_010_000;
    private static readonly string[] Limitations = [
        "Generated application-provider events exercise native ETL/ETLX decoding and indexed evidence, not resource interpretation.",
        "Near-five-million indexed events do not certify every 1 GiB resource trace, rich stacks, or process churn.",
        "Generator/reference memory is separate from the measured importer worker.",
        "No kernel, process, file or network providers are enabled. No original ETL is included in default artifacts.",
        "This is development validation, not independent Windows 11 desktop or usability evidence."
    ];
    private static void Write(string output, object result) {
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions(Wire.Json) { WriteIndented=true }));
        Console.WriteLine(JsonSerializer.Serialize(result, Wire.Json));
    }

    [EventSource(Name=ProviderName)]
    private sealed class EnvelopeProvider : EventSource
    {
        public const string ProviderName = "Woi-Validation-Envelope";
        [Event(1, Level=EventLevel.Verbose)]
        public void Pulse(int sequence) => WriteEvent(1, sequence);
    }
}
