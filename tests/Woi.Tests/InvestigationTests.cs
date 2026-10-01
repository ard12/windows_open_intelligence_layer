using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Woi.Core;
using Xunit;

namespace Woi.Tests;

public sealed class InvestigationTests
{
    private static readonly DateTime Utc = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static SourceDescriptor Source(string id = "session-a") => new(id, "WindowsApiSnapshot", InvestigationAnalyzer.Version,
        "recorded-machine", "recorded-os", 2, 1000, 10000, Utc);
    private static MonitorSnapshot Sample(int second, double? cpu = 50, double? read = 100, double? write = 200,
        double? interval = 1, long creation = 100, int unreadable = 0) =>
        new(10000 + second * 1000L, Utc.AddSeconds(second), second == 0 ? null : interval,
            second == 0 ? null : cpu, second == 0 ? null : read, second == 0 ? null : write,
            10000 - second * 100L, 20000 + second * 50L, 1 + unreadable, unreadable,
            [new($"1:42:{creation}", 42, 1, creation, "workload", second == 0 ? null : cpu,
                second == 0 ? null : read, second == 0 ? null : write, 1000 + second * 10, 1500)], []);
    private static InvestigationDocument Document(string id = "attempt-a", string source = "session-a", string? process = null) =>
        InvestigationAnalyzer.Analyze(Source(source), Enumerable.Range(0, 7).Select(s => Sample(s)).ToArray(), new(new(1, 6), "App delay", process), id);

