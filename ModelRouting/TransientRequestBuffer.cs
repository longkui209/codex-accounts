using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Opt-in retry bytes stay in bounded process memory, are never spooled to disk, and are cleared on disposal.</summary>
internal sealed class TransientRequestBuffer : IDisposable
{
    private readonly MemoryStream data=new();
    public string? Model {get;private set;}
    public bool SelfContained {get;private set;}=true;
    public static async Task<TransientRequestBuffer> ReadAsync(Stream input,bool encoded,CancellationToken cancellation)
    {
        var result=new TransientRequestBuffer();var chunk=new byte[16384];
        try
        {
            int read;
            while((read=await input.ReadAsync(chunk,cancellation).ConfigureAwait(false))!=0)
            {
                if(result.data.Length+read>64L*1024*1024)throw new IOException();
                result.data.Write(chunk,0,read);
            }
            if(!encoded)
            {
                try
                {
                    using var document=JsonDocument.Parse(result.data.GetBuffer().AsMemory(0,checked((int)result.data.Length)),new JsonDocumentOptions {MaxDepth=64});
                    var root=document.RootElement;
                    if(root.TryGetProperty("model",out var model) && model.ValueKind==JsonValueKind.String && model.GetString() is {Length:>0 and <=128} name &&
                        name.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.'))result.Model=name;
                    if(root.TryGetProperty("previous_response_id",out var previous) && previous.ValueKind!=JsonValueKind.Null)result.SelfContained=false;
                }
                catch(Exception error) when(error is JsonException or InvalidOperationException){result.SelfContained=false;}
            }
            return result;
        }
        catch{result.Dispose();throw;}
        finally {CryptographicOperations.ZeroMemory(chunk);}
    }
    public HttpContent Content()=>new ByteArrayContent(data.GetBuffer(),0,checked((int)data.Length));
    public void Dispose(){CryptographicOperations.ZeroMemory(data.GetBuffer());data.Dispose();}
}
