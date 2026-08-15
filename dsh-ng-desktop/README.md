# dsh-ng-desktop

`dsh-ng-desktop` 是 DeepSeek Harness（DSH）的 Avalonia 跨平台桌面客户端，主要面向 Windows，同时支持 macOS。

项目已完成共享基础与平台边界：显式应用状态机、受管路径与安装清单、结构化日志、单实例 IPC、平台服务接口、环境诊断和 AOT 静态门禁均已就绪。后续将按 Spec 实现 DSH 供应与监督、平台安装器、Avalonia 托盘、内嵌 DSH Web UI 和干净卸载。

正式安装包仅通过 GitHub Releases 发布。正式 .NET 产物以 Native AOT、自包含构建为准入条件，同时支持非 AOT self-contained 安装和普通源码构建。

## 开发指引
对于开发者或接入的 AI Agent，请按照以下规范阅读开发文档（请注意上下文不要越界读取其它无关项目）：
1. `specs/1_REQUIREMENTS.md` - 了解需求边界
2. `specs/2_ARCHITECTURE.md` - 审查技术约束
3. `specs/3_TASKS.md` - 认领或查看进度当前挂起任务
4. `specs/4_VERIFICATION.md` - 查阅测试与交付指标

全局文档与 AI 协作规范位于仓库根目录 `specs/0_DOC_STANDARDS.md`。
