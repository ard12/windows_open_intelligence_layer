using System.Text.Json;
using Woi.Core;

internal static class ProtocolOutput
{
    private static readonly byte[] NewLine = [10];
    public static async Task Write(Stream output, Response value)
    {
        // Complete serialization before writing so a malformed result cannot emit
        // a partial JSON reply. UTF-8 bytes avoid the intermediate UTF-16 string.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json);
        await output.WriteAsync(bytes).ConfigureAwait(false);
        await output.WriteAsync(NewLine).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
}
