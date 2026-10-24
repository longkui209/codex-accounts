using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.LiveSession;

public sealed class LiveClient(VaultStore vault, string scope = "live")
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly LiveState state = new(vault, scope);
    public Task<LiveStatus> StatusAsync(CancellationToken cancellation = default) => SendAsync("status", null, cancellation);
    public Task<LiveStatus> SwitchAsync(string accountId, CancellationToken cancellation = default)
    {
        if (!LiveState.ValidId(accountId)) throw new UserFacingException("账号标识无效。");
        return SendAsync("switch", accountId, cancellation);
    }
    public Task<LiveStatus> RemoveAsync(string accountId, CancellationToken cancellation = default)
    {
        if (!LiveState.ValidId(accountId)) throw new UserFacingException("账号标识无效。");
        return SendAsync("remove",accountId,cancellation);
    }
    public Task<LiveStatus> ConfigureRotationAsync(bool enabled,CancellationToken cancellation=default)
        => SendAsync("auto-rotation",null,cancellation,enabled);
    private async Task<LiveStatus> SendAsync(string operation, string? accountId, CancellationToken cancellation,bool? enabled=null)
    {
        var descriptor = state.Descriptor();
        if (descriptor is null)
        {
            if (operation != "status") throw new UserFacingException(scope == "routing" ? "请先正常退出 Codex，再从「Codex（订阅切换）」入口打开。" : "请先正常退出 Codex，再从「Codex（账号直切）」入口打开。");
            return new LiveStatus(false, false, true, null, scope == "routing" ? "订阅切换尚未接入原生桌面。" : "请从「Codex（账号直切）」打开以启用免重启切换。");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(operation == "status" ? 4 : 45));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", descriptor.ControlPipe, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(2500, timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != descriptor.OwnerPid)
                throw new IOException();
            var request = JsonSerializer.SerializeToUtf8Bytes(new { operation, accountId, enabled }, Options);
            await pipe.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            var bytes = new byte[16 * 1024]; var used = 0;
            while (used < bytes.Length)
            {
                if (await pipe.ReadAsync(bytes.AsMemory(used, 1), timeout.Token).ConfigureAwait(false) == 0) break;
                if (bytes[used++] == '\n') break;
            }
            if (used == 0 || used == bytes.Length) throw new IOException();
            var response = JsonSerializer.Deserialize<ControlReply>(bytes.AsSpan(0, used), Options) ?? throw new IOException();
            if (!response.Ok) throw new UserFacingException(response.Message is { Length: >0 and <=512 } ? response.Message : "直切操作未完成。");
            return response.Status ?? throw new IOException();
        }
        catch (UserFacingException) { throw; }
        catch
        {
            if (operation != "status") throw new UserFacingException("直切请求未能确认完成，请刷新查看当前账号，不要重复提交切换。");
            return new LiveStatus(true, false, true, null, "直切连接暂不可用；当前账号未能确认。");
        }
    }
    internal sealed record ControlReply(bool Ok, LiveStatus? Status, string? Message);
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
