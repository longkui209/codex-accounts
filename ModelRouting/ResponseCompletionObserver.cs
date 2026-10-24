using System.IO.Compression;
using System.IO.Pipelines;
using System.Text.Json;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Observes only bytes already delivered. Nothing is logged and wire bytes are never rewritten.</summary>
internal sealed class ResponseCompletionObserver : IAsyncDisposable
{
    private readonly TerminalParser parser;
    private readonly Pipe? pipe;
    private readonly Task? decode;
    private bool invalid;
    private bool finished;
    public string Format { get; }
    public string Encoding { get; }
    public bool Completed => finished && !invalid && parser.Completed;
    public ResponseCompletionObserver(string? mediaType, IEnumerable<string> encodings)
    {
        Format = mediaType switch { "text/event-stream" => "sse", "application/json" => "json", _ => "other" };
        var values = encodings.Select(value => value.Trim().ToLowerInvariant()).Where(value => value != "identity").ToArray();
        Encoding = values.Length == 0 ? "identity" : values.Length == 1 && values[0] is "gzip" or "br" or "deflate" ? values[0] : "unsupported";
        parser = new TerminalParser(Format);
        if (Encoding == "unsupported") { invalid = true; return; }
        if (Encoding == "identity") return;
        pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 128 * 1024, resumeWriterThreshold: 64 * 1024, useSynchronizationContext: false));
        decode = DecodeAsync();
    }
    public async ValueTask ObserveDeliveredAsync(ReadOnlyMemory<byte> bytes)
    {
        if (invalid || finished) return;
        if (pipe is null) { parser.Push(bytes.Span); return; }
        try { await pipe.Writer.WriteAsync(bytes).ConfigureAwait(false); }
        catch { invalid = true; }
    }
    public async Task FinishAsync()
    {
        if (finished) return;
        if (pipe is not null)
        {
            await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            await decode!.ConfigureAwait(false);
        }
        parser.Finish(); finished = true;
    }
    private async Task DecodeAsync()
    {
        var buffer = new byte[16384];
        try
        {
            await using var source = pipe!.Reader.AsStream(leaveOpen: true);
            await using Stream decoder = Encoding switch
            {
                "gzip" => new GZipStream(source, CompressionMode.Decompress, leaveOpen: true),
                "br" => new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true),
                _ => new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true)
            };
            int count;
            while ((count = await decoder.ReadAsync(buffer).ConfigureAwait(false)) != 0) parser.Push(buffer.AsSpan(0,count));
        }
        catch { invalid = true; }
        finally { Array.Clear(buffer); await pipe!.Reader.CompleteAsync().ConfigureAwait(false); }
    }
    public async ValueTask DisposeAsync() { await FinishAsync().ConfigureAwait(false); parser.Dispose(); }

    private sealed class TerminalParser(string format) : IDisposable
    {
        private readonly MemoryStream line = new();
        private bool oversized;
        public bool Completed { get; private set; }
        public void Push(ReadOnlySpan<byte> bytes)
        {
            if (format == "json") { foreach (var value in bytes) Append(value); return; }
            foreach (var value in bytes)
            {
                if (value != 10) { Append(value); continue; }
                ParseLine(); line.SetLength(0); oversized = false;
            }
        }
        private void Append(byte value)
        {
            if (line.Length < 16 * 1024 * 1024 && !oversized) line.WriteByte(value);
            else oversized = true;
        }
        public void Finish()
        {
            if (format == "json") ParseJson(line.GetBuffer().AsSpan(0,checked((int)line.Length)), true);
            else if (line.Length != 0) ParseLine();
        }
        private void ParseLine()
        {
            if (oversized) return;
            var bytes = line.GetBuffer().AsSpan(0,checked((int)line.Length));
            if (bytes.StartsWith("data:"u8)) ParseJson(bytes[5..],false);
        }
        private void ParseJson(ReadOnlySpan<byte> bytes, bool jsonResponse)
        {
            if (oversized) return;
            try
            {
                using var document = JsonDocument.Parse(bytes.ToArray());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (root.TryGetProperty("type",out var type) && type.ValueKind == JsonValueKind.String)
                {
                    if (type.GetString() == "response.completed") Completed = true;
                    else if (type.GetString() is "response.failed" or "response.incomplete" or "error") Completed = false;
                }
                else if (jsonResponse && root.TryGetProperty("object",out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "response" &&
                    root.TryGetProperty("status",out var status) && status.ValueKind == JsonValueKind.String) Completed = status.GetString() == "completed";
            }
            catch (JsonException) { }
        }
        public void Dispose() { Array.Clear(line.GetBuffer()); line.Dispose(); }
    }
}
