# ClaudeQuotaTray

Fork of [CodexQuotaTray](https://github.com/hxxol/CodexQuotaTray). UI and logic are unchanged; only the quota source differs: remaining Claude 5-hour / weekly quota is read from `https://api.anthropic.com/api/oauth/usage` using the Claude Code login in `~/.claude/.credentials.json` (or `$CLAUDE_CONFIG_DIR`). Log in with Claude Code at least once; if the token expires, open Claude Code to refresh it. The unofficial endpoint may change.

---

# CodexQuotaTray 3.0.2-preview

本版仅将用户提供的 PNG 转为多尺寸 ICO，并嵌入 EXE 文件图标。任务栏动态额度圆环、悬浮窗、交互与解析逻辑保持 3.0.1-preview 不变。用户已确认 3.0.1-preview 在其 Windows 电脑上运行正常；3.0.2-preview 在 Linux 环境交叉构建并检查资源，未新增 Windows 实机运行验证。交互与恢复修订见 [FIX_3.0.1.md](FIX_3.0.1.md)。原有首次启动设置逻辑保持不变。

面向 Windows x64 的 Codex 额度显示工具：保留任务栏大圆环，并新增独立的 WPF 悬浮窗与设置页。

## 使用

点击任务栏圆环打开菜单，顺序固定为：

1. 悬浮窗（勾选开关）
2. 设置
3. 退出

左右键都可打开这个菜单。任务栏圆环不能拖动。悬浮窗可按住左键拖动，不提供右键菜单、边框或缩放手柄。

设置分为任务栏与悬浮窗两部分：

- 两者的圆环颜色、数字颜色互不影响，仅提供自定义颜色。
- 任务栏保留原有大小公式、字体与字号，只开放线宽和两种颜色。
- 悬浮窗口只可选择 128、256、512 **实际像素**的正方形画布；不同 DPI 下仍保持指定窗口像素大小。
- 悬浮窗可调整数字大小、字重、圆环线宽和圆环缩放。线宽与字号以 256 像素画布为基准。
- 圆环没有背景底圈；缺口居中朝下；100 显示为 `100`，不附加百分号。
- 悬停继续使用原倒计时文本：中文为“6天23小时11分钟后重置”，英文为“Resets in 6d 23h 11m”。离线、无重置时间或已过期时不显示旧倒计时。

显示设置、悬浮开关和最后拖动位置自动保存。保存路径继续使用 `%LOCALAPPDATA%\CodexMeter\settings.ini`，旧配置会自动读取，新字段使用默认值。新安装默认关闭悬浮窗。

如果任务栏太拥挤、Explorer 暂不可用或未找到可信位置，程序会暂时隐藏任务栏圆环，避免盖住真正的系统按钮。此时标准系统托盘会显示恢复图标，正常大圆环可用后自动隐藏它。再次运行同版 EXE 会打开现有进程的设置窗口，也可使用 `--settings`。`--recover` 会打开设置，并开启悬浮窗、移到主屏中央；不会重置其他设置。程序不会移动、注入或修改 Explorer。

## 额度来源与兼容性

`SessionQuotaReader.cs` 相比输入版本只替换了序列化器的命名空间；其他代码保持不变。仍然使用原来的 Codex 目录解析、进程判断、本地会话、app-server 调用、额度池优先顺序、时间窗口、历史与刷新逻辑。

- 额度每 20 秒后台刷新，避免阻塞界面。
- 存在备用额度时，沿用原先的备用额度选择；否则显示原先最紧的窗口，平局顺序保持不变。
- 已保存的最后余额不会被更旧的离线快照覆盖。没有已保存余额时，仍可从本地快照初始化。
- 第一次读取完成前即可显示已保存余额。
- .NET Framework 的 `System.Web` 在现代 .NET 中不可用，因此增加独立兼容适配器：保留字典、数组、整数/长整数/decimal/double 类型和数字格式；保留旧解析器接受的单引号、未加引号的字段及 ASP.NET 日期形式。
- `tests/compare-legacy.ps1` 在 Windows 上把原始 .NET Framework 解析器与现代版本的合成输入结果逐行比较，并检查读取器源码未发生其他变化。测试不会访问用户真实会话、认证或额度。

输入版本的旧详情页、旧设置窗口和自动更新入口未接入新 UI。旧更新解析器仅保留用于原始回归测试，不会编译进发布程序，也不会从旧仓库下载更新。

## 任务栏定位

任务栏仍是大尺寸透明覆盖窗口，不是系统固定大小的 NotifyIcon。它保留原尺寸公式：`max(16, taskbar thickness − round(12 × DPI scale))`。

新的独立布局监测器观察 Explorer 事件，并以 200ms 轻量检查发现窗口、几何与 DPI 变化；UI Automation 在单独 MTA 后台线程中运行，正常情况下最多约 800ms 重新核验一次，和额度的 20 秒刷新完全分离。每次探测原子替换完整结果；失败、Explorer 重启、显示器和 DPI 变化都使缓存失效。

定位只改变本程序窗口，并避开已识别的任务栏控件。按住任意鼠标键时暂停任务栏窗体移动；本程序的按下、捕获、移动、抬起形成完整手势，拖动不会误触菜单。真正点击在其他系统图标上的操作仍由 Windows 处理。

Windows 各版本的任务栏实现、第三方任务栏以及 UIA 卡顿可能影响定位。不能把覆盖窗口等同于 Explorer 原生管理的图标槽位。交互验收清单见 [TESTING.md](TESTING.md)。

## 两种单文件发布

目标框架为 `net10.0-windows`，Windows x64。

- `self-contained`：包括 .NET 10 Windows Desktop Runtime，文件较大；无需另外安装运行时。
- `framework-dependent`：较小的单 EXE，电脑需已安装 **.NET 10 Windows Desktop Runtime x64**。只有普通 .NET Runtime 不够。

两个版本都只分发一个 EXE。自包含单文件可能首次运行时把内置原生库解包到用户临时缓存，这是 .NET 单文件的正常实现；无需安装到系统目录。不使用裁剪，也不自动修改注册表开机启动项。

.NET 10 是当前 LTS，官方支持至 2028-11-14：
https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core

官方下载运行时：
https://dotnet.microsoft.com/en-us/download/dotnet/10.0

## 本地构建

Windows 上安装 .NET 10 SDK，然后在项目根目录运行 PowerShell：

```powershell
./build.ps1
```

脚本依次运行 Windows 夹具/绘制测试、与 .NET Framework 的兼容比较、两种单 EXE 发布、以及两种发布程序的有界启动冒烟检查。

Linux/macOS 可以运行纯逻辑回归测试（不包含 Windows 绘制与交互）：

```sh
dotnet run --project tests/CodexQuotaTray.CoreTests.csproj -c Release
```

Windows 完整夹具测试：

```powershell
dotnet run --project tests/CodexQuotaTray.Tests.csproj -c Release -- --fixtures-only
./tests/compare-legacy.ps1
```

默认测试只使用合成数据。原测试保留的 `--live-read` 仅供用户自行明确选择的本机诊断，不用于 CI。

## 手动 GitHub 发布

工作流只支持 `workflow_dispatch`，提交和推送不会自动触发。具体步骤见 [BUILD_RELEASE.md](BUILD_RELEASE.md)。所有自动测试、原解析器比较和两种 EXE 冒烟检查成功后，才允许创建 Release。工作流不会覆盖已存在的版本。

## 来源

基于用户提供的 CodexQuotaTray 2.6.0 源码包重写；原作者信息保留为 SYD-Official。原始输入压缩包保持未改动。`tests/legacy/` 仅为兼容验证保留原始读取器与模型，不参与应用编译。
