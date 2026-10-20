using System.Text.Json;
using Woi.Core;
using Xunit;

namespace Woi.Tests;

public sealed class ReportExportTests
{
    private const string Private = "PRIVATE_";
    private static readonly string Hash = new('a', 64);
    private static readonly DateTime Utc = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
    private static EvidenceReference Reference => new(Hash, "3.2.8.0", 7, 1000, 1, Private + "provider", Private + "event", 42, 7);
    private static Report TraceReport() => new(
        new(Private + "session", @"D:\PRIVATE_recording.etl", Hash, "3.2.8.0", Wire.AnalysisVersion, @"D:\PRIVATE_cache", Utc, 1000, 2, 100, 2,
            [new("CPU", Availability.Available, 10, Private + "detail")], [Private + "warning"], 1, 1024),
        new(0, 1000, Private + "process"), Private + "explanation includes D:\\PRIVATE_path and PRIVATE_endpoint and PRIVATE_command",
        [new(Private + "process", Private + "name", 42, 10, 5, .25, null, null, null, null, null, [Reference])],
        [new(Private + "incident", "CPU", Private + "title", 0, 100, Private + "description", Provenance.Observed, [Reference])],
        [Private + "limitation"], Findings: [new(Provenance.ResourceAttribution, Private + "finding", [Reference])]);
    private static InvestigationView InvestigationReport() => new(
        new(Private + "investigation", Private + "symptom", new(0, 1), "1:42:100", Private + "scope", Private + "notes",
            new(1, 1, 50, 1, 1, 100, 1, 200, 4096, 0, 0, null), null,
            [new(Private + "finding", Private + "title", Private + "statement", Provenance.Observed,
                [new(Private + "measurement", 1, Private + "unit", 1, Private + "source")],
                [new(Private + "source", 1000, Utc, "1:42:100")], [Private + "prerequisite"], [Private + "limit"], Private + "next")],
            [new("1:42:100", 42, Private + "process-name", 1, 100, 200, null, new(Private + "source", 1000, Utc, "1:42:100"))],
            [Private + "limit"], Private + "next"),
        new(Private + "source", "WindowsApiSnapshot", InvestigationAnalyzer.Version, Private + "machine", Private + "os", 2, 1000, 0, Utc), TraceReport());
    private static object[] Reports()
    {
        var investigation = InvestigationReport();
        var comparison = new Comparison(Private + "comparison", investigation.Investigation.Id, Private + "second", Private + "task", Private + "change",
            Private + "context", Private + "statement", [new("Accounted CPU time", "CPU-seconds", 1, 2, 100, true, Private + "limit")], [Private + "limit"]);
        var interval = new IntervalComparisonPage(Hash, new(new(0, 100), new(100, 200)), 0, 0,
            [new(Private + "label", "bytes", 1, 2, 1, 100, 10, 20, 10)], 1,
            [new(Private + "process", Private + "name", 42, new("Recorded physical disk transfers", "bytes", 1, 2, 1, 100, 10, 20, 10), [Reference], [Reference])], false, [Private + "limit"]);
        var stacks = CpuStackAggregation.Build(Hash, new(0, 10, View: CpuStackView.CallTree, ThreadId: "aaaaaaaaaaaaaaaa:t:1", HottestPath: true),
            [new(CoreTests.Obs(1, 1) with { ThreadId = "aaaaaaaaaaaaaaaa:t:1", Evidence = Reference },
                [new(Private + "frame-key", Private + "symbol", Private + "module-key", Private + "module", true)])], default);
        return [TraceReport(), investigation, new AttemptComparisonExport(comparison, investigation, investigation), interval, stacks];
    }
    [Theory]
    [InlineData("json")]
    [InlineData("markdown")]
    public void SafeDefaultsRemoveOpaqueTextAcrossEveryExportAndAttachment(string format)
    {
        foreach (var report in Reports()) {
            var original = JsonSerializer.Serialize(report, report.GetType(), Wire.Json);
            var output = ReportExport.Render(report, format);
            Assert.DoesNotContain(Private, output); Assert.Contains(Hash, output);
            Assert.Contains("exportPrivacy", output); Assert.Contains("etlxEventIndex", output);
            Assert.Contains("3.2.8.0", output);
            Assert.Equal(original, JsonSerializer.Serialize(report, report.GetType(), Wire.Json));
            var full = ReportExport.Render(report, format, new(true, true, true, true, true, true));
            Assert.Contains(Private, full);
        }
    }
    [Fact] public void ChoicesAreExplicitAndFreeTextCannotBypassExcludedIdentifierCategories()
    {
        var report = InvestigationReport();
        var output = ReportExport.Render(report, "json", new(IncludeNames: true));
        Assert.Contains(Private + "process-name", output); Assert.DoesNotContain(Private + "machine", output);
        Assert.DoesNotContain(Private + "notes", output); Assert.DoesNotContain("PRIVATE_recording", output);
        output = ReportExport.Render(report, "json", new(IncludeMachineMetadata: true, IncludePaths: true));
        Assert.Contains(Private + "machine", output); Assert.Contains("PRIVATE_recording", output);
        Assert.DoesNotContain(Private + "process-name", output); Assert.DoesNotContain(Private + "notes", output);
        Assert.Throws<WoiException>(() => ReportExport.Render(report, "json", new(IncludeFreeText: true)));
        var review = ReportExport.Review(report);
        Assert.Equal(6, review.Count); Assert.True(review.Single(x => x.Category == "Names").Fields > 0);
        Assert.True(review.Single(x => x.Category == "Paths").Fields > 0);
        Assert.True(review.Single(x => x.Category == "Free text").Fields > 0);
        Assert.Throws<WoiException>(() => ReportExport.Render(new { notes = "unsupported" }, "json"));
    }
    [Fact] public void ExactMeasurementsCoverageAndRecordedThreadScopeSurvivePrivacySelection()
    {
        using var report = JsonDocument.Parse(ReportExport.Render(TraceReport(), "json"));
        var root = report.RootElement;
        Assert.Equal(2, root.GetProperty("session").GetProperty("reportedLostEvents").GetInt32());
        var row = root.GetProperty("contributors")[0];
        Assert.Equal(10, row.GetProperty("cpuSamples").GetInt64());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("diskBytes").ValueKind);
        Assert.Equal(7, row.GetProperty("evidence")[0].GetProperty("etlxEventIndex").GetInt64());
        Assert.Equal(root.GetProperty("interval").GetProperty("processId").GetString(), row.GetProperty("processId").GetString());
        using var stacks = JsonDocument.Parse(ReportExport.Render(Reports()[^1], "json"));
        Assert.Equal("aaaaaaaaaaaaaaaa:t:1", stacks.RootElement.GetProperty("query").GetProperty("threadId").GetString());
    }
    [Fact] public void OptedInMarkdownNamesRemainTextRatherThanLinksOrHtml()
    {
        var report = TraceReport();
        report = report with { Contributors = [report.Contributors[0] with { Name = "![tracker](https://example.invalid/pixel)<img src=x>|`text`" }] };
        var markdown = ReportExport.Render(report, "markdown", new(IncludeNames: true));
        Assert.Contains("!\\[tracker\\]", markdown); Assert.Contains("&lt;img src=x&gt;\\|\\`text\\`", markdown);
        Assert.DoesNotContain("<img", markdown);
    }
    [Fact] public void WritesMatchThePreviewAndProtectEvidenceAndExistingFilesOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "woi-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try {
            var destination = Path.Combine(directory, "report.json");
            var report = TraceReport(); var preview = ReportExport.Render(report, "json");
            ReportExport.Write(destination, "json", report); Assert.Equal(preview, File.ReadAllText(destination));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => ReportExport.Write(destination, "json", report, token: cancellation.Token));
            Assert.Equal(preview, File.ReadAllText(destination)); Assert.Empty(Directory.GetFiles(directory, ".woi-export-*"));
            Assert.Throws<WoiException>(() => ReportExport.Write(Path.Combine(directory, "recording.etl"), "json", report));
            Assert.Throws<WoiException>(() => ReportExport.Write(destination, "json", report with { Session = report.Session with { SourcePath = destination } }));
            Assert.Throws<WoiException>(() => ReportExport.Write(destination, "json", report with { Session = report.Session with { CacheDirectory = directory } }));
            Assert.Equal(preview, File.ReadAllText(destination));
        } finally { Directory.Delete(directory, true); }
    }
    [Fact] public void BriefWindowsSharingLocksRetryAndPersistentLocksPreserveTheDestination()
    {
        var directory = Path.Combine(Path.GetTempPath(), "woi-export-lock-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try {
            var path = Path.Combine(directory, "report.json"); File.WriteAllText(path, "previous");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                var error = Record.Exception(() => ReportExport.Write(path, "json", TraceReport()));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.Equal("previous", File.ReadAllText(path));
                Assert.Empty(Directory.GetFiles(directory, ".woi-export-*"));
            }
            using var transient = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = new Thread(() => { Thread.Sleep(75); transient.Dispose(); });
            release.Start();
            try { ReportExport.Write(path, "json", TraceReport()); } finally { release.Join(); }
            Assert.Equal(ReportExport.Render(TraceReport(), "json"), File.ReadAllText(path));
        } finally { Directory.Delete(directory, true); }
    }
}
