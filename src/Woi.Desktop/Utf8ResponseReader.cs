using System.Buffers;
using System.IO;

namespace Woi.Desktop;

// A returned line borrows this reader's buffer until the next ReadLine or Dispose.
// Keep the JsonDocument lifetime inside that boundary. Reading UTF-8 directly avoids
// allocating a large UTF-16 string and encoding it again for every worker reply.
internal sealed class Utf8ResponseReader : IDisposable
{
    internal const int MaximumResponseBytes = 128 * 1024 * 1024;
    private readonly Stream stream;
    private readonly int maximumBytes;
    private byte[] buffer;
    private int start, end;
    private bool disposed;

    public Utf8ResponseReader(Stream stream, int maximumBytes = MaximumResponseBytes)
    {
        if (maximumBytes < 1 || maximumBytes > MaximumResponseBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.stream = stream;
        this.maximumBytes = maximumBytes;
        buffer = ArrayPool<byte>.Shared.Rent(Math.Min(8192, maximumBytes + 1));
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadLine(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (start > 0) {
            buffer.AsSpan(start, end - start).CopyTo(buffer);
            end -= start;
            start = 0;
        }
        var scanned = 0;
        while (true) {
            token.ThrowIfCancellationRequested();
            var newline = Array.IndexOf(buffer, (byte)'\n', scanned, end - scanned);
            if (newline >= 0) {
                if (newline > maximumBytes) throw new IOException("The worker response exceeds 128 MiB.");
                var length = newline > 0 && buffer[newline - 1] == '\r' ? newline - 1 : newline;
                start = newline + 1;
                return buffer.AsMemory(0, length);
            }
            if (end > maximumBytes) throw new IOException("The worker response exceeds 128 MiB.");
            scanned = end; // Search only newly received bytes; long lines remain linear.
            if (end == buffer.Length) {
                var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(maximumBytes + 1L, buffer.Length * 2L));
                buffer.AsSpan(0, end).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = larger;
            }
            var count = await stream.ReadAsync(buffer.AsMemory(end, Math.Min(buffer.Length - end, maximumBytes + 1 - end)), token).ConfigureAwait(false);
            if (count == 0) {
                if (end != 0) throw new IOException("The worker disconnected during a response.");
                return null;
            }
            end += count;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = [];
        // The owning Process/StreamReader closes the redirected pipe.
    }
}
