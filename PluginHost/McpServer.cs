using System.Reflection;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.PluginHost;

public sealed class McpServer : IDisposable
{
    public const string PanelUri = "ui://codex-accounts/panel.html";
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly VaultStore vault;
    private readonly string home;
    private readonly DeferredSwitchService deferred;
    private readonly PluginLoginService login;
    private readonly bool isolated;
    private readonly LiveClient? live;
    private readonly LiveClient? routing;
    private readonly AccountUsageService? usage;
    private readonly AccountResetService? resets;
    private readonly DesktopLoginSwitchService desktopSwitch;
    private readonly PanelPreferences panelPreferences;

    public McpServer(string? isolatedRoot = null)
    {
        isolated = isolatedRoot is not null;
        if (isolated)
        {
            var root = Path.GetFullPath(isolatedRoot!);
            home = Path.Combine(root, "codex-home");
            Directory.CreateDirectory(home);
            vault = new VaultStore(Path.Combine(root, "vault"));
            deferred = new DeferredSwitchService(vault, home, startWorker: _ => throw new UserFacingException("隔离验证不启动切换工作进程。"));
        }
        else
        {
            home = CodexRuntime.ResolveCodexHome();
            vault = CreateVault();
            deferred = new DeferredSwitchService(vault, home);
        }
        login = new PluginLoginService(vault, !isolated);
        live = isolated ? null : new LiveClient(vault);
        routing = isolated ? null : new LiveClient(vault, "routing");
        usage = isolated ? null : new AccountUsageService(vault, UsageCredentialAsync);
        resets = isolated ? null : new AccountResetService(vault, UsageCredentialAsync, id => usage?.Cached(id));
        desktopSwitch=new DesktopLoginSwitchService(vault,home,usage is null ? null : (id,token)=>usage.RefreshAsync(id,true,token));
        panelPreferences=new PanelPreferences(vault);
    }

    private async Task<LiveCredential> UsageCredentialAsync(string profile, CancellationToken cancellation)
    {
        // The desktop's own auth remains managed by Codex. Never refresh its saved copy.
        CodexRuntime.EnsureFileAuthStore(home);
        using var desktop = AuthSnapshot.Read(Path.Combine(home, "auth.json"));
        using var saved = new LiveCredentials(vault);
        var credentials = new RouterCredentials(vault, home, saved, desktop.Info.IdentityKey);
        return await credentials.GetAsync(profile, cancellation).ConfigureAwait(false);
    }
    public void Dispose() { usage?.Dispose(); resets?.Dispose(); }

    public static VaultStore CreateVault() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountSwitcher", "vault"));

