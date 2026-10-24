using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public sealed record AccountRow(AccountSummary Summary, bool IsCurrent)
{
    public string Id => Summary.Id;
    public string Label => Summary.Label;
    public string MaskedEmail => Summary.MaskedEmail;
    public string Plan => Summary.Plan.ToUpperInvariant();
    public string StateText => IsCurrent ? "当前缓存" : Summary.ExpiresAtUtc < DateTimeOffset.UtcNow ? "下次使用时需刷新" : "已加密保存";
    public string SavedText => "保存于 " + Summary.SavedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
}

public partial class MainWindow : Window
{
    private readonly bool demo;
    private readonly string codexHome;
    private VaultStore? vault;
    private AccountSwitchService? switcher;
    private bool busy;
    private bool closeRequested;
    private bool hasBackup;
    private string? currentIdentity;
    private RuntimeStatus status = new(false, "尚未检查运行状态。", Array.Empty<string>());
    private CancellationTokenSource? loginCancellation;
    private readonly ObservableCollection<AccountRow> rows = new();

    public MainWindow(bool isDemo = false)
    {
        InitializeComponent();
        demo = isDemo;
        codexHome = demo ? "演示模式" : CodexRuntime.ResolveCodexHome();
        AccountsList.ItemsSource = rows;
        Loaded += (_, _) => RefreshView();
        Closing += Window_Closing;
    }

