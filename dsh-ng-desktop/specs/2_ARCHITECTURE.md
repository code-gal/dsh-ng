# 技术架构与工程规范

## 1. 工程选型
*   **平台**：.NET 10/11。
*   **发布模式**：保留 `<PublishAot>true</PublishAot>` 配置；本轮先验证普通运行模式，AOT 兼容性单独验收。
*   **UI 形态**：Avalonia 标准桌面生命周期创建无系统装饰的主窗口，自定义标题栏与 `NativeWebView` 分行布局，WebView 加载固定 loopback 地址。
*   **架构形态**：极简扁平的 File-based 面向对象。禁绝重度切层（No MVC/MVVM），本轮只保留 WebView 宿主和最小失败状态。

## 2. 核心模块与文件映射 (最多 4 个 .cs 文件)
为了防止代码腐化或由于单文件过长(超过2000行)引发代码污染，我们将逻辑划分至以下实体：

| 文件名 | 职责划分 | 备注约束 |
|---|---|---|
| `Program.cs` | 标准 Avalonia 启动入口。 | 本轮不创建 Win32 托盘或 DSH 调度对象。 |
| `App.axaml` / `App.axaml.cs` | Avalonia 初始化和主窗口创建。 | 使用经典桌面生命周期。 |
| `Views/MainWindow.axaml` / `MainWindow.axaml.cs` | 自定义标题栏、固定 loopback URL 的 WebView 宿主、加载失败状态和重新加载。 | 标题栏使用原生窗口操作；禁止页面脚本桥接与外部 URL 内嵌。 |
| `DshOrchestrator.cs` / `Configuration.cs` | 既有后续能力。 | 本轮不进入其执行路径。 |

## 3. 跨进程 IPC 设计
*   WebView 固定导航到 `http://127.0.0.1:3080/`，不负责端口发现或外部服务进程生命周期。
*   顶级导航仅允许 loopback 地址；非 loopback 请求取消并使用系统浏览器打开。
*   `NativeWebView` 所依赖的系统 WebView 运行时是外部边界；Windows 缺少 WebView2 时需显示失败状态。
*   WebView 是原生控件宿主，Avalonia 控件不得依赖覆盖它实现交互；标题栏与 WebView 必须在独立布局区域中显示。

## 4. 开发与代码规范 (Coding Standards)
为保持项目强内聚并在 AI 协作中降低理解成本，必须严格遵循以下纪律：
*   **易读性优先**：代码不仅是让计算机执行，更是写给人类和 AI 协作阅读的。保持扁平结构，拒绝炫技式的反人类“一行流”，多用自然平滑的表达逻辑。
*   **注释即意图 (Why over What)**：绝不用注释去翻译代码表面逻辑（What），注释只用来阐述业务背景和技术妥协（Why）。比如规避操作系统的某个 Bug 等。如无必要，勿添注释。
*   **避免过度防御性编程 (Fail Fast)**：对外部输入边界（如网络、文件 IO、跨进程调用）严加防范并提供清晰溯源报错；但对于内部契约调用，不要在每个方法里都写冗余的判空和 “try-catch-all”。错误发生时应尽早暴露（抛出异常），杜绝把异常吃掉而导致的幽灵状态。
