# 技术架构与工程规范

## 1. 工程选型
*   **平台**：.NET 10/11。
*   **发布模式**：`<PublishAot>true</PublishAot>` 原生编译。产出极小、无运行时依赖、毫秒级启动的独立应用。
*   **架构形态**：极简扁平的 File-based 面向对象。禁绝重度切层（No MVC/MVVM），全部逻辑封闭在几个特定的单一职责类文件中。

## 2. 核心模块与文件映射 (最多 4 个 .cs 文件)
为了防止代码腐化或由于单文件过长(超过2000行)引发代码污染，我们将逻辑划分至以下实体：

| 文件名 | 职责划分 | 备注约束 |
|---|---|---|
| `Program.cs` / `TrayApp^` | 顶层启动流水线、托盘原生 UI 建立、系统退出钩子、自启挂载逻辑。 | 拒绝拖拽式 XAML，用纯 C# 直写。 |
| `DshOrchestrator.cs` | Node 环境断言、跨进程 (ProcessStartInfo) `npm install` 包体下载、dsh 的后台创建与进程重定向。 | 必须是异步 (`async/await`) 且不可致 UI 卡顿。 |
| `Configuration.cs` | 强类型映射模型、端口的防冲突探测、向后的 `appsettings.json` IO 固化落地。 | 涉及 JSON 务必采用 Source Generator (AOT必须)。 |
| `DshCleaner.cs` | **卸载专核**：剥离自启项，IO 强删下层目录，Temp 落盘自焚 `.bat` 的操作集成。 | 需要仔细处理权限与进程互斥锁定。 |

## 3. 跨进程 IPC 设计
*   由于 NPM 指令（下载/更新）频率极低且时间较长，用传统的 `Process.Start` 把标准输出流用异步委托导回记录，是资源开销最低、兼容性最好的防线，远胜于强耦合去打内嵌 Node 脚本系统。
*   主 dsh 进程也是长生命周期子进程，父进程 (桌面壳) 必须持有一个安全的句柄，并在 `AppDomain.CurrentDomain.ProcessExit` 等关键钩子期主动向其发送 `Kill()` 树状清剿，防止留下僵尸 Node。