    public async Task RunAsync(Stream input, Stream output)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line)
        {
            JsonElement? id = null;
            try
            {
                if (line.Length > 64 * 1024) throw new UserFacingException("MCP 请求过长。");
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 24 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var protocol) || protocol.GetString() != "2.0")
                    throw new UserFacingException("MCP 请求无效。");
                if (!root.TryGetProperty("id", out var requestId)) continue; // Initialized/cancel notifications do not have a reply.
                if (requestId.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) throw new UserFacingException("MCP 请求标识无效。");
                id = requestId.Clone();
                var method = root.GetProperty("method").GetString();
                var parameters = root.TryGetProperty("params", out var value) ? value : default;
                object result = method switch
                {
                    "initialize" => new { protocolVersion = "2025-06-18", capabilities = new { tools = new { listChanged = false }, resources = new { listChanged = false } }, serverInfo = new { name = "codex-accounts", version = typeof(McpServer).Assembly.GetName().Version?.ToString(3) }, instructions = "0.14.0 面板精简重复说明，提供十种显示语言；语言偏好按 Windows 用户加密保存，仅修改显示，不更改账号、模型、额度或轮换设置。普通启动组件独立运行，面板更新不等于机器组件更新。账号选择和适配加载不代表真实请求完成；依据实际完成记录核验。凭据不返回面板。" },
                    "ping" => new { },
                    "tools/list" => new { tools = Tools() },
                    "tools/call" => CallTool(parameters),
                    "resources/list" => new { resources = new[] { new { uri = PanelUri, name = "Codex 账号管理", mimeType = "text/html;profile=mcp-app", description = "加密账号列表和退出后切换面板" } } },
                    "resources/templates/list" => new { resourceTemplates = Array.Empty<object>() },
                    "resources/read" => ReadResource(parameters),
                    _ => throw new UserFacingException("不支持该 MCP 方法。")
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Options));
            }
            catch (Exception error)
            {
                var message = error is UserFacingException safe ? safe.Message : "MCP 操作失败，未输出原始请求或凭据。";
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32602, message } }, Options));
            }
        }
    }

    private object CallTool(JsonElement parameters)
    {
        var name = parameters.GetProperty("name").GetString();
        var args = parameters.TryGetProperty("arguments", out var input) ? input : default;
        try
        {
            string message;
            ResetResult? resetResult = null;
            switch (name)
            {
                case "accounts_set_language":
                    var language=panelPreferences.SetLanguage(StringArg(args,"language",20));
                    return new{content=new[]{new{type="text",text="Display language saved."}},structuredContent=new{languagePreference=language},isError=false};
                case "accounts_startup_status":
                    return new{content=new[]{new{type="text",text="普通启动组件状态已读取。"}},structuredContent=new{startupIntegration=isolated?new{state="isolated",message="合成验证不读取真实安装状态。"}:(object)WindowsStartupIntegration.Status()},isError=false};
                case "accounts_install_startup":
                    if(isolated||!BoolArg(args,"confirmed",false))throw new UserFacingException("请在账号面板确认安装普通启动组件。");
                    using(var installation=WindowsStartupIntegration.BeginInstall()){}
                    message="已启动 Windows 普通启动安装。准备完成后请保存任务并正常退出 Codex，随后自动重开。";
                    break;
                case "accounts_uninstall_startup":
                    if(isolated||!BoolArg(args,"confirmed",false))throw new UserFacingException("请在账号面板确认恢复普通启动。");
                    using(var removal=WindowsStartupIntegration.BeginUninstall()){}
                    message="已启动普通入口恢复，请完成 Windows 管理员确认。";
                    break;
                case "accounts_profile_details":
                    var profiles=vault.List().Select(account=>
                    {
                        using var savedProfile=vault.Read(account.Id);
                        return new {accountId=account.Id,profile=savedProfile.Profile};
                    }).Where(entry=>entry.profile.HasDetails).Select(entry=>new
                    {
                        entry.accountId,entry.profile.DisplayName,entry.profile.Username,entry.profile.AvatarUrl,source="login-cache"
                    }).ToArray();
                    return new {content=new[]{new{type="text",text="账号展示资料已读取；缺失的可选资料已省略。"}},structuredContent=new {profiles},isError=false};
                case "accounts_panel":
                case "accounts_list":
                case "accounts_live_status": message = "本机加密账号列表与直切连接状态。"; break;
                case "accounts_routing_status": message = "订阅转发连接状态；账号选择不代表已经完成真实请求。"; break;
                case "accounts_refresh_usage":
                    if (isolated || usage is null) throw new UserFacingException("隔离验证模式不查询真实套餐或用量。");
                    using (var usageDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
                    {
                        usage.RefreshAllAsync(vault.List().Select(account => account.Id), BoolArg(args, "force", false), usageDeadline.Token).GetAwaiter().GetResult();
                        resets!.RefreshAllAsync(vault.List().Select(account => account.Id), BoolArg(args, "force", false), usageDeadline.Token).GetAwaiter().GetResult();
                    }
                    message = "套餐、额度及重置卡查询已结束，失败或过期的数据已单独标记。账号选择和桌面登录保持原样。";
                    break;
                case "accounts_prepare_reset":
                    if (isolated || resets is null) throw new UserFacingException("隔离验证不查询或使用真实重置卡。");
                    using (var resetDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    {
                        var preview = resets.PrepareAsync(StringArg(args, "account_id", 64), StringArg(args, "credit_id", 256), resetDeadline.Token).GetAwaiter().GetResult();
                        return new { content = new[] { new { type = "text", text = preview.Message } }, structuredContent = new { resetPreview = preview }, isError = false };
                    }
                case "accounts_use_reset":
                    if (isolated || resets is null || usage is null) throw new UserFacingException("隔离验证不使用真实重置卡。");
                    var resetAccount = StringArg(args, "account_id", 64);
                    using (var resetDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
                    {
                        resetResult = resets.ConsumeAsync(resetAccount, StringArg(args, "confirmation_id", 32), BoolArg(args, "confirmed", false), resetDeadline.Token).GetAwaiter().GetResult();
                        try
                        {
                            usage.RefreshAsync(resetAccount, true, resetDeadline.Token).GetAwaiter().GetResult();
                            resets.RefreshAsync(resetAccount, true, resetDeadline.Token).GetAwaiter().GetResult();
                        }
                        catch { /* Preserve the official redemption result even if later metadata refresh fails. */ }
                    }
                    message = resetResult.Message;
                    break;
                case "accounts_capture_current":
                    var active = LiveStatus();
                    if (active.Enabled && !RoutingStatus().Enabled)
                    {
                        if (!active.Connected || active.CurrentAccountId is null) throw new UserFacingException("当前直切账号无法确认，请等待连接正常后重试。");
                        vault.Rename(active.CurrentAccountId, StringArg(args, "label", 60));
                    }
                    else
                    {
                        CodexRuntime.EnsureFileAuthStore(home);
                        using var current = AuthSnapshot.Read(Path.Combine(home, "auth.json"));
                        vault.Save(current, StringArg(args, "label", 60));
                    }
                    message = "当前账号已加密保存；没有切换登录。";
                    break;
                case "accounts_rename":
                    vault.Rename(StringArg(args, "account_id", 64), StringArg(args, "label", 60));
                    message = "本机账号名称已更新。";
                    break;
                case "accounts_remove":
                    if (!BoolArg(args,"confirmed",false)) throw new UserFacingException("请先确认移除指定的本机保存记录。");
                    var removeId = StringArg(args,"account_id",64);
                    var removalConnection = LiveStatus();
                    if (removalConnection.Enabled)
                    {
                        if (!RoutingStatus().Enabled || !removalConnection.RemovalSupported)
                            throw new UserFacingException("当前运行主机尚未加载移除功能。请正常退出 Codex，再从已更新的「Codex（订阅切换）」打开。");
                        routing!.RemoveAsync(removeId).GetAwaiter().GetResult();
                    }
                    else new AccountRemovalService(vault,home).RemoveOffline(removeId);
                    message = "已从本机保存列表移除；可点击「撤销上次移除」恢复。";
                    break;
                case "accounts_set_auto_rotation":
                    var rotationStatus=RoutingStatus();
                    if (isolated || routing is null || !rotationStatus.AutoRotationSupported)
                        throw new UserFacingException("当前运行主机尚未加载自动轮换功能，请正常重开更新后的专用入口。");
                    var rotationEnabled=BoolArg(args,"enabled",false);
                    var configured=routing.ConfigureRotationAsync(rotationEnabled).GetAwaiter().GetResult();
                    message=configured.Message;
                    break;
                case "accounts_restore_removed":
                    vault.RestoreRemoved(StringArg(args,"removal_id",32));
                    message = "已恢复到本机保存列表；当前使用的订阅没有改变。";
                    break;
                case "accounts_begin_login":
                    if (isolated) throw new UserFacingException("隔离验证模式不启动真实登录。");
                    desktopSwitch.WhileIdle(()=>login.Begin(StringArg(args, "label", 60)));
                    message = "已启动独立的官方网页登录，请在浏览器手动完成。当前 Codex 登录不变。";
                    break;
                case "accounts_login_status": message = "登录状态已更新。"; break;
                case "accounts_cancel_login": message = login.Cancel() ? "登录取消请求已提交。" : "没有正在等待的登录。"; break;
                case "accounts_request_switch":
                    if (isolated) throw new UserFacingException("隔离验证模式不启动真实切换。");
                    if(desktopSwitch.Status() is { } waitingDesktop && DesktopLoginSwitchService.IsActive(waitingDesktop))
                        throw new UserFacingException("已有桌面登录切换待下次启动应用，请先完成或取消。");
                    var request = desktopSwitch.WhileIdle(()=>deferred.Request(StringArg(args, "account_id", 64), BoolArg(args, "reopen", true)));
                    if (request.State == "failed") throw new UserFacingException(request.Message);
                    message = "已登记退出后切换。请在 10 分钟内结束任务并正常退出所有 Codex 桌面和后台；成功后按设置重新打开。当前会话账号尚未改变。";
                    break;
                case "accounts_switch_live":
                    if (isolated || live is null) throw new UserFacingException("隔离验证模式不执行真实账号直切。");
                    var changed = live.SwitchAsync(StringArg(args, "account_id", 64)).GetAwaiter().GetResult();
                    if (!changed.Connected) throw new UserFacingException("直切连接未能确认，目标账号不能标为已生效。");
                    message = changed.Message;
                    break;
                case "accounts_select_subscription":
                    if (isolated || routing is null) throw new UserFacingException("隔离验证模式不选择真实订阅账号。");
                    var selected = desktopSwitch.WhileIdle(()=>routing.SwitchAsync(StringArg(args, "account_id", 64)).GetAwaiter().GetResult());
                    if (!selected.Connected) throw new UserFacingException("订阅转发未连接，目标账号不能标为已选择。");
                    message = selected.Message;
                    break;
                case "accounts_request_desktop_switch":
                    if(isolated)throw new UserFacingException("隔离模式不登记或执行真实桌面登录切换。");
                    using(var desktopDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                        desktopSwitch.RequestAsync(StringArg(args,"account_id",64),BoolArg(args,"confirmed",false),desktopDeadline.Token).GetAwaiter().GetResult();
                    message="已登记同步切换桌面登录。当前登录未改动；请保存任务并正常退出，再在 15 分钟内从「Codex（订阅切换）」打开。";
                    break;
                case "accounts_cancel_desktop_switch":
                    if(isolated)throw new UserFacingException("隔离模式不操作真实桌面切换记录。");
                    message=desktopSwitch.Cancel(StringArg(args,"request_id",32)) ? "已取消同步切换桌面登录。" : "该桌面切换记录已结束或发生变化，请刷新查看。";
                    break;
                case "accounts_pending_status": message = "待切换状态已更新。"; break;
                case "accounts_cancel_switch": message = deferred.Cancel() ? "待切换已取消。" : "没有可取消的待切换任务。"; break;
                default: throw new UserFacingException("工具名称无效。");
            }
            var view = View(message, resetResult);
            return new { content = new[] { new { type = "text", text = message + "\n" + JsonSerializer.Serialize(view, Options) } }, structuredContent = view, isError = false };
        }
        catch (Exception error)
        {
            var message = error is UserFacingException safe ? safe.Message : "本机账号操作失败。没有输出认证文件、令牌或登录地址。";
            return new { content = new[] { new { type = "text", text = message } }, isError = true };
        }
    }

    private object View(string note, ResetResult? resetResult = null)
    {
        var session = LiveStatus();
        var subscription = RoutingStatus().Enabled;
        AccountInfo? current = null;
        AccountInfo? desktop = null;
        var currentError = "";
        try
        {
            if (File.Exists(Path.Combine(home, "auth.json"))) { using var auth = AuthSnapshot.Read(Path.Combine(home, "auth.json")); desktop = auth.Info; }
            if (session.Enabled)
            {
                if (session.Connected && session.CurrentAccountId is not null) { using var auth = vault.Read(session.CurrentAccountId); current = auth.Info; }
            }
            else if (File.Exists(Path.Combine(home, "auth.json"))) { using var auth = AuthSnapshot.Read(Path.Combine(home, "auth.json")); current = auth.Info; }
        }
        catch (UserFacingException) { currentError = "当前缓存暂时无法读取。"; }
        return new
        {
            accounts = vault.List().Select(account =>
            {
                var snapshot = usage?.Cached(account.Id);
                return new { id = account.Id, label = account.Label, maskedEmail = account.MaskedEmail,
                    plan = snapshot?.Plan ?? account.Plan, planSource = snapshot?.Plan is not null ? "server" : "cached",
                    usage = snapshot, resetCredits = resets?.Cached(account.Id), resetPending = resets?.Pending(account.Id),
                    isCurrent = account.Id == current?.IdentityKey, isDesktop = account.Id == desktop?.IdentityKey };
            }),
            current = current is null ? null : new { maskedEmail = current.MaskedEmail,
                plan = usage?.Cached(current.IdentityKey)?.Plan ?? current.Plan,
                planSource = usage?.Cached(current.IdentityKey)?.Plan is not null ? "server" : "cached" },
            pending = deferred.Status(),
            login = login.Status(),
            live = session,
            subscriptionRouting = subscription,
            desktop = desktop is null ? null : new { maskedEmail = desktop.MaskedEmail, plan = usage?.Cached(desktop.IdentityKey)?.Plan ?? desktop.Plan },
            desktopQuota = DesktopQuotaPreflight.Check(desktop is null ? null : usage?.Cached(desktop.IdentityKey),
                desktop is not null && current is not null && desktop.IdentityKey != current.IdentityKey),
            routingStartup = new RoutingStartupStore(vault).Status(),
            resetResult,
            lastRemoved = vault.LastRemoved(),
            desktopSwitch=desktopSwitch.Status(),
            desktopSwitchSupported=!isolated,
            languagePreference=panelPreferences.ReadLanguage(),
            startupIntegration=isolated?null:WindowsStartupIntegration.Status(),
            note = note + (currentError.Length == 0 ? "" : " " + currentError)
        };
    }
    private LiveStatus RoutingStatus() => routing?.StatusAsync().GetAwaiter().GetResult() ?? new LiveStatus(false, false, true, null, "订阅切换尚未接入。");
    private LiveStatus LiveStatus()
    {
        var subscription = RoutingStatus();
        if (subscription.Enabled) return subscription;
        var direct = live?.StatusAsync().GetAwaiter().GetResult();
        return direct?.Enabled == true ? direct : new LiveStatus(false, false, true, null, "未接入订阅切换，不能确认运行中的模型使用了所选账号。请查看启动状态。");
    }

    private static string StringArg(JsonElement args, string key, int maximum)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            throw new UserFacingException("缺少必要的账号参数。");
        var text = value.GetString()!.Trim();
        if (text.Length == 0 || text.Length > maximum || text.Any(char.IsControl)) throw new UserFacingException("账号参数无效。");
        return text;
    }
    private static bool BoolArg(JsonElement args, string key, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new UserFacingException("重新打开选项无效。");
        return value.GetBoolean();
    }

    private static object ReadResource(JsonElement parameters)
    {
        if (parameters.GetProperty("uri").GetString() != PanelUri) throw new UserFacingException("资源不存在。");
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexAccountSwitcher.AccountPanel.html") ?? throw new UserFacingException("账号面板资源缺失，请重新安装插件。");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        using var translations=Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexAccountSwitcher.PanelTranslations.json")??throw new UserFacingException("Panel translations missing.");
        using var translationReader=new StreamReader(translations,Encoding.UTF8);
        using var dictionary=JsonDocument.Parse(translationReader.ReadToEnd());
        var page=reader.ReadToEnd().Replace("/*__ACCOUNT_TRANSLATIONS__*/{}",JsonSerializer.Serialize(dictionary.RootElement),StringComparison.Ordinal);
        return new { contents = new[] { new { uri = PanelUri, mimeType = "text/html;profile=mcp-app", text = page, _meta = new Dictionary<string, object>
        {
            ["ui"] = new { csp = new { connectDomains = Array.Empty<string>(), resourceDomains = AccountProfile.AvatarOrigins }, prefersBorder = true },
            ["openai/ui"] = new { availableDisplayModes = new[] { "inline", "fullscreen" }, preferredDisplayMode = "fullscreen" }
        } } } };
    }

    private static object[] Tools()
    {
        var definitions = new List<object>();
        void Add(string name, string title, string description, bool readOnly, Dictionary<string, object>? properties = null, string[]? required = null, bool panel = false, bool appOnly = false,bool? destructive=null,bool? idempotent=null)
        {
            definitions.Add(new
            {
                name, title, description,
                inputSchema = new { type = "object", properties = properties ?? new Dictionary<string, object>(), required = required ?? Array.Empty<string>(), additionalProperties = false },
                annotations = new { readOnlyHint = readOnly, destructiveHint = destructive??!readOnly, idempotentHint = idempotent??readOnly, openWorldHint = name == "accounts_begin_login" },
                _meta = panel ? new Dictionary<string, object> { ["ui"] = new { resourceUri = PanelUri, visibility = new[] { "model", "app" } }, ["openai/outputTemplate"] = PanelUri, ["openai/ui"] = new { entrypoints = new[] { new { type = "global" }, new { type = "thread" } } } } : new Dictionary<string, object> { ["ui"] = new { visibility = appOnly ? new[] { "app" } : new[] { "model", "app" } } }
            });
        }
        var label = new { type = "string", minLength = 1, maxLength = 60, description = "本机账号备注名称" };
        var account = new { type = "string", pattern = "^[0-9a-f]{64}$", description = "accounts_list 返回的账号标识，不是登录令牌" };
        Add("accounts_set_language","Set display language","Panel-only display preference. Saves only the selected language for this Windows user; does not switch accounts or change quota settings.",false,
            new(){["language"]=new{type="string",@enum=PanelPreferences.Languages}},["language"],appOnly:true,destructive:false,idempotent:true);
        Add("accounts_startup_status","查看普通启动集成","查看本机启动组件安装或等待退出状态，不代表已完成真实推理或远程验证。",true);
        Add("accounts_install_startup","安装普通启动组件","仅供面板明确确认后调用。需要一次 Windows 管理员确认；准备后等待用户正常退出，再启用可恢复启动注册。",false,new(){["confirmed"]=new{type="boolean",@const=true}},["confirmed"],appOnly:true);
        Add("accounts_uninstall_startup","恢复普通启动","仅供面板确认后恢复本插件拥有的启动注册；不停止正在运行的客户端。",false,new(){["confirmed"]=new{type="boolean",@const=true}},["confirmed"],appOnly:true);
        Add("accounts_panel", "Codex 账号切换", "桌面可点击的账号面板。已接入直切模式且空闲时可直接切换；普通模式保留退出后切换。", true, panel: true);
        Add("accounts_list", "查看已保存账号", "返回脱敏身份、账号备注、套餐来源及额度缓存；时效与失败状态单独标明。", true);
        Add("accounts_profile_details","读取面板账号资料","仅供面板读取各保存账号登录时返回的可选名称、用户名和头像。缺失字段不推断，不查询浏览器其他登录，不返回凭据。",true,appOnly:true);
        Add("accounts_refresh_usage", "刷新套餐、额度和重置卡", "只查询已保存账号的官方套餐、额度、重置卡数量及到期时间；不执行模型推理、不使用重置卡、不切换账号。", true,
            new() { ["force"] = new { type = "boolean", @default = false } });
        Add("accounts_prepare_reset", "查看重置确认", "仅供账号面板点击。刷新并查看指定账号、指定重置卡的确认信息，此操作不消耗重置卡。", false,
            new() { ["account_id"] = account, ["credit_id"] = new { type = "string", minLength = 1, maxLength = 256 } }, ["account_id", "credit_id"], appOnly: true);
        Add("accounts_use_reset", "确认使用重置卡", "仅供面板在用户确认后调用。账号、卡与确认标识必须匹配；网络结果未知时仅核对原操作，禁止自动使用。", false,
            new() { ["account_id"] = account, ["confirmation_id"] = new { type = "string", pattern = "^[0-9a-f]{32}$" }, ["confirmed"] = new { type = "boolean", @const = true } },
            ["account_id", "confirmation_id", "confirmed"], appOnly: true);
        Add("accounts_capture_current", "保存当前 Codex 账号", "只在用户明确要求保存当前登录时调用。将本机已有订阅认证加密保存；不返回凭据。", false, new() { ["label"] = label }, ["label"]);
        Add("accounts_begin_login", "登录新账号", "只在用户要求添加账号时调用。后台官方登录打开浏览器，必须由用户手动完成。", false, new() { ["label"] = label }, ["label"]);
        Add("accounts_login_status", "查看登录状态", "查看手动登录是否完成，不暴露登录链接、验证码或凭据。", true);
        Add("accounts_cancel_login", "取消添加账号", "取消当前等待中的插件登录。", false);
        Add("accounts_rename", "重命名本机账号", "在用户指定名称时更新加密账号备注。", false, new() { ["account_id"] = account, ["label"] = label }, ["account_id", "label"]);
        Add("accounts_remove", "移除本机保存账号", "仅供面板在用户确认具体账号后调用。加密归档并从保存列表移除；当前使用账号、忙碌状态及待确认操作会拒绝。", false,
            new() { ["account_id"] = account, ["confirmed"] = new { type="boolean", @const=true } }, ["account_id","confirmed"], appOnly:true);
        Add("accounts_restore_removed", "撤销上次账号移除", "恢复最近移除的本机记录；不覆盖已存在的身份，不切换当前订阅。", false,
            new() { ["removal_id"] = new { type="string",pattern="^[0-9a-f]{32}$" } }, ["removal_id"], appOnly:true);
        Add("accounts_set_auto_rotation", "设置额度耗尽自动轮换", "仅供面板开关。默认关闭；开启后在明确的额度拒绝且尚无输出时，在同一请求内选择有额度且支持同模型的保存账号接续。不开启重置卡自动使用。", false,
            new() { ["enabled"] = new {type="boolean"} }, ["enabled"], appOnly:true);
        Add("accounts_request_desktop_switch","登记同步切换桌面登录","面板明确确认目标后登记 15 分钟有效的切换意图；用户正常退出后，下次专用入口启动前核对官方额度并应用，不启动退出等待进程，不关闭现有任务。",false,
            new(){["account_id"]=account,["confirmed"]=new {type="boolean",@const=true}},["account_id","confirmed"],appOnly:true);
        Add("accounts_cancel_desktop_switch","取消同步切换桌面登录","取消指定尚未应用的桌面登录切换意图。",false,
            new(){["request_id"]=new {type="string",pattern="^[0-9a-f]{32}$"}},["request_id"],appOnly:true);
        Add("accounts_request_switch", "退出后切换账号", "用户明确选择目标账号时创建 10 分钟有效的本机请求。只在用户退出所有 Codex 客户端和后台后执行，不关闭客户端。", false, new() { ["account_id"] = account, ["reopen"] = new { type = "boolean", @default = true } }, ["account_id"]);
        Add("accounts_pending_status", "查看待切换状态", "返回 pending/waiting/completed/failed/cancelled/expired 状态。只有 completed 表示磁盘切换完成，仍需 Codex 校验登录。", true);
        Add("accounts_cancel_switch", "取消退出后切换", "取消尚未执行的本机切换请求，不更改登录。", false);
        Add("accounts_live_status", "查看直切状态", "检查桌面连接、任务忙碌和已验证的当前账号；不返回后端凭据。", true);
        Add("accounts_switch_live", "直接切换账号", "用户点击选择目标账号后，在已绑定且空闲的直切后台切换并验证工作区。连接或任务状态不明时拒绝。", false,
            new() { ["account_id"] = account }, ["account_id"]);
        Add("accounts_routing_status", "查看订阅转发状态", "保留桌面登录的订阅转发状态。已选择不表示已经验证真实请求；不返回凭据。", true);
        Add("accounts_select_subscription", "切换模型请求订阅", "用户点击目标账号后选择下一次模型请求使用的订阅。响应进行中拒绝切换。保留模型、桌面登录和插件连接。", false,
            new() { ["account_id"] = account }, ["account_id"]);
        return definitions.ToArray();
    }
}
