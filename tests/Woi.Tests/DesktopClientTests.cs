using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Woi.Core;
using Woi.Desktop;
using Xunit;

namespace Woi.Tests;

public sealed class DesktopClientTests
{
    [Fact]
    public async Task TypedRepliesSurviveDocumentDisposalAndDoNotRequireTheUiContext()
    {
        using var directory = new TemporaryDirectory();
        var context = SynchronizationContext.Current;
        WorkerClient client;
        try {
            SynchronizationContext.SetSynchronizationContext(new UnavailableUiContext());
            client = new WorkerClient(Start(monitor: true, directory.Root));
        } finally { SynchronizationContext.SetSynchronizationContext(context); }
        using (client) {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var replies = await Task.WhenAll(Enumerable.Range(0, 6)
                .Select(_ => client.Send<MonitorView>("monitor.status", new { }, timeout.Token)));
            Assert.All(replies, x => Assert.Equal("WindowsApiSnapshot", x.Source));
            // A caller's wrong response type fails that request, rather than
            // discarding the connection or retaining a disposed JsonDocument.
            await Assert.ThrowsAsync<JsonException>(() => client.Send<MonitorView>("monitor.lifetimes", new { }, timeout.Token));
            var status = await client.Send<MonitorView>("monitor.status", new { }, timeout.Token);
            Assert.Equal("WindowsApiSnapshot", status.Source);
            var stopped = await client.Send<JsonElement>("monitor.stop", new { }, timeout.Token);
            Assert.True(stopped.GetProperty("stopped").GetBoolean());
            Assert.True(client.Diagnostics.Replies >= 8);
        }
    }
    [Fact]
    public async Task PrecancelAndWorkerErrorsLeaveTheClientUsable()
    {
        using var directory = new TemporaryDirectory();
        using var client = new WorkerClient(Start(monitor: false, directory.Root));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Send<Summary>("summary", new { }, cancelled.Token));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 2; i++) {
            var error = await Assert.ThrowsAsync<WoiException>(() => client.Send<Summary>("summary", new { }, timeout.Token));
            Assert.Equal("NoTrace", error.Code);
        }
    }
    [FixtureFact]
    public async Task ImportProgressCancellationAndRestartWorkThroughTheDesktopClient()
    {
        using var directory = new TemporaryDirectory();
        using var client = new WorkerClient(Start(monitor: false, directory.Root));
        var decoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Progress += x => { if (x.Stage == "Decode") decoded.TrySetResult(); };
        var fixture = Path.Combine(Environment.GetEnvironmentVariable("WOI_FIXTURES")!, "allocator", "SimpleAllocator-dotnet6.etl");
        using var cancelled = new CancellationTokenSource();
        var import = client.Send<Summary>("import", new { path=fixture }, cancelled.Token);
        await decoded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var summary = await client.Send<Summary>("import", new { path=fixture }, timeout.Token);
        Assert.True(summary.Session.IndexedEvents > 0);
        Assert.NotEmpty(summary.Processes);
    }
    [FixtureFact]
    public async Task WorkerDeathReleasesAnOutstandingTypedReply()
    {
        using var directory = new TemporaryDirectory();
        using var client = new WorkerClient(Start(monitor: false, directory.Root));
        var decoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Progress += x => { if (x.Stage == "Decode") decoded.TrySetResult(); };
        var fixture = Path.Combine(Environment.GetEnvironmentVariable("WOI_FIXTURES")!, "allocator", "SimpleAllocator-dotnet6.etl");
        var import = client.Send<Summary>("import", new { path=fixture });
        await decoded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var worker = Process.GetProcessById(client.ProcessId);
        worker.Kill(true);
        await worker.WaitForExitAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => import.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Fact]
    public async Task Utf8RepliesKeepLineFramingUnicodeAndSerializationFailureAtomic()
    {
        using var output = new MemoryStream();
        await ProtocolOutput.Write(output, new("one", Wire.Version, "result", new { name="路径 · café 👋" }));
        await ProtocolOutput.Write(output, new("two", Wire.Version, "error", Error:"Missing", Code:"NotCaptured"));
        var bytes = output.ToArray();
        Assert.Equal((byte)'{', bytes[0]); // No UTF-8 BOM.
        var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("路径 · café 👋", first.RootElement.GetProperty("result").GetProperty("name").GetString());
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("NotCaptured", second.RootElement.GetProperty("code").GetString());
        var length = output.Length;
        await Assert.ThrowsAsync<ArgumentException>(() => ProtocolOutput.Write(output, new("bad", Wire.Version, "result", new { value=double.NaN })));
        Assert.Equal(length, output.Length);
    }
    private sealed class UnavailableUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => throw new InvalidOperationException("UI context is unavailable.");
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "woi-desktop-client-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static ProcessStartInfo Start(bool monitor, string cache)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "WindowsOpenIntelligence.slnx"))) root=root.Parent;
        var folder = root?.FullName ?? throw new IOException("Solution root not found.");
        var sdk = new[] { Path.Combine(folder, ".tools", "dotnet", "dotnet.exe"), Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "dotnet.exe") }.FirstOrDefault(x => x != null && File.Exists(x)) ?? "dotnet";
        var configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar) ? "Release" : "Debug";
        var start = new ProcessStartInfo(sdk) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true,
            RedirectStandardOutput=true, RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(folder, "src", "Woi.Worker", "bin", configuration, "net10.0-windows", "Woi.Worker.dll"));
        if (monitor) {
            using var parent = Process.GetCurrentProcess();
            foreach (var value in new[] { "--monitor", "--parent", parent.Id.ToString(), "--parent-start", parent.StartTime.ToUniversalTime().Ticks.ToString() })
                start.ArgumentList.Add(value);
        } else { start.ArgumentList.Add("--cache-root"); start.ArgumentList.Add(cache); }
        return start;
    }
}
