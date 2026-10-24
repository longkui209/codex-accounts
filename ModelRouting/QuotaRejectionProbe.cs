using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Only a complete, explicit 429 quota rejection is eligible. All examined bytes remain replayable.</summary>
internal static class QuotaRejectionProbe
{
    internal sealed record Result(bool Exhausted,DateTimeOffset? ResetsAtUtc);
    public static async Task<Result> InspectAsync(HttpResponseMessage response,CancellationToken cancellation)
    {
        // Some native endpoints return SSE with a missing or nonstandard media type.
        // Determine eligibility from the first complete event, never the header alone.
        var sse=response.IsSuccessStatusCode;
        if(response.StatusCode!=HttpStatusCode.TooManyRequests && !sse || response.Content.Headers.ContentEncoding.Any(value=>value!="identity"))return new(false,null);
        var original=response.Content;
        var input=await original.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        var prefix=new byte[65537];var used=0;var ended=false;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while(used<prefix.Length)
            {
                var read=await input.ReadAsync(prefix.AsMemory(used),timeout.Token).ConfigureAwait(false);
                if(read==0){ended=true;break;}used+=read;
                if(sse)
                {
                    if(FirstSseEvent(prefix.AsSpan(0,used)) is { } admission)return admission;
                    if(DefinitelyNotEventOrJson(prefix,used))return new(false,null);
                }
            }
            if(!ended || used>65536)return new(false,null);
            using var document=JsonDocument.Parse(prefix.AsMemory(0,used),new JsonDocumentOptions {MaxDepth=12});
            return Error(document.RootElement);
        }
        catch {return new(false,null);}
        finally
        {
            var restored=new StreamContent(new PrefixStream(prefix,used,input,original));
            foreach(var header in original.Headers)restored.Headers.TryAddWithoutValidation(header.Key,header.Value);
            response.Content=restored;
        }
    }
    private static bool DefinitelyNotEventOrJson(byte[] buffer,int count)
    {
        var start=buffer.AsSpan(0,count).TrimStart((byte)32);
        return start.Length>=16&&!start.StartsWith("data:"u8)&&!start.StartsWith("event:"u8)&&!start.StartsWith(":"u8)&&start[0]!=(byte)'{';
    }
    private static Result? FirstSseEvent(ReadOnlySpan<byte> bytes)
    {
        while(bytes.Length>0)
        {
            var end=bytes.IndexOf((byte)10);if(end<0)return null;
            var line=bytes[..end];bytes=bytes[(end+1)..];
            if(!line.StartsWith("data:"u8))continue;
            try
            {
                using var document=JsonDocument.Parse(line[5..].ToArray());
                var root=document.RootElement;
                if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("type",out var type) || type.ValueKind!=JsonValueKind.String)return new(false,null);
                if(type.GetString() is "keepalive" or "ping")continue;
                return type.GetString() is "error" or "response.failed" ? Error(root) : new(false,null);
            }
            catch{return new(false,null);}
        }
        return null;
    }
    private static Result Error(JsonElement root)
    {
            if(root.ValueKind==JsonValueKind.Object && root.TryGetProperty("response",out var response) && response.ValueKind==JsonValueKind.Object)root=response;
            if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("error",out var error) || error.ValueKind!=JsonValueKind.Object ||
                !(error.TryGetProperty("type",out var type) && type.ValueKind==JsonValueKind.String && type.GetString()=="usage_limit_reached" ||
                    error.TryGetProperty("code",out var code) && code.ValueKind==JsonValueKind.String && code.GetString()=="usage_limit_reached"))return new(false,null);
            DateTimeOffset? reset=null;
            if(error.TryGetProperty("resets_at",out var timestamp) && timestamp.ValueKind==JsonValueKind.Number &&
                timestamp.TryGetInt64(out var seconds) && seconds is >0 and <253402300800)reset=DateTimeOffset.FromUnixTimeSeconds(seconds);
            return new(true,reset);
    }
    private sealed class PrefixStream(byte[] prefix,int count,Stream input,HttpContent owner) : Stream
    {
        private int position;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();
        public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int length)
        {
            if(position<count){var size=Math.Min(length,count-position);prefix.AsSpan(position,size).CopyTo(buffer.AsSpan(offset,size));position+=size;return size;}
            return input.Read(buffer,offset,length);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {
            if(position<count){var size=Math.Min(buffer.Length,count-position);prefix.AsMemory(position,size).CopyTo(buffer);position+=size;return ValueTask.FromResult(size);}
            return input.ReadAsync(buffer,cancellationToken);
        }
        public override void Flush()=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing){CryptographicOperations.ZeroMemory(prefix);owner.Dispose();}base.Dispose(disposing);}
    }
}
