# dsh-ng-desktop

`dsh-ng-desktop` 是 DeepSeek Harness（DSH）的 Avalonia 跨平台桌面客户端，主要面向 Windows，同时支持 macOS。

项目当前处于重构基线：旧的 Win32 托盘、npm 安装调度和 WebView 实验实现已经移除。后续将按照 Spec 实现平台安装器、原生安装引导、npx 私有缓存、DSH 进程监督、Avalonia 托盘、内嵌 DSH Web UI 和干净卸载。

## 开发指引
对于开发者或接入的 AI Agent，请按照以下规范阅读开发文档（请注意上下文不要越界读取其它无关项目）：
1. `specs/1_REQUIREMENTS.md` - 了解需求边界
2. `specs/2_ARCHITECTURE.md` - 审查技术约束
3. `specs/3_TASKS.md` - 认领或查看进度当前挂起任务
4. `specs/4_VERIFICATION.md` - 查阅测试与交付指标

全局文档与 AI 协作规范位于仓库根目录 `specs/0_DOC_STANDARDS.md`。
