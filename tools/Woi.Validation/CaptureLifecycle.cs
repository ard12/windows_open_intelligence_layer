using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Woi.Capture;
using Woi.Core;
using Woi.Trace;

namespace Woi.Validation;

// Exercises the shipping helper, not a simulated WPR runner. Run from an elevated
// same-account shell (CI Windows runners provide one). Only named sessions created
// by this validator or verified helper metadata are stopped.
internal static class CaptureLifecycle
{
    public static async Task<int> Run(string outputDirectory, string helperExecutable)
    {
        var output = Path.GetFullPath(outputDirectory);
        var executable = Path.GetFullPath(helperExecutable);
        Directory.CreateDirectory(output);
        var cases = new List<object>();
        string? activeCase = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var token = deadline.Token;
        try {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new WoiException("ElevationRequired", "Native lifecycle validation requires an elevated shell using the same Windows account.");
            if (!File.Exists(executable)) throw new FileNotFoundException("Publish the shipping capture helper first.", executable);

            activeCase = "automatic deadline";
            await using (var client = await CaptureClient.Connect(executable, token)) {
                var started = await client.Send("start", new CaptureOptions("Resources", 5), token);
                var saved = await WaitFor(client, x => x.Phase is CapturePhase.Saved or CapturePhase.Failed or CapturePhase.Cancelled, token);
                Require(saved.Phase == CapturePhase.Saved && saved.TracePath != null && saved.ElapsedSeconds >= 5,
                    "Automatic stop did not publish a completed recording: " + saved.Phase + ". " + saved.Message);
                var session = EtlImporter.Import(saved.TracePath!, Path.Combine(output, "deadline-cache"), null, token);
                using var reader = new EtlReader(session);
                var page = reader.QueryEvents(new(0, session.DurationMs), token);
                Require(session.SourceHash == saved.Sha256 && session.IndexedEvents > 0 && page.Events.Count > 0,
                    "Automatically saved recording could not be imported or inspected.");
                var again = await client.Send("stop", new { }, token);
                Require(again.Sha256 == saved.Sha256 && again.TracePath == saved.TracePath, "Repeated stop changed the saved recording.");
                cases.Add(new { name=activeCase, passed=true, elapsedSeconds=saved.ElapsedSeconds, indexedEvents=session.IndexedEvents,
                    sourceIntegrityPreserved=true, evidenceAvailable=true });
            }

            activeCase = "manual cancellation";
            await using (var client = await CaptureClient.Connect(executable, token)) {
                var started = await client.Send("start", new CaptureOptions("FileActivity", 30), token);
                await Task.Delay(1000, token);
                var cancelled = await client.Send("cancel", new { }, token);
                Require(cancelled.Phase == CapturePhase.Cancelled && cancelled.TracePath == null, "Cancellation was presented as a saved trace.");
                await AssertNoRecording(started.Instance, token);
                cases.Add(new { name=activeCase, passed=true, completedTraceAbsent=true });
            }

            activeCase = "pipe disconnect";
            string disconnectedInstance;
            await using (var client = await CaptureClient.Connect(executable, token)) {
                disconnectedInstance = (await client.Send("start", new CaptureOptions("Resources", 30), token)).Instance;
            }
            await WaitForMetadata(disconnectedInstance, x => x.Phase == CapturePhase.Cancelled, token);
            await AssertNoRecording(disconnectedInstance, token);
            cases.Add(new { name=activeCase, passed=true, completedTraceAbsent=true, ownedSessionStopped=true });

            activeCase = "helper crash and recovery";
            string crashedInstance;
            await using (var client = await CaptureClient.Connect(executable, token)) {
                crashedInstance = (await client.Send("start", new CaptureOptions("Resources", 30), token)).Instance;
                var metadata = ReadMetadata(crashedInstance);
                using var helper = Process.GetProcessById(metadata.HelperPid);
                Require(Math.Abs((helper.StartTime.ToUniversalTime() - metadata.HelperStartUtc).TotalMilliseconds) < 10 &&
                    string.Equals(Path.GetFullPath(helper.MainModule!.FileName), executable, StringComparison.OrdinalIgnoreCase),
                    "Refusing to terminate an unverified helper process.");
                helper.Kill(); // Intentional failure injection, limited to this owned helper.
                await helper.WaitForExitAsync(token);
            }
            await using (var recovered = await CaptureClient.Connect(executable, token)) {
                Require(ReadMetadata(crashedInstance).Phase == CapturePhase.Cancelled, "Reconnect did not recover the interrupted owned session.");
                await AssertNoRecording(crashedInstance, token);
                var restarted = await recovered.Send("start", new CaptureOptions("Resources", 30), token);
                Require(restarted.Phase == CapturePhase.Recording, "Capture could not restart after recovery.");
                await recovered.Send("cancel", new { }, token);
            }
            cases.Add(new { name=activeCase, passed=true, completedTraceAbsent=true, recoveryAndRestart=true });

            activeCase = "coexistence";
            // A separate named recording represents another tracing client. A profile
            // conflict is acceptable only if starting fails explicitly and that
            // client's recording survives cleanup and can still be saved and read.
            using (var owner = Process.GetCurrentProcess()) {
                await using var foreign = new CaptureController(new WprRunner(), Path.Combine(output, "separate-client"),
                    owner.Id, owner.StartTime.ToUniversalTime());
                await foreign.Start(new("Resources", 30), token);
                var helperStart = "started";
                await using (var client = await CaptureClient.Connect(executable, token)) {
                    try {
                        await client.Send("start", new CaptureOptions("Resources", 30), token);
                        await client.Send("cancel", new { }, token);
                    } catch (WoiException ex) when (ex.Code == "CaptureStartFailed") { helperStart = "explicit profile conflict"; }
                }
                var saved = await foreign.Stop(token);
                Require(saved.Phase == CapturePhase.Saved && saved.TracePath != null, "Helper cleanup disrupted the separate tracing client.");
                var session = EtlImporter.Import(saved.TracePath!, Path.Combine(output, "coexistence-cache"), null, token);
                Require(session.SourceHash == saved.Sha256 && session.IndexedEvents > 0,
                    "Separate-client recording was damaged or empty after helper cleanup.");
                cases.Add(new { name=activeCase, passed=true, helperStart, separateClientSaved=true, indexedEvents=session.IndexedEvents });
            }
            WriteResult(true, null);
            Console.WriteLine("Native deadline, cancellation, disconnect, crash recovery and coexistence passed.");
            return 0;
        } catch (Exception ex) {
            WriteResult(false, ex);
            Console.Error.WriteLine($"{activeCase}: {ex.Message}");
            return 1;
        }
        void WriteResult(bool success, Exception? error) =>
            File.WriteAllText(Path.Combine(output, "lifecycle.json"), JsonSerializer.Serialize(new {
                schema=1, success, utc=DateTime.UtcNow, os=Environment.OSVersion.VersionString,
                failedCase=success ? null : activeCase, error=error?.ToString(), cases,
                limitations=new[] { "Native transitions for fixed shipping profiles; not a Windows 11 clean-machine or all-provider compatibility claim.",
                    "Coexistence permits explicit start refusal when providers conflict; the separately owned recording must survive." }
            }, new JsonSerializerOptions(Wire.Json) { WriteIndented=true }));
    }

