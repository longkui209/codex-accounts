using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

/// <summary>Synthetic metadata and deterministic times only; never starts or inspects a desktop.</summary>
public static class NativeDesktopLifetimeSelfTests
{
    private const string Image = @"C:\synthetic-installed-desktop\app\ChatGPT.exe";
    private const string Sid = "S-1-5-21-100-200-300-400";
    private const string Cli = @"C:\synthetic-verified-core\codex.exe";
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<string> Run()
    {
        var reports = new List<string>();
        var tracker = Create();
        var initial = Candidate(10);
        Require(tracker.Observe(Snapshot(initial), Start, false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "initial native desktop is followed");
        Require(tracker.Observe(Snapshot(), Start.AddSeconds(1), false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "initial PID exit is not desktop closure");
        Require(tracker.Observe(Snapshot(Candidate(11, created: Start.AddSeconds(2))), Start.AddSeconds(2), false)
            == NativeDesktopLifetime.Outcome.KeepAlive, "native replacement PID keeps relay alive");
        Require(tracker.Observe(Snapshot(Candidate(11), Candidate(12)), Start.AddSeconds(100), false)
            == NativeDesktopLifetime.Outcome.KeepAlive, "multiple native process roles are followed");
        reports.Add("PASS: 合成初始桌面退出与原生 PID 接管保持订阅服务，多个同次启动进程不会提前停服。");

        Require(tracker.Observe(Snapshot(), Start.AddSeconds(101), false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "empty snapshot starts stable close window");
        Require(tracker.Observe(Snapshot(), Start.AddSeconds(103), false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "three-second shutdown grace is enforced");
        Require(tracker.Observe(Snapshot(), Start.AddSeconds(104), false) == NativeDesktopLifetime.Outcome.Closed,
            "confirmed native exit finishes only after stable grace");
        reports.Add("PASS: 仅在所有匹配桌面退出并持续稳定三秒后结束，枚举空窗不会直接报告已关闭。");

        tracker = Create();
        var rejected = Snapshot(Candidate(20, image: @"C:\unrelated\ChatGPT.exe"),
            Candidate(21, sid: "S-1-5-21-900-900-900-900"), Candidate(22, created: Start.AddSeconds(-2)),
            Candidate(23, created: Start.AddSeconds(30)), Candidate(0));
        Require(tracker.Observe(rejected, Start, false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "startup waits without binding mismatched processes");
        Require(tracker.Observe(Snapshot(), Start.AddSeconds(20), false) == NativeDesktopLifetime.Outcome.StartupNotConfirmed,
            "path SID old birth future birth and invalid PID cannot establish binding");
        tracker = Create();
        Require(tracker.Observe(Snapshot(Candidate(24, image: Image.ToUpperInvariant(), created: Start.AddSeconds(-1))),
            Start, false) == NativeDesktopLifetime.Outcome.KeepAlive, "canonical Windows casing and birth tolerance");
        Require(tracker.Observe(Snapshot(), Start.AddSeconds(21), false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "matching process was actually bound");
        reports.Add("PASS: 不绑定其他路径、其他用户、启动前旧进程或未来时间；仅接受精确安装路径、同 SID 和新启动时间。");

        tracker = Create();
        var unknown = new NativeDesktopLifetime.Snapshot([], true);
        Require(tracker.Observe(unknown, Start, false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "unknown metadata is not closure");
        Require(tracker.Observe(unknown, Start.AddSeconds(59), false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "unknown state receives bounded grace");
        Require(tracker.Observe(unknown, Start.AddSeconds(60), false) == NativeDesktopLifetime.Outcome.Unverified,
            "unknown state never becomes a successful closure");
        tracker = Create();
        Require(tracker.Observe(Snapshot(initial), Start, false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "known initial binding");
        Require(tracker.Observe(unknown, Start.AddSeconds(1), false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "unreadable metadata resets close observation");
        Require(tracker.Observe(Snapshot(), Start.AddSeconds(100), false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "recovered empty state begins a new close grace");
        reports.Add("PASS: 无法读取的进程状态保持未知，超时只报告未核验；恢复读取后重新等待正常退出窗口。");

        tracker = Create();
        Require(tracker.Observe(Snapshot(initial), Start, false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "active-request fixture desktop bound");
        Require(tracker.Observe(Snapshot(), Start.AddSeconds(1), true) == NativeDesktopLifetime.Outcome.KeepAlive,
            "in-flight request retains relay after PID exit");
        Require(tracker.Observe(unknown, Start.AddSeconds(120), true) == NativeDesktopLifetime.Outcome.KeepAlive,
            "unknown timeout cannot interrupt authenticated active request");
        Require(tracker.Observe(Snapshot(), Start.AddSeconds(121), false) == NativeDesktopLifetime.Outcome.KeepAlive,
            "request completion restarts closure grace");
        Require(tracker.Observe(Snapshot(), Start.AddSeconds(124), false) == NativeDesktopLifetime.Outcome.Closed,
            "relay may stop after request completion and stable native exit");
        reports.Add("PASS: 已鉴权的执行中请求保持订阅服务，正常结束后才重新等待桌面退出。");

        var parent = Candidate(30);
        var caller = Candidate(31, image: Cli, created: Start.AddSeconds(2));
        var parents = Snapshot(parent);
        bool Match(NativeDesktopLifetime.Candidate value, int parentPid, NativeDesktopLifetime.Snapshot candidates)
            => NativeDesktopLifetime.CallerMatches(value, parentPid, candidates, Image, Cli, Sid,
                Start, Start.AddSeconds(3));
        Require(Match(caller, 30, parents), "verified native CLI direct child can bind");
        Require(!Match(caller, 99, parents), "unrelated same-user CLI parent is refused");
        Require(!Match(caller, 30, Snapshot()), "exited native parent cannot authorize a leftover CLI");
        Require(!Match(caller with { ImagePath = @"C:\other\codex.exe" }, 30, parents), "CLI image path must be exact");
        Require(!Match(caller with { UserSid = "S-1-5-21-900-900-900-900" }, 30, parents), "caller SID must match");
        Require(!Match(caller with { CreatedUtc = Start.AddSeconds(-2) }, 30, parents), "old CLI cannot bind");
        Require(!Match(caller with { CreatedUtc = Start.AddSeconds(10) }, 30, parents), "future CLI cannot bind");
        Require(!Match(caller, 30, Snapshot(parent with { UserSid = "S-1-5-21-900-900-900-900" })),
            "native parent SID must match");
        Require(!Match(caller, 30, Snapshot(parent with { ImagePath = @"C:\other\ChatGPT.exe" })),
            "native parent installation must match");
        Require(!Match(caller, 30, Snapshot(parent with { CreatedUtc = Start.AddSeconds(-2) })),
            "old parent cannot bind");
        Require(!Match(caller, 30, Snapshot(parent with { CreatedUtc = Start.AddSeconds(3) })),
            "reused parent PID newer than the caller cannot bind");
        Require(!Match(caller, 31, Snapshot(caller)), "self-parent cannot bind");
        reports.Add("PASS: 仅本次原生桌面的精确已验证 CLI 子进程可建立绑定；无关同用户 CLI、旧进程和父 PID 复用拒绝。");
        return reports;
    }

    private static NativeDesktopLifetime.Tracker Create() => new(Image, Sid, Start, TimeSpan.FromSeconds(3));
    private static NativeDesktopLifetime.Candidate Candidate(int pid, string image = Image, string sid = Sid,
        DateTimeOffset? created = null) => new(pid, image, sid, created ?? Start);
    private static NativeDesktopLifetime.Snapshot Snapshot(params NativeDesktopLifetime.Candidate[] candidates)
        => new(candidates, false);
    private static void Require(bool value, string name)
    {
        if (!value) throw new UserFacingException("隔离桌面生命周期验证失败：" + name + "。");
    }
}
