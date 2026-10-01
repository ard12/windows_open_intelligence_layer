using System.Text;
using System.Text.Json;
using Woi.Desktop;
using Xunit;

namespace Woi.Tests;

public sealed class Utf8ResponseReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(65536)]
    public async Task FragmentedUnicodeAndMultipleRepliesKeepTheirBoundaries(int chunk)
    {
        var name = string.Concat(Enumerable.Repeat("路径 · café 👋", 6000));
        var first = JsonSerializer.SerializeToUtf8Bytes(new { id="first", name });
        var second = JsonSerializer.SerializeToUtf8Bytes(new { id="second", name="smaller" });
        var input = first.Concat("\r\n"u8.ToArray()).Concat(second).Concat("\n"u8.ToArray()).ToArray();
        using var stream = new FragmentedStream(input, chunk);
        using var reader = new Utf8ResponseReader(stream);
        using (var document = JsonDocument.Parse((await reader.ReadLine())!.Value)) {
            Assert.Equal("first", document.RootElement.GetProperty("id").GetString());
            Assert.Equal(name, document.RootElement.GetProperty("name").GetString());
        }
        using (var document = JsonDocument.Parse((await reader.ReadLine())!.Value)) {
            Assert.Equal("second", document.RootElement.GetProperty("id").GetString());
            Assert.Equal("smaller", document.RootElement.GetProperty("name").GetString());
        }
        Assert.Null(await reader.ReadLine());
    }

    [Fact]
    public async Task ExactLimitAndCoalescedRepliesAreAcceptedButLargerLinesFail()
    {
        using var stream = new MemoryStream("12345678\nok\n123456789\n"u8.ToArray());
        using var reader = new Utf8ResponseReader(stream, 8);
        Assert.Equal("12345678", Encoding.UTF8.GetString((await reader.ReadLine())!.Value.Span));
        Assert.Equal("ok", Encoding.UTF8.GetString((await reader.ReadLine())!.Value.Span));
        await Assert.ThrowsAsync<IOException>(async () => await reader.ReadLine());
    }

    [Fact]
    public async Task InterruptedReplyFailsAndCancellationAndDisposalAreExplicit()
    {
        using var stream = new MemoryStream("{\"id\":\"incomplete\""u8.ToArray());
        using var reader = new Utf8ResponseReader(stream);
        await Assert.ThrowsAsync<IOException>(async () => await reader.ReadLine());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadLine(cancelled.Token));
        reader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await reader.ReadLine());
        Assert.True(stream.CanRead); // Pipe lifetime belongs to WorkerClient.
    }

    private sealed class FragmentedStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], token);
    }
}
