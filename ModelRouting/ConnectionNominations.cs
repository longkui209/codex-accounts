using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CodexAccountSwitcher.ModelRouting;

internal interface IConnectionNominations
{
    IReadOnlySet<string> Names { get; }
}

/// <summary>
/// Reads only the first HTTP/1 header before Kestrel normalizes Connection.
/// The caller must close non-upgraded responses so a connection has one request.
/// Every byte remains unread for Kestrel; body framing stays with Kestrel.
/// </summary>
internal static class ConnectionNominations
{
    private const int MaximumHeaderBytes = 32 * 1024;
    private const int MaximumNameBytes = 256;
    private const int MaximumNominations = 64;

    internal static void Install(ListenOptions listener)
    {
        // This runs before HTTP connection limits, so bound unauthenticated peeks too.
        var headerSlots = new SemaphoreSlim(32, 32);
        listener.Use(next => async connection =>
        {
            if (!headerSlots.Wait(0))
            {
                connection.Abort(new ConnectionAbortedException("Local subscription request headers were refused."));
                return;
            }
            IConnectionNominations nominations;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                nominations = await PeekAsync(connection.Transport.Input, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                connection.Abort(new ConnectionAbortedException("Local subscription request headers were refused."));
                return;
            }
            finally { headerSlots.Release(); }
            // Public connection features are the fallback collection of HttpContext.Features.
            connection.Features.Set<IConnectionNominations>(nominations);
            await next(connection).ConfigureAwait(false);
        });
    }

    private static async Task<IConnectionNominations> PeekAsync(PipeReader input, CancellationToken cancellation)
    {
        while (true)
        {
            var result = await input.ReadAsync(cancellation).ConfigureAwait(false);
            var buffer = result.Buffer;
            byte[]? header = null;
            var complete = false;
            try
            {
                if (result.IsCanceled) throw Refusal();
                header = CopyCompleteHeader(buffer);
                if (header is not null)
                {
                    complete = true;
                    return Parse(header);
                }
                if (buffer.Length >= MaximumHeaderBytes || result.IsCompleted) throw Refusal();
            }
            finally
            {
                // Completed headers must be available immediately to the next PipeReader user.
                // Incomplete headers stay unconsumed while we wait for additional bytes.
                try { input.AdvanceTo(buffer.Start, complete ? buffer.Start : buffer.End); }
                finally { if (header is not null) CryptographicOperations.ZeroMemory(header); }
            }
        }
    }

    private static byte[]? CopyCompleteHeader(ReadOnlySequence<byte> buffer)
    {
        var limited = buffer.Slice(0, Math.Min(buffer.Length, MaximumHeaderBytes));
        var scanner = new SequenceReader<byte>(limited);
        if (!scanner.TryReadTo(out ReadOnlySequence<byte> header, "\r\n\r\n"u8, advancePastDelimiter: true)) return null;
        var length = checked((int)header.Length + 4);
        if (length > MaximumHeaderBytes) throw Refusal();
        return buffer.Slice(0, length).ToArray();
    }

