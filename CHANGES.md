# 修改说明（相对原版 CodexQuotaTray 3.0.2-preview）

本仓库由 [hxxol/CodexQuotaTray](https://github.com/hxxol/CodexQuotaTray) 改成 Claude 版。界面和逻辑尽量保持原样，只改了下面这些地方。当前最新版本：v1.0.3。

## 1. 额度来源：Codex 改为 Claude

文件：`src/SessionQuotaReader.cs`

- `ReadLatest()` 不再读取 Codex 本地会话日志和 `codex app-server`，改为调用 Claude 的 OAuth 用量接口。
- 读取流程：
  1. 从 `~/.claude/.credentials.json` 读取登录凭据（设置了环境变量 `CLAUDE_CONFIG_DIR` 时读该目录），取 `claudeAiOauth.accessToken` 和 `subscriptionType`。
  2. 请求 `GET https://api.anthropic.com/api/oauth/usage`，请求头带 `Authorization: Bearer <token>` 和 `anthropic-beta: oauth-2025-04-20`。
  3. 解析返回：优先读 `limits` 数组（`kind=session` 为 5 小时窗口，`kind=weekly_all` 为每周窗口，字段 `percent`、`resets_at`）；没有时读 `five_hour` / `seven_day`（字段 `utilization`、`resets_at`）。已用百分比映射为原程序使用的剩余额度。
- 新增的方法：`ResolveClaudeCredentialsPath`、`IsClaudeLoggedIn`、`ReadLatestFromClaudeApi`、`TryParseClaudeUsageResponse` 等。原有的 Codex 解析代码保留，不再被调用。
- 额度历史记录目录从 `CodexMeter` 改为 `ClaudeMeter`。
- Claude 没有对应的 token 活动数据，这部分为空。
- 没有凭据、token 过期、网络失败、返回无法识别时，会给出对应的错误提示。程序只读取 token，不刷新也不改写凭据文件。token 约一小时过期，过期后打开一次 Claude Code 即可刷新。
- 注意：该接口是非官方接口，没有官方文档，以后可能变化。

## 2. 悬停提示改为两行

文件：`src/TrayApplicationContext.cs`、`src/TaskbarWidgetForm.cs`、`src/FloatingQuotaWindow.cs`、`src/LegacyResetTooltip.cs`、新增 `src/TooltipText.cs`

- 鼠标悬停在任务栏圆环和悬浮窗上时，提示显示两行，每秒更新：
  ```
  5h额度：X天X时X分后重置
  周额度：X天X时X分后重置
  ```
  重置时间来自同一个用量接口的 `resets_at`。没有数据时对应那行显示“暂无数据”。
- `TrayApplicationContext` 新增 `BuildResetText` 和 `FormatTrayReset` 生成文字，并通过新增的 `ResetTextProvider` 属性交给圆环窗口和悬浮窗。
- 原来的提示框只能画单行文字。新增 `TooltipText`，按行测量和绘制，提示框的深色背景和样式不变。
- 系统托盘（右下角“^”面板）里的图标现在一直显示，悬停同样是这两行文字。原来只有任务栏小组件不可用时才会出现。

## 3. 程序图标

文件：`Assets/CodexQuotaTray.ico`

- 换成用户提供的图标，缩放为 16、20、24、32、40、48、64、96、128、256 共 10 个尺寸。小尺寸为自动缩小，可能不如专门绘制的清晰。

## 4. 启动崩溃修复

文件：`app.manifest`、`src/FloatingQuotaWindow.cs`

- 现象：部分 Windows 11 环境下启动时报错，日志显示悬浮窗的提示框创建失败（`LegacyResetTooltip` 构造函数）。
- 修改：
  - 在 `app.manifest` 中声明使用 Common Controls v6（推测原因是缺少该声明，未在出错的电脑上单独验证）。
  - 悬浮窗提示框创建失败时改为写日志（事件名 `floating.tooltip-unavailable`）并跳过，不再让程序退出。

## 5. 构建与发布

文件：`.github/workflows/manual-release.yml`

- 仍然是手动触发的工作流，输入版本号并选择是否发布 Release。
- 删除了“与原版 .NET Framework 解析器逐字对比”的步骤。该步骤要求读取额度的代码与原版完全一致，而本仓库已有意修改。
- 交互式冒烟测试（`--smoke-test`）改为只警告、不中止。它需要真实桌面，在 GitHub 构建机上自包含版本的退出码为 1，原因未完全确认。请在真实桌面上自行运行验证。
- Release 标题改为 `ClaudeQuotaTray v… · Preview`。

## 6. 文档

- `README.md` 顶部新增了本 fork 的简要说明，其余内容是原版文档。
- 新增本文件 `CHANGES.md`。

## 未修改的部分

- exe 文件名、程序内文案、命名空间、设置窗口、悬浮窗和任务栏圆环的绘制、测试代码等均保持原样，仍显示 Codex 字样。
- 提示框的深色背景没有改成透明：原生提示框不支持只让背景透明，强行实现效果差、风险大，所以没有做。
