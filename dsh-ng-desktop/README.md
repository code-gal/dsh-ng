# dsh-ng-desktop

`dsh-ng-desktop` 是 DeepSeek Harness（DSH）的 Avalonia 跨平台桌面客户端，主要面向 Windows，同时支持 macOS。

项目已完成 M1 至 M3：共享状态机、受管路径与安装清单、结构化日志、单实例 IPC、环境诊断、AOT 静态门禁、DSH 供应与监督，以及带补偿回滚的原生安装器事务。Windows 当前用户安装器和 macOS `.pkg` 构建入口均已就绪；后续按 Spec 实现运行窗口、WebView、托盘与完整运行中卸载协作。

正式安装包仅通过 GitHub Releases 发布。正式 .NET 产物以 Native AOT、自包含构建为准入条件，同时支持非 AOT self-contained 安装和普通源码构建。

## 开发指引
对于开发者或接入的 AI Agent，请按照以下规范阅读开发文档（请注意上下文不要越界读取其它无关项目）：
1. `specs/1_REQUIREMENTS.md` - 了解需求边界
2. `specs/2_ARCHITECTURE.md` - 审查技术约束
3. `specs/3_TASKS.md` - 认领或查看进度当前挂起任务
4. `specs/4_VERIFICATION.md` - 查阅测试与交付指标

全局文档与 AI 协作规范位于仓库根目录 `specs/0_DOC_STANDARDS.md`。