    private void RefreshView()
    {
        var selectedId = (AccountsList.SelectedItem as AccountRow)?.Id;
        if (demo)
        {
            CurrentAccount.Text = "de***@example.com";
            CurrentDetail.Text = "只读界面演示 · 未读取任何真实账号";
            PlanBadge.Text = "PRO";
            status = new(true, "演示模式 · 所有账号操作已禁用", Array.Empty<string>());
            rows.Clear();
            rows.Add(new(new("demo-pro", "日常开发", "de***@example.com", "Pro", DateTimeOffset.Now.AddMinutes(-8), DateTimeOffset.Now.AddHours(1)), true));
            rows.Add(new(new("demo-plus", "个人订阅", "pe***@example.com", "Plus", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddHours(2)), false));
            rows.Add(new(new("demo-team", "工作账号", "wo***@example.com", "Business", DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddHours(1)), false));
            AccountsList.SelectedIndex = 1;
            UpdateControls();
            return;
        }
        try
        {
            vault ??= new VaultStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountSwitcher", "vault"));
            switcher ??= new AccountSwitchService(vault, codexHome);
            currentIdentity = null;
            CurrentAccount.Text = "尚未登录 Codex";
            CurrentDetail.Text = "登录后可以保存到本机账号库。";
            PlanBadge.Text = "未登录";
            if (File.Exists(Path.Combine(codexHome, "auth.json")))
            {
                try
                {
                    using var current = AuthSnapshot.Read(Path.Combine(codexHome, "auth.json"));
                    currentIdentity = current.Info.IdentityKey;
                    CurrentAccount.Text = current.Info.MaskedEmail;
                    CurrentDetail.Text = "显示本地登录缓存；连接状态以 Codex 启动后的验证结果为准。";
                    PlanBadge.Text = current.Info.Plan.ToUpperInvariant();
                }
                catch (UserFacingException)
                {
                    CurrentAccount.Text = "当前登录格式需要检查";
                    CurrentDetail.Text = "可以通过官方页面登录新账号；当前文件不会被覆盖。";
                }
            }
            rows.Clear();
            foreach (var account in vault.List()) rows.Add(new(account, account.Id == currentIdentity));
            hasBackup = vault.LatestBackupFileName is not null;
            status = CodexRuntime.Inspect();
            AccountsList.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedId) ?? rows.FirstOrDefault();
        }
        catch (Exception error)
        {
            SetStatus(SafeMessage(error));
            hasBackup = false;
            status = new(false, "读取状态失败，切换操作已锁定。", Array.Empty<string>());
        }
        UpdateControls();
    }

    private void UpdateControls()
    {
        CountLabel.Text = $"已保存账号 · {rows.Count}";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RuntimeLabel.Text = status.Message;
        var selected = AccountsList.SelectedItem is AccountRow;
        CaptureButton.IsEnabled = !demo && !busy && currentIdentity is not null;
        LoginButton.IsEnabled = !demo && vault is not null && (!busy || loginCancellation is not null);
        LoginButton.Content = loginCancellation is null ? "＋ 登录新账号" : "取消登录";
        RenameButton.IsEnabled = !demo && !busy && selected;
        SwitchButton.IsEnabled = !demo && !busy && selected && status.CanSwitch;
        RestoreButton.IsEnabled = !demo && !busy && status.CanSwitch && hasBackup;
        AccountsList.IsEnabled = !busy;
    }

    private void SetStatus(string text) => StatusText.Text = text;
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        e.Cancel = true;
        closeRequested = true;
        loginCancellation?.Cancel();
        SetStatus(loginCancellation is null ? "正在完成缓存写入，完成后自动退出。" : "正在取消登录并清理临时缓存，完成后自动退出。");
    }
    private static string SafeMessage(Exception error) => error is UserFacingException known ? known.Message : "操作失败。请检查文件权限或重新打开工具；不会输出登录令牌。";
    private void Refresh_Click(object sender, RoutedEventArgs e) { if (!busy) RefreshView(); }
    private void Accounts_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (demo || busy || vault is null) return;
        var label = AskLabel("保存当前账号", "给这个账号起个名字，例如：个人 Pro、工作账号。", "");
        if (label is null) return;
        try
        {
            using var snapshot = AuthSnapshot.Read(Path.Combine(codexHome, "auth.json"));
            var saved = vault.Save(snapshot, label);
            RefreshView();
            AccountsList.SelectedItem = rows.FirstOrDefault(row => row.Id == saved.Id);
            SetStatus("当前账号已加密保存，原 Codex 登录没有更改。");
        }
        catch (Exception error) { SetStatus(SafeMessage(error)); }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (loginCancellation is not null) { loginCancellation.Cancel(); return; }
        if (demo || busy || vault is null) return;
        var label = AskLabel("添加订阅账号", "登录会在独立目录中进行，不会替换当前账号。请在官方浏览器页面完成登录。", "");
        if (label is null) return;
        busy = true;
        loginCancellation = new();
        UpdateControls();
        SetStatus("正在启动官方登录页面，请在浏览器中选择要保存的账号。完成后会自动加密保存。");
        try
        {
            var saved = await new OfficialLogin(vault).AddAccountAsync(label, loginCancellation.Token);
            SetStatus($"“{saved.Label}”已添加。当前桌面登录没有更改。");
        }
        catch (Exception error)
        {
            SetStatus(SafeMessage(error));
            if (closeRequested && !SafeMessage(error).StartsWith("已取消", StringComparison.Ordinal))
                MessageBox.Show(this, SafeMessage(error), "登录清理结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            loginCancellation.Dispose();
            loginCancellation = null;
            busy = false;
            RefreshView();
            if (closeRequested) Close();
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (demo || busy || vault is null || AccountsList.SelectedItem is not AccountRow row) return;
        var label = AskLabel("重命名账号", "名称只保存在本机加密账号库。", row.Label);
        if (label is null) return;
        try { vault.Rename(row.Id, label); RefreshView(); SetStatus("账号名称已更新。"); }
        catch (Exception error) { SetStatus(SafeMessage(error)); }
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (demo || busy || switcher is null || AccountsList.SelectedItem is not AccountRow row) return;
        if (row.IsCurrent)
        {
            SetStatus("所选账号已是当前登录，未替换缓存。可点击保存当前账号以更新账号库。");
            return;
        }
        busy = true;
        UpdateControls();
        try
        {
            var hadCurrent = File.Exists(Path.Combine(codexHome, "auth.json"));
            var backup = await Task.Run(() => switcher.Switch(row.Id));
            SetStatus(hadCurrent && string.IsNullOrEmpty(backup)
                ? "账号已是当前登录，已同步最新凭据，未替换缓存。"
                : string.IsNullOrEmpty(backup)
                    ? $"已写入“{row.Label}”的登录缓存，之前没有登录文件。下次启动 Codex 时会验证账号。"
                    : $"已写入“{row.Label}”的登录缓存，并保存加密恢复备份。下次启动 Codex 时会验证账号。");
            if (OpenAfterSwitch.IsChecked == true)
            {
                try { CodexRuntime.LaunchDesktop(); }
                catch (Exception error) { SetStatus("登录缓存已切换。" + SafeMessage(error)); }
            }
        }
        catch (Exception error) { SetStatus(SafeMessage(error)); }
        finally { busy = false; RefreshView(); if (closeRequested) Close(); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (demo || busy || switcher is null) return;
        if (MessageBox.Show(this, "将恢复上一次切换前的 Codex 登录缓存。当前账号也会保存为加密备份。继续？", "恢复登录缓存", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        busy = true;
        UpdateControls();
        try { await Task.Run(() => switcher.RestoreLatest()); SetStatus("上一次登录缓存已恢复，请重新打开 Codex 完成账号验证。"); }
        catch (Exception error) { SetStatus(SafeMessage(error)); }
        finally { busy = false; RefreshView(); if (closeRequested) Close(); }
    }

    private string? AskLabel(string title, string message, string initial)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(100, 121, 128)), Margin = new Thickness(0, 0, 0, 18) });
        var input = new TextBox { Text = initial, MaxLength = 60, MinHeight = 38 };
        panel.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var dialog = new Window { Title = title, Owner = this, Width = 450, Height = 250, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, Content = panel, ShowInTaskbar = false };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var accept = new Button { Content = "确定", IsDefault = true, Style = (Style)FindResource("Primary") };
        accept.Click += (_, _) => { if (input.Text.Trim().Length > 0) dialog.DialogResult = true; else input.Focus(); };
        actions.Children.Add(cancel); actions.Children.Add(accept); panel.Children.Add(actions);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
