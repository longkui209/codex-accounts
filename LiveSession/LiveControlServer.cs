using System.IO.Pipes;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.LiveSession;

public sealed class LiveControlServer(string pipeName, Func<CancellationToken,Task<LiveStatus>> status,
    Func<string,CancellationToken,Task<LiveStatus>> change,
    Func<string,CancellationToken,Task<LiveStatus>>? remove = null,
    Func<bool,CancellationToken,Task<LiveStatus>>? configureRotation = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public async Task RunAsync(CancellationToken cancellation)
    {
        var clients = new List<Task>();
        using var slots = new SemaphoreSlim(8);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await slots.WaitAsync(cancellation);
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(cancellation); }
                catch { slots.Release(); await pipe.DisposeAsync(); throw; }
                clients.RemoveAll(task => task.IsCompleted);
                clients.Add(ServeAsync(pipe, slots, cancellation));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients); }
    }
    private async Task ServeAsync(NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken shutdown)
    {
        await using var owned = pipe;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        object reply;
        try
        {
            var bytes = new byte[4096]; var used = 0;
            while (used < bytes.Length)
            {
                if (await pipe.ReadAsync(bytes.AsMemory(used, 1), timeout.Token) == 0) break;
                if (bytes[used++] == '\n') break;
            }
            if (used == 0 || used == bytes.Length) throw new UserFacingException("直切请求无效。");
            using var json = JsonDocument.Parse(bytes.AsMemory(0, used), new JsonDocumentOptions { MaxDepth = 3 });
            var root = json.RootElement;
            var operation = root.GetProperty("operation").GetString();
            var value = operation switch
            {
                "status" => await status(timeout.Token),
                "switch" => await change(root.GetProperty("accountId").GetString() ?? "", timeout.Token),
                "remove" when remove is not null => await remove(root.GetProperty("accountId").GetString() ?? "", timeout.Token),
                "auto-rotation" when configureRotation is not null => await configureRotation(root.GetProperty("enabled").GetBoolean(),timeout.Token),
                _ => throw new UserFacingException("不支持该账号操作。")
            };
            reply = new LiveClient.ControlReply(true, value, null);
        }
        catch (Exception error)
        {
            reply = new LiveClient.ControlReply(false, null, error is UserFacingException safe ? safe.Message : "直切操作未完成，当前状态需要刷新确认。");
        }
        try
        {
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(reply, Options), timeout.Token);
            await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
        }
        catch { }
        finally { slots.Release(); }
    }
}
