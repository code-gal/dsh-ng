# Changelog

本文件记录 DSH Desktop 每个已发布版本中面向用户的变更，不记录逐次提交明细。版本号由维护者按 SemVer 自主决定；发行标签 `desktop-v<SemVer>` 必须与对应条目一致。

## [Unreleased]

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