    [Fact]
    public void PartialEdgesIntegrateMonotonicRateIntervalsAndExposeUnits()
    {
        var doc = InvestigationAnalyzer.Analyze(Source(), Enumerable.Range(0, 4).Select(s => Sample(s)).ToArray(), new(new(.5, 2.5), "Slow task"));
        var m = doc.Investigation.Metrics;
        Assert.Equal(2, m.DurationSeconds); Assert.Equal(2, m.CpuCoveredSeconds);
        Assert.Equal(50, m.MeanCpuPercent); Assert.Equal(2, m.CpuSeconds);
        Assert.Equal(200, m.ReadBytes); Assert.Equal(400, m.WriteBytes);
        Assert.Equal(-100, m.AvailableChangeBytes);
        Assert.Equal(3, doc.Investigation.Findings.Count);
        Assert.Contains(doc.Investigation.Limitations, x => x.Contains("proportional estimates"));
        Assert.All(doc.Investigation.Findings, f => { Assert.NotEmpty(f.Prerequisites); Assert.NotEmpty(f.Snapshots); Assert.NotEmpty(f.Limitations); Assert.NotEmpty(f.NextMeasurement); Assert.NotEqual(Provenance.Hypothesis, f.Provenance); });
        Assert.Contains(doc.Investigation.Limitations, x => x.Contains("not been confirmed healthy"));
    }
    [Fact]
    public void MissingCoverageAbstainsWithoutReassuringZeros()
    {
        var snapshots = new[] { Sample(0), Sample(1), Sample(2) }.Select(s => s with {
            CpuPercent = null, ReadBytesPerSecond = null, WriteBytesPerSecond = null, AvailablePhysicalBytes = null,
            CommitBytes = null, Processes = [] }).ToArray();
        var doc = InvestigationAnalyzer.Analyze(Source(), snapshots, new(new(0, 2), "Unknown slowdown"));
        Assert.Null(doc.Investigation.Metrics.CpuSeconds); Assert.Null(doc.Investigation.Metrics.ReadBytes);
        Assert.Empty(doc.Investigation.Findings); Assert.Contains("Start observing", doc.Investigation.NextMeasurement);
    }
    [Fact]
    public void ProcessScopeDoesNotMergeReusedPid()
    {
        var samples = new[] { Sample(0), Sample(1), Sample(2, null, null, null, creation: 200), Sample(3, creation: 200) };
        var doc = InvestigationAnalyzer.Analyze(Source(), samples, new(new(0, 3), "Application delay", "1:42:100"));
        Assert.Equal(1, doc.Investigation.Metrics.CpuSeconds);
        Assert.Equal(1, doc.Investigation.Metrics.CpuCoveredSeconds);
        Assert.Equal("1:42:100", Assert.Single(doc.Investigation.Contributors).LifetimeId);
        Assert.All(doc.Investigation.Findings.Where(x => x.Id != "memory").SelectMany(x => x.Snapshots), x => Assert.Equal("1:42:100", x.ProcessLifetimeId));
        Assert.All(doc.Investigation.Findings.Single(x => x.Id == "memory").Snapshots, x => Assert.Null(x.ProcessLifetimeId));
    }
    [Fact]
    public void ClockRollbackDoesNotAlterDurationRatesOrEndpointOrder()
    {
        var samples = new[] { Sample(0), Sample(1), Sample(2) with { Utc = Utc.AddHours(-1) } };
        var doc = InvestigationAnalyzer.Analyze(Source(), samples, new(new(0, 2), "Clock-change task"));
        Assert.Equal(2, doc.Investigation.Metrics.CpuSeconds);
        Assert.Equal(-200, doc.Investigation.Metrics.AvailableChangeBytes);
        Assert.Equal(Utc.AddHours(-1), doc.Investigation.Findings[1].Snapshots.Last().Utc);
    }
    [Fact]
    public void GapsBreakSustainedConditionAndReduceComparableCoverage()
    {
        var samples = new[] { Sample(0), Sample(1, 90), Sample(2, 90), Sample(4, null, null, null, null), Sample(5, 90) };
        var doc = InvestigationAnalyzer.Analyze(Source(), samples, new(new(0, 5), "Intermittent history"));
        Assert.Equal(3, doc.Investigation.Metrics.CpuCoveredSeconds);
        Assert.DoesNotContain("heuristic was met", doc.Investigation.Findings[0].Statement);
        Assert.Contains(doc.Investigation.Limitations, x => x.Contains("Less than 80%"));
    }
    [Fact]
    public void ConfiguredSustainedThresholdRequiresMeasuredDuration()
    {
        var samples = Enumerable.Range(0, 5).Select(s => Sample(s, 60)).ToArray();
        var doc = InvestigationAnalyzer.Analyze(Source(), samples, new(new(0, 4), "CPU work", Settings: new(50, 3)));
        Assert.Contains("50% / 3-second heuristic was met", doc.Investigation.Findings[0].Statement);
        var shortDoc = InvestigationAnalyzer.Analyze(Source(), samples, new(new(1.5, 4), "Short work", Settings: new(50, 3)));
        Assert.DoesNotContain("heuristic was met", shortDoc.Investigation.Findings[0].Statement);
    }
    [Fact]
    public void WeightedMeanUsesElapsedTimeInsteadOfSampleCount()
    {
        var samples = new[] { Sample(0), Sample(1, 20), Sample(3, 80, interval: 2) };
        var doc = InvestigationAnalyzer.Analyze(Source(), samples, new(new(0, 3), "Mixed intervals"));
        Assert.Equal(60, doc.Investigation.Metrics.MeanCpuPercent);
        Assert.Equal(3.6, doc.Investigation.Metrics.CpuSeconds!.Value, 8);
    }
    [Theory]
    [InlineData(-1, 2)] [InlineData(2, 2)] [InlineData(0, 99)]
    public void RejectsInvalidOrUnretainedIntervals(double start, double end) =>
        Assert.Throws<WoiException>(() => InvestigationAnalyzer.Analyze(Source(), new[] { Sample(0), Sample(1), Sample(2) }, new(new(start, end), "bad interval")));
    [Fact]
    public void RejectsOverlappingRatesAndConflictingLifetimeIdentity()
    {
        Assert.Equal("InvalidEvidence", Assert.Throws<WoiException>(() => InvestigationAnalyzer.Analyze(Source(),
            new[] { Sample(0), Sample(1), Sample(2, interval: 2) }, new(new(0, 2), "overlap"))).Code);
        var sample = Sample(1) with { Processes = [Sample(1).Processes[0] with { LifetimeId = "wrong" }] };
        Assert.Equal("InvalidEvidence", Assert.Throws<WoiException>(() => InvestigationAnalyzer.Analyze(Source(), new[] { Sample(0), sample }, new(new(0, 1), "identity"))).Code);
    }
    [Fact]
    public void SaveReopenRebuildsFindingsFromRecordedEvidenceAndMetadata()
    {
        using var temp = new Temp(); var path = Path.Combine(temp.Path, "saved.woi");
        var doc = Document();
        var forged = doc with { Investigation = doc.Investigation with { Findings = [doc.Investigation.Findings[0] with { Statement = "unsupported diagnosis" }] } };
        InvestigationStore.Save(path, forged);
        var reopened = InvestigationStore.Open(path);
        Assert.Equal(doc.Source, reopened.Source); Assert.Equal(doc.Investigation.Metrics, reopened.Investigation.Metrics);
        Assert.DoesNotContain(reopened.Investigation.Findings, x => x.Statement.Contains("unsupported diagnosis"));
        Assert.Equal(doc.Snapshots.Count, reopened.Snapshots.Count);
    }
    [Fact]
    public void CancellationPreservesExistingFileAndLeavesNoPartialSave()
    {
        using var temp = new Temp(); var path = Path.Combine(temp.Path, "saved.woi");
        InvestigationStore.Save(path, Document()); var prior = SHA256.HashData(File.ReadAllBytes(path));
        Assert.Throws<OperationCanceledException>(() => InvestigationStore.Save(path, Document("another"), new CancellationToken(true)));
        Assert.Equal(prior, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Single(Directory.GetFiles(temp.Path));
    }
    [Fact]
    public void DamagedPayloadAndUnknownSchemaFailExplicitly()
    {
        using var temp = new Temp(); var path = Path.Combine(temp.Path, "saved.woi");
        InvestigationStore.Save(path, Document());
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())) {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE document SET payload=X'7b7d'"; cmd.ExecuteNonQuery();
        }
        Assert.Equal("DamagedInvestigation", Assert.Throws<WoiException>(() => InvestigationStore.Open(path)).Code);
        InvestigationStore.Save(path, Document());
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())) {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE document SET schema_version=99"; cmd.ExecuteNonQuery();
        }
        Assert.Equal("UnsupportedInvestigation", Assert.Throws<WoiException>(() => InvestigationStore.Open(path)).Code);
    }
    [Fact]
    public void ComparisonRequiresSameWorkAndCrossSourceApplicationContext()
    {
        var a = Document("a", "first", "1:42:100"); var b = Document("b", "second", "1:42:100");
        Assert.Equal("TaskConfirmationRequired", Assert.Throws<WoiException>(() => ComparisonEngine.Compare(a, b, new("a", "b", "build", "change", false))).Code);
        Assert.Equal("ApplicationConfirmationRequired", Assert.Throws<WoiException>(() => ComparisonEngine.Compare(a, b, new("a", "b", "build", "change", true))).Code);
        var comparison = ComparisonEngine.Compare(a, b, new("a", "b", "build", "change", true, "C:\\tool.exe / version 1", true));
        Assert.True(comparison.Metrics.Single(x => x.Name == "Accounted CPU time").Comparable);
        Assert.Contains(comparison.Limitations, x => x.Contains("does not prove"));
    }
    [Fact]
    public void ComparisonSuppressesDifferencesForMissingRateCoverage()
    {
        var a = Document("a");
        var samples = Enumerable.Range(0, 7).Select(s => Sample(s, s == 1 ? 50 : null)).ToArray();
        var b = InvestigationAnalyzer.Analyze(Source(), samples, new(new(1, 6), "missing CPU"), "b");
        var comparison = ComparisonEngine.Compare(a, b, new("a", "b", "same work", "", true));
        var cpu = comparison.Metrics.Single(x => x.Name == "Accounted CPU time");
        Assert.False(cpu.Comparable); Assert.Null(cpu.ChangePercent);
        Assert.True(comparison.Metrics[0].Comparable);
    }
    [Fact]
    public void ComparisonRejectsDifferentScopeAndConflictingSourceMetadata()
    {
        var a = Document("a"); var b = Document("b", process: "1:42:100");
        Assert.Equal("IncompatibleScope", Assert.Throws<WoiException>(() => ComparisonEngine.Compare(a, b, new("a", "b", "same", "", true, "app", true))).Code);
        b = Document("b") with { Source = Source() with { Machine = "other-machine" } };
        Assert.Equal("EvidenceSourceMismatch", Assert.Throws<WoiException>(() => ComparisonEngine.Compare(a, b, new("a", "b", "same", "", true))).Code);
    }
    [Fact]
    public void EvidenceReferencesKeepSourceAndLifetimeSeparateFromEtlIndices()
    {
        var workspace = new InvestigationWorkspace(); var doc = Document(); workspace.Add(doc);
        var reference = doc.Investigation.Contributors[0].Evidence;
        var detail = workspace.Evidence(doc.Investigation.Id, reference);
        Assert.Equal("1:42:100", detail.Process!.LifetimeId);
        Assert.Equal(doc.Source.Id, detail.Source.Id);
        Assert.Throws<WoiException>(() => workspace.Evidence(doc.Investigation.Id, reference with { SourceId = "other" }));
        Assert.Throws<WoiException>(() => workspace.Evidence(doc.Investigation.Id, reference with { ProcessLifetimeId = "1:42:200" }));
        Assert.DoesNotContain("etlx", JsonSerializer.Serialize(detail, Wire.Json), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void WorkspacePinsAreBoundedAndClosingAllowsAnotherInvestigation()
    {
        var workspace = new InvestigationWorkspace();
        for (var n = 0; n < 8; n++) workspace.Add(Document("attempt" + n));
        Assert.Equal("WorkspaceLimit", Assert.Throws<WoiException>(() => workspace.Add(Document("ninth"))).Code);
        Assert.True(workspace.Close("attempt0")); workspace.Add(Document("ninth"));
    }
    [Fact]
    public void SavedSeparateSessionsCanCompareAfterRestart()
    {
        using var temp = new Temp();
        InvestigationStore.Save(Path.Combine(temp.Path, "first.woi"), Document("a", "first"));
        InvestigationStore.Save(Path.Combine(temp.Path, "second.woi"), Document("b", "second"));
        var workspace = new InvestigationWorkspace(); workspace.Open(Path.Combine(temp.Path, "first.woi")); workspace.Open(Path.Combine(temp.Path, "second.woi"));
        var comparison = workspace.Compare(new("a", "b", "repeat build", "changed setting", true));
        Assert.Equal(6, comparison.Metrics.Count); Assert.True(comparison.Metrics[0].Comparable);
        workspace.Export("a", Path.Combine(temp.Path, "report.md"), "markdown");
        var report = File.ReadAllText(Path.Combine(temp.Path, "report.md"));
        Assert.Contains("no ETLX event index", report); Assert.Contains("Raw ETL", report);
    }
    [Fact]
    public void SubsecondSelectionKeepsRateEndpointEvidence()
    {
        var doc = InvestigationAnalyzer.Analyze(Source(), new[] { Sample(0), Sample(1), Sample(2) }, new(new(.1, .2), "brief interval"));
        Assert.Equal(.1, doc.Investigation.Metrics.CpuSeconds!.Value, 8);
        Assert.All(doc.Investigation.Findings, x => Assert.NotEmpty(x.Snapshots));
        var other = doc with { Investigation = doc.Investigation with { Id = "other" } };
        var comparison = ComparisonEngine.Compare(doc, other, new(doc.Investigation.Id, "other", "brief work", "", true));
        Assert.True(comparison.Metrics[0].Comparable);
        Assert.All(comparison.Metrics.Skip(1), x => Assert.False(x.Comparable));
    }
    [Fact]
    public void ReportExportExcludesOriginalPathsAndAttachmentKeepsSourceFingerprint()
    {
        using var temp = new Temp(); var workspace = new InvestigationWorkspace(); var doc = Document(); workspace.Add(doc);
        var hash = new string('a', 64);
        var session = new TraceSession("etl", @"C:\private\recording.etl", hash, "parser", "analysis", @"C:\private\cache", Utc, 1000, 2, 1, 0, [], [], 1, 1);
        var reference = new EvidenceReference(hash, "parser", 7, 12, 500, "provider", "event", 42, 43);
        var report = new Report(session, new(0, 1000), "resource attribution", [new("lifetime", "workload", 42, 1, 1, 1, null, null, null, null, null, [reference])], [], []);
        workspace.Attach(doc.Investigation.Id, report);
        var path = System.IO.Path.Combine(temp.Path, "report.json"); workspace.Export(doc.Investigation.Id, path, "json");
        var output = File.ReadAllText(path);
        var exported = JsonSerializer.Deserialize<InvestigationView>(output, Wire.Json)!;
        Assert.Equal("", exported.AttachedTrace!.Session.SourcePath); Assert.Equal("", exported.AttachedTrace.Session.CacheDirectory);
        Assert.DoesNotContain("recording.etl", output); Assert.Contains(hash, output); Assert.Contains("etlxEventIndex", output);
        Assert.Throws<WoiException>(() => workspace.Attach(doc.Investigation.Id, report with { Session = session with { SourceHash = new string('b', 64) } }));
    }
    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "woi-investigation-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
