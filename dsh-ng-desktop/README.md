# dsh-ng-desktop (DeepSeek Harness NG Desktop Client)

本目录是 `dsh-ng-desktop` 项目的独立工作区。
这是一个使用 .NET AOT 构建的跨平台桌面应用壳，负责守护和调度系统底层的 `deepseek-harness` 智能体。

## 核心特性
- **Native AOT 构建**：毫秒级启动，免依赖分发。
- **环境免隔离**：直接打通系统全局底层，让 dsh 获取原生系统 CLI 与文件系统的完全访问权。
- **无痕卸载**：托盘级别的静默管理及干净到字节的“自焚式”净空能力。

## 开发指引
对于开发者或接入的 AI Agent，请按照以下规范阅读开发文档（请注意上下文不要越界读取其它无关项目）：
1. `specs/1_REQUIREMENTS.md` - 了解需求边界
2. `specs/2_ARCHITECTURE.md` - 审查技术约束
3. `specs/3_TASKS.md` - 认领或查看进度当前挂起任务
4. `specs/4_VERIFICATION.md` - 查阅测试与交付指标

*(全局的文档与 AI 开发协作规范，请回到仓库根目录参阅相应文档。)*