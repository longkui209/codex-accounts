using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

internal static class OfficialMetadataJson
{
    // ResponseHeadersRead ends HttpClient's header timeout; all body reads need their own cancellation.
    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response,int maximumBytes,CancellationToken cancellation)
    {
        if(response.Content.Headers.ContentType?.MediaType!="application/json")throw new IOException();
        await using var input=await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer=new MemoryStream();var chunk=new byte[8192];
        try
        {
            int count;
            while((count=await input.ReadAsync(chunk,cancellation).ConfigureAwait(false))!=0)
            {
                if(buffer.Length+count>maximumBytes)throw new IOException();
                buffer.Write(chunk,0,count);
            }
            buffer.Position=0;
            return await JsonDocument.ParseAsync(buffer,new JsonDocumentOptions {MaxDepth=32},cancellation).ConfigureAwait(false);
        }
        finally {CryptographicOperations.ZeroMemory(chunk);CryptographicOperations.ZeroMemory(buffer.GetBuffer());}
    }
}