    private static IConnectionNominations Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length > MaximumHeaderBytes || !header.EndsWith("\r\n\r\n"u8)) throw Refusal();
        var firstEnd = header.IndexOf("\r\n"u8);
        if (firstEnd <= 0) throw Refusal();
        var first = header[..firstEnd];
        if (!first.EndsWith(" HTTP/1.1"u8) && !first.EndsWith(" HTTP/1.0"u8)) throw Refusal();
        foreach (var value in first) if (value < 32 || value > 126) throw Refusal();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nominationCount = 0;
        var remaining = header[(firstEnd + 2)..];
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf("\r\n"u8);
            if (end < 0) throw Refusal();
            var line = remaining[..end];
            remaining = remaining[(end + 2)..];
            if (line.IsEmpty)
            {
                if (!remaining.IsEmpty) throw Refusal();
                return new Feature(names);
            }
            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || colon > MaximumNameBytes) throw Refusal();
            var key = line[..colon];
            foreach (var value in key) if (!TokenByte(value)) throw Refusal();
            var field = line[(colon + 1)..];
            foreach (var value in field)
                if (value < 32 && value != 9 || value == 127) throw Refusal();
            if (!AsciiEquals(key, "connection"u8)) continue;
            do
            {
                var comma = field.IndexOf((byte)',');
                var item = Trim(comma < 0 ? field : field[..comma]);
                if (item.IsEmpty || item.Length > MaximumNameBytes || ++nominationCount > MaximumNominations) throw Refusal();
                foreach (var value in item) if (!TokenByte(value)) throw Refusal();
                // Only field names become strings. Authentication values never do.
                names.Add(Encoding.ASCII.GetString(item));
                if (comma < 0) break;
                field = field[(comma + 1)..];
            } while (true);
        }
        throw Refusal();
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && value[0] is 32 or 9) value = value[1..];
        while (!value.IsEmpty && value[^1] is 32 or 9) value = value[..^1];
        return value;
    }

    private static bool TokenByte(byte value) => value is >= 48 and <= 57 or >= 65 and <= 90 or >= 97 and <= 122
        or 33 or 35 or 36 or 37 or 38 or 39 or 42 or 43 or 45 or 46 or 94 or 95 or 96 or 124 or 126;

    private static bool AsciiEquals(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> lower)
    {
        if (actual.Length != lower.Length) return false;
        for (var index = 0; index < actual.Length; index++)
        {
            var value = actual[index];
            if (value is >= 65 and <= 90) value += 32;
            if (value != lower[index]) return false;
        }
        return true;
    }

    internal static IReadOnlyList<string> ParserSelfTests()
    {
        var results = new List<string>();
        void Accept(string source, string label, params string[] expected)
        {
            var bytes = Encoding.ASCII.GetBytes(source);
            try
            {
                var parsed = Parse(bytes);
                if (!parsed.Names.SetEquals(expected)) throw Refusal();
                results.Add(label);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        void Reject(string source, string label)
        {
            var bytes = Encoding.ASCII.GetBytes(source);
            var rejected = false;
            try { _ = Parse(bytes); } catch (InvalidDataException) { rejected = true; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            if (!rejected) throw Refusal();
            results.Add(label);
        }
        const string request = "GET /backend-api/codex/responses HTTP/1.1\r\n";
        Accept(request + "Host: 127.0.0.1\r\nAuthorization: Bearer synthetic-never-a-real-token\r\nConnection: keep-alive, X-Synthetic-Hop\r\nConnection: OTHER-NOMINATION\r\n\r\n",
            "connection_names_preserved_before_normalization", "keep-alive", "x-synthetic-hop", "other-nomination");
        Accept(request + "Connection:\tUPGRADE ,\tX-Synthetic-Hop\t\r\n\r\n", "connection_ascii_case_and_OWS", "upgrade", "x-synthetic-hop");
        Accept(request + "Host: 127.0.0.1\r\n\r\n", "connection_absent_has_empty_names");
        Reject(request + "Connection: keep-alive,,x-hop\r\n\r\n", "connection_empty_token_rejected");
        Reject(request + "Connection: x/hop\r\n\r\n", "connection_invalid_token_rejected");
        Reject(request + "Connection: " + new string('x', MaximumNameBytes + 1) + "\r\n\r\n", "connection_oversize_name_rejected");
        Reject(request + "Connection: " + string.Join(',', Enumerable.Repeat("x-hop", MaximumNominations + 1)) + "\r\n\r\n", "connection_excess_names_rejected");
        Reject(request + "Connection: x-hop\nHost: 127.0.0.1\r\n\r\n", "connection_bare_LF_rejected");
        Reject(request + " Connection: x-hop\r\n\r\n", "connection_folded_header_rejected");
        Reject(request + "Connection x-hop\r\n\r\n", "connection_missing_colon_rejected");
        Reject(request + "X-Filler: " + new string('x', MaximumHeaderBytes) + "\r\n\r\n", "connection_oversize_header_rejected");
        return results;
    }

    private static InvalidDataException Refusal() => new("Local subscription request headers were refused.");
    private sealed class Feature(HashSet<string> names) : IConnectionNominations
    {
        public IReadOnlySet<string> Names { get; } = names;
    }
}
