# dsh-ng-desktop

`dsh-ng-desktop` 是 DeepSeek Harness（DSH）的 Avalonia 桌面客户端。Windows `win-x64` 是当前唯一的发布和验收目标；macOS 保留兼容性代码，供外部开发者自行构建验证。

项目已完成 M1 至 M4 以及 M5 的安装事务、卸载协调和 Windows SetupHost 本地实现：共享状态机、受管路径与安装清单、结构化日志、单实例 IPC、环境诊断、AOT 静态门禁、DSH 供应与监督、Avalonia 原生安装引导、WebView、托盘和带确认握手的运行中卸载。Windows 安装器由无界面 Native AOT SetupHost 提供唯一单文件入口，并内嵌 AOT 或 .NET 依赖客户端负载。发行规则见 `specs/4_VERIFICATION.md` 和 `Installer/Packaging/RELEASE.md`，实际开发、签名、打包和发布步骤见 [开发与发布指南](docs/DEVELOPMENT_AND_RELEASE.md)。

正式安装包仅通过 GitHub Releases 发布。Windows `win-x64` 同时提供 Native AOT 自包含安装包和需要预装匹配 .NET Runtime 的 `.NET 依赖`安装包；Native AOT 仍是正式准入条件。macOS 不提供项目发行安装包。

## 开发指引
对于开发者或接入的 AI Agent，请按照以下规范阅读开发文档（请注意上下文不要越界读取其它无关项目）：
1. `specs/1_REQUIREMENTS.md` - 了解需求边界
2. `specs/2_ARCHITECTURE.md` - 审查技术约束
3. `specs/3_TASKS.md` - 认领或查看进度当前挂起任务
4. `specs/4_VERIFICATION.md` - 查阅测试与交付指标

全局文档与 AI 协作规范位于仓库根目录 `specs/0_DOC_STANDARDS.md`。

源码运行使用 `dotnet run -- --development`；不带参数的 `DshNgDesktop.exe` 只用于已安装客户端，不能隐式启动安装事务。
