# 系统兼容与运行条件

| 包类型 | 目标系统（均为 x64） | 运行环境 |
| --- | --- | --- |
| 现代完整版 ZIP | Windows 10、Windows 11 | 自包含 .NET 10，无需安装 .NET；仅上传 GitHub |
| 现代轻量 ZIP | Windows 10、Windows 11 | 需预装 x64 .NET 10 Desktop Runtime，最低 10.0.13；普通 Runtime 或 .NET Framework 不可代替 |
| 在线安装 EXE | Windows 10 1809 及以后、Windows 11 | 原生安装器检测 x64 桌面运行环境；缺少时征求同意，从微软下载并校验后安装应用 |
| 兼容预览版（文件名含 `net48`） | Windows 7 SP1、Windows 10、Windows 11 | 需要系统已安装 .NET Framework 4.8 或兼容的后续 4.x 运行环境 |

本轮不针对 Windows 8 或 32 位系统。Windows 7 必须为 SP1；系统安装及更新运行环境可能需要管理员权限，应用自身仍按普通用户运行。

## 如何选择

- Win10／Win11 优先使用在线安装包；不便安装组件或需离线运行时选择 GitHub 的现代完整版 ZIP。Win7 使用兼容 ZIP，并先安装 [.NET Framework 4.8 官方离线安装程序](https://support.microsoft.com/en-us/servicing/dotnetframework/2019/10/microsoft-net-framework-4-8-offline-installer-for-windows)。现代在线安装器不在 Win7 上运行。
- Win7 的系统更新、根证书及 TLS 1.2 能力会影响 HTTPS 取数。兼容版使用 TLS 1.2，证书验证保持开启；网络不可用时保留最近数据并提示异常。
- 解压到新的目录，退出旧版后再启动；现代版和兼容版使用同一份 `%LocalAppData%\NbaLive` 配置、收藏及单实例通信，不应同时运行。
- 兼容版依赖的 DLL 与 `NbaLive.exe.config` 必须保留，不要只复制 EXE。

## 实现与验收范围

同一套 WPF 窗口与核心逻辑分别编译为 `net10.0-windows` 和 `net48`。日期、集合、计时、异步等待及配置原子替换保持兼容；比赛收藏的日期 JSON 继续使用 `yyyy-MM-dd`。旧系统没有按窗口 DPI／按 DPI 获取托盘尺寸的接口时，回退到 WPF DPI 和系统托盘尺寸。旧系统不支持的标题栏配色忽略并保留系统标题栏，应用内部双主题继续可用。

本机为 Windows 11：两套运行时均已执行核心测试、真实 WPF 阅读／布局回归；兼容版实际 App 流程的托盘与辅助窗口启动也通过。以上是代码、运行时及当前主机验证，**尚无 Win7／Win10 实机或虚拟机结果**，因此兼容包暂按预览交付，不宣称已完成跨系统实机验收。

仍需在 Win7 SP1＋.NET 4.8、Win10、Win11 分别核对启动、联网、快捷键、托盘、穿透、缩放与多显示器操作。旧系统的标题栏样式和字体可能与 Win11 截图不同。

## 构建与测试

开发环境继续使用 Windows 与 .NET 10 SDK，不需要在 Win7 上安装开发工具。

```powershell
dotnet run --project tests -c Release
dotnet build tests-compat -c Release
.\tests-compat\bin\Release\net48\NbaLive.CompatTests.exe
dotnet run --project tests-ui -f net10.0-windows -c Release
dotnet build tests-ui -f net48 -c Release
.\tests-ui\bin\Release\net48\NbaLive.UiTests.exe
pwsh -File scripts/build.ps1
pwsh -File scripts/build.ps1 -Compatible -ArtifactSuffix compatibility-preview
```
