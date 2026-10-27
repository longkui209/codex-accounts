# Codex Accounts

面向 Windows 的本地账号管理工具与 Codex MCP 插件，由独立开发者维护。

[English](README.md) · [兼容性](docs/COMPATIBILITY.md) · [安全说明](SECURITY.md) · [功能合规审查](docs/COMPLIANCE_REVIEW.md)

**当前公开 0.15.5 完整实验源码并披露未决问题；正式产品合规确认与活动审批结果尚未确认。** 部分功能仍有服务条款、接口支持和来源核验问题。本项目未获得 OpenAI 官方认可，不能把本机运行成功解释为正式接口支持或合规许可。

## 现有功能

- 按 Windows 用户加密保存账号及恢复数据，由用户手动完成官方登录。
- 展示套餐、额度和登录时提供的可选资料，提供十种界面语言。
- 确认后移除本地账号并支持撤销；展示重置卡详情，使用前须针对具体操作确认。
- 实验性订阅路由：保留桌面登录，选择后续模型请求使用的已保存账号。
- 可选自动轮换：在明确额度拒绝且尚无输出时，以原模型、原设置接续符合条件的请求。
- 可选整机启动组件、单独确认安装的版本更新服务，以及从当前签名客户端发现结构的原生输入适配。

后三项依赖原生客户端行为或非公开服务接口，存在未决审查项。公开介绍应与实际代码一致；不能宣传为无限额度、官方支持的限额绕过或官方插件。

## 构建与隔离验证

需要 Windows x64、`global.json` 指定的 .NET 8 SDK，以及用于验证和导出的 Node.js 22+。运行框架依赖版还需 x64 的 .NET 8 Windows Desktop 与 ASP.NET Core 共享运行时。无需 WSL，项目未声明外部 NuGet 包。

```powershell
.\Build.ps1
.\Test.ps1 -SkipBuild
node scripts/public-release.cjs check
node scripts/public-release.cjs test
node scripts/public-release.cjs export --purpose review
node scripts/public-release.cjs export --purpose source-preview
```

默认验证仅使用合成数据和本地模拟服务，不安装插件、不更改整机启动、不登录真实账号、不调用模型、不使用重置卡。源码导出按明确清单执行；不会直接打包整个工作目录。

`Build-Plugin.ps1` 生成本地插件目录；`Install-Plugin.ps1` 是单独的显式安装步骤，会更新当前用户插件配置并保留加密备份。构建和测试不安装它。原生桌面与官方 CLI 需单独安装，不随本项目分发。

如果本项目以前创建的订阅快捷方式仍指向旧运行版本，安装时会在核验原生集成后，尝试迁移这个已确认归属的旧入口，并保留快捷方式备份。入口内的独立命令在每次启动时查找当前官方安装，核验签名及自有启动规则，再直接启动签名程序。此前使用 AppsFolder 的方案已观察到冷启动绕过集成，不能据此宣称覆盖所有原版入口。`Repair-LegacyRoutingShortcut.ps1` 默认只预览，添加 `-Apply` 才修改。它不恢复、重建或改写账号库；旧解析器可能将新版认证字段误报为账号库损坏。

普通启动组件须单独接受 Windows 管理员确认，保存本机原生应用副本并登记精确路径的整机 IFEO 规则，影响通过该路径启动应用的所有 Windows 用户；各用户账号库仍然隔离。只安装插件不会静默完成此项系统变更。官方更新安装路径后需重新核验，独立 ChatGPT 客户端与其他 Windows 用户尚未逐一实测。

可卸载的更新服务以 LocalSystem 运行，只处理签名安装元数据和自有启动规则，不读取账号库或聊天。新版登记前核验文件，未知资源结构保留原生行为。近期修复包括输入模块拆分、官方 OAuth 元数据兼容及常驻沙箱服务误阻塞启动，详见[版本更新](docs/VERSION_UPDATES.md)。具体安装仍须正常重开后以真实请求核验，不能仅凭服务运行宣称切换成功。

## 发布和申请

完整实验源码按维护者选择以 `source-preview` 公开，未决项不因公开而解决。已通过合规审查的正式产品发布仍受 `release/review-status.json` 约束，`--purpose publication` 会拒绝未通过条件；这只是项目自身的检查，不代表法律意见或官方许可。

详见[发布流程](docs/RELEASING.md)、[合规审查](docs/COMPLIANCE_REVIEW.md)、[活动申请准备](docs/OPEN_SOURCE_PROGRAM.md)、[隐私说明](PRIVACY.md)及[来源与第三方说明](THIRD_PARTY_NOTICES.md)。自有贡献采用 [MIT](LICENSE)，第三方材料应保留其适用权利声明。不能以新建仓库代替真实用户、维护记录或生态价值证明。
