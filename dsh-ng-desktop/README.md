# dsh-ng-desktop

`dsh-ng-desktop` 是 DeepSeek Harness（DSH）的 Avalonia 跨平台桌面客户端，主要面向 Windows，同时支持 macOS。

项目已完成 M1 至 M4 以及 M5 的安装事务、卸载协调等本地实现：共享状态机、受管路径与安装清单、结构化日志、单实例 IPC、环境诊断、AOT 静态门禁、DSH 供应与监督、Avalonia 原生安装引导、WebView、托盘和带确认握手的运行中卸载。Windows 旧 IExpress 包装链已移除，新的无界面 Native AOT SetupHost 单文件入口尚待实现；完成真实安装验收前不存在可正式发布的 Windows 安装包。发行规则见 `specs/4_VERIFICATION.md` 和 `Installer/Packaging/RELEASE.md`。

正式安装包仅通过 GitHub Releases 发布。每个目标平台同时提供 Native AOT 自包含安装包和需要预装匹配 .NET Runtime 的 `.NET 依赖`安装包；Native AOT 仍是正式准入条件。

## 开发指引
对于开发者或接入的 AI Agent，请按照以下规范阅读开发文档（请注意上下文不要越界读取其它无关项目）：
1. `specs/1_REQUIREMENTS.md` - 了解需求边界
2. `specs/2_ARCHITECTURE.md` - 审查技术约束
3. `specs/3_TASKS.md` - 认领或查看进度当前挂起任务
4. `specs/4_VERIFICATION.md` - 查阅测试与交付指标

全局文档与 AI 协作规范位于仓库根目录 `specs/0_DOC_STANDARDS.md`。
