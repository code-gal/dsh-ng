# Changelog

本文件记录 DSH Desktop 每个已发布版本中面向用户的变更，不记录逐次提交明细。版本号由维护者按 SemVer 自主决定；发行标签 `desktop-v<SemVer>` 必须与对应条目一致。

## [Unreleased]

## [0.9.14] - 2026-08-17

### Fixed

- 修复 macOS Release 门禁中测试宿主进程崩溃（Test host process crashed）无法定位的问题：为 `dotnet test` 启用 blame 崩溃转储与执行序列采集，并将诊断产物作为 CI 工件上传，用于精确定位崩溃时正在执行的测试与堆栈。

## [0.9.13] - 2026-08-17

### Fixed

- 修复 macOS ARM64 Native AOT 安装包 CI 构建失败：`dotnet publish` 显式传递 `AppleMinOSVersion=14.0`，主程序按 `minos 14.0` 链接，与 WebKit 辅助程序和 Mach-O 最低版本门禁一致。

## [0.9.12] - 2026-08-16

### Fixed

- 修复 macOS Apple Silicon 上的跨平台兼容性问题：
  - 维护租约（单实例与更新互斥锁）改为跨平台实现，修复 macOS 上互斥与等待失效的问题。
  - 安装部署按 Unix 语义复制文件权限，避免 macOS 上可执行权限缺失导致启动失败。
  - Node/npx 解析改为受控查找并限制执行 PATH，避免外部 PATH 污染解析到错误工具。
  - DSH 进程以独立进程组和私有工作目录启动，避免信号传播与工作目录干扰。
  - 安装后脚本（postinstall）运行在真实用户环境并以同步方式等待退出，保证安装闭环。
- 修复卸载与关闭窗口时的残留清理：macOS 上彻底清理 WebKit 残留数据，避免重装后状态污染。

### Changed

- macOS 安装包目标提升至 macOS 14+，并附带 dSYM 内部验证工件。
- 新增 ReleaseTests 与真实产物门禁，使用实际安装产物验证发行边界，强化跨平台发行验收。

## [0.9.11] - 2026-08-16

### Added

- 首个 DSH Desktop 社区预览版，支持 Windows `win-x64` 和 macOS Apple Silicon (`osx-arm64`)。
- 原生安装引导、DSH 环境检测、私有运行目录、健康检查、托盘驻留、开机自启动和干净卸载。
- DSH Web UI 内嵌桌面窗口；关闭主窗口后服务继续在托盘运行，可随时重新打开。
- Windows 桌面和开始菜单快捷方式，以及安装、运行和诊断日志入口。

### Changed

- Windows 同时提供 Native AOT 自包含安装器和需要 .NET 10 Desktop Runtime 的 .NET 依赖安装器；macOS 提供 Native AOT 自包含安装包。
- 客户端通过 `npx @deepseek-ai/dsh web` 管理 DSH，使用专属缓存和数据目录，不修改用户的全局 npm 缓存或系统 Node.js 安装。
- GitHub Release 使用本文件的精炼更新说明、提交数量和比较链接，不再生成详细提交日志。

发布版本时，将与标签一致的版本小节置于此处，并保留新的 `Unreleased` 小节。每个版本至少包含一项用户可见的 Added、Changed、Fixed 或 Removed 更新。