    private static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
    private static string RecordingDirectory(string instance)
    {
        var suffix = instance.StartsWith("WoiCapture", StringComparison.Ordinal) ? instance["WoiCapture".Length..] : "";
        Require(Guid.TryParseExact(suffix, "N", out _), "Invalid owned capture instance.");
        return Path.Combine(CaptureController.RecordingRoot, suffix);
    }
    private static CaptureMetadata ReadMetadata(string instance) =>
        JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(Path.Combine(RecordingDirectory(instance), "capture.json")), Wire.Json)
        ?? throw new IOException("Capture metadata is missing.");
    private static async Task<CaptureMetadata> WaitForMetadata(string instance, Func<CaptureMetadata, bool> done, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        while (true) {
            timeout.Token.ThrowIfCancellationRequested();
            var metadata = ReadMetadata(instance);
            if (done(metadata)) return metadata;
            await Task.Delay(250, timeout.Token);
        }
    }
    private static async Task<CaptureStatus> WaitFor(CaptureClient client, Func<CaptureStatus, bool> done, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        while (true) {
            var status = await client.Send("status", new { }, timeout.Token);
            if (done(status)) return status;
            await Task.Delay(250, timeout.Token);
        }
    }
    private static async Task AssertNoRecording(string instance, CancellationToken token)
    {
        Require(!File.Exists(Path.Combine(RecordingDirectory(instance), "recording.etl")), "A cancelled recording published an ETL.");
        var status = await new WprRunner().Run(["-status", "-instancename", instance], token);
        // WPR -status returns success with this text on the English test hosts;
        // -cancel instead reports the no-recording HRESULT. Neither access denial
        // nor an unrecognized/localized status is allowed to masquerade as a pass.
        Require(status.ExitCode == 0 && status.Output.Contains("WPR is not recording", StringComparison.OrdinalIgnoreCase) ||
            status.ExitCode != 0 && status.Output.Contains("0xc5583000", StringComparison.OrdinalIgnoreCase),
            "The cancelled WPR instance is still running or its status could not be verified: " + status.Output);
    }
}
