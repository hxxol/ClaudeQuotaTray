# 手动构建与 Release

1. 将项目根目录内容上传到自己的仓库，包含 `.github/workflows/manual-release.yml`。
2. 打开仓库的 Actions → Manual Windows build and release → Run workflow。
3. 输入三段版本号，例如 `3.0.0`。
4. 勾选“Publish a GitHub Release…”会在全部验证成功后发布；取消勾选则只生成 Actions 附件。

工作流仅有 `workflow_dispatch`，没有 push、pull_request、定时或自动升级触发。

执行顺序：

- 使用 Windows runner 和 .NET 10 SDK
- 编译并运行 Windows 回归、绘制测试
- 检查 SessionQuotaReader 只改了序列化器命名空间
- 用系统 .NET Framework 编译原始解析器，比较原始/现代版本的合成 JSON 与额度结果
- 分别发布自包含和依赖框架的 Windows x64 单 EXE
- 检查每个发布目录恰好一个 EXE
- 每个 EXE 运行 `--smoke-test`，最多 30 秒，实际创建 WPF 窗口和原生 tooltip，检查 128/256/512 像素尺寸，再关闭
- 生成 SHA256SUMS.txt，上传 Actions 附件
- 若选择发布，创建对应版本的 Release，并将标签指向本次验证过的 commit

发布文件：

- `CodexQuotaTray-vX.Y.Z-win-x64-self-contained.exe`
- `CodexQuotaTray-vX.Y.Z-win-x64-framework-dependent.exe`
- `SHA256SUMS.txt`

已有同名 Release 会停止创建，避免覆盖用户已下载的文件。需要重发时应使用新的版本号。Release 使用内置 GitHub token，不需要个人访问令牌；仓库策略仍需允许工作流写入 Releases。

自动启动测试不能代替真实 Windows 桌面的任务栏、混合 DPI、鼠标拖动及 tooltip 对照验收。见 TESTING.md。

当前工作流固定标记为预发布（Pre-release），因为真实桌面交互仍待验收。自动测试成功也不会将它标记为已验证的稳定版。
