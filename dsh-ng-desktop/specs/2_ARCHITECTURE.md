# 技术架构与工程规范

## 1. 技术选型

- **应用框架**：.NET 10 与 Avalonia 12。
- **业务界面**：Avalonia `NativeWebView` 内嵌 DSH Web UI。
- **安装界面**：Avalonia 原生控件，不依赖 WebView。
- **托盘**：Avalonia `TrayIcon`、`TrayIcons` 与 `NativeMenu`。
- **进程启动**：系统已有的 Node.js 与 npx；不携带私有 Node.js。
- **发布方式**：各目标平台独立、自包含构建；AOT 可继续评估，但不作为首轮重构的准入条件。
- **平台优先级**：先完成 Windows，再以相同接口实现 macOS。

## 2. 总体边界

系统由两个产品生命周期组成：

1. **安装器生命周期**：部署客户端文件、运行原生安装引导、供应 DSH、健康检查、提交或回滚安装事务。
2. **桌面客户端生命周期**：单实例运行、启动和停止 DSH、承载 WebView、托盘驻留、处理自启动和卸载协作。

Windows 与 macOS 必须分别产出安装器和应用包。安装流程、状态机、日志和 DSH 调度逻辑共享；文件部署、自启动、进程组、卸载和签名由平台适配层负责。

## 3. 安装事务状态机

安装器按以下单向状态推进：

`Preflight -> DeployingClient -> ProvisioningDsh -> WaitingForWebUi -> Registering -> Committed`

任一提交前状态可进入：

`Stopping -> RollingBack -> Failed`

约束：

- `Preflight` 检测 OS、架构、目录权限、Node、npx 和 WebView 前置条件。
- 客户端文件在 `Committed` 前属于可回滚资源。
- `ProvisioningDsh` 使用原生窗口展示阶段进度和实时日志；进度采用阶段完成度，不伪造下载百分比。
- `WaitingForWebUi` 必须验证返回内容属于 DSH Web UI。
- 自启动与系统卸载注册放在事务末尾；部分失败必须执行补偿操作。
- 失败日志先供用户查看，退出安装器时再按失败清理策略处理。

## 4. DSH 运行环境

### 4.1 命令和环境

基础命令为：

`npx --yes @deepseek-ai/dsh web --host 127.0.0.1 --port <port>`

每次启动均使用未锁版本的包规格。客户端不查询 registry 版本，也不区分“升级”和“普通启动”。

子进程环境至少固定：

- `npm_config_cache=<AppData>/runtime/npm-cache`
- `DSH_HOME=<AppData>/dsh-home`
- 无交互安装确认和适合日志采集的终端设置

工作目录固定为 `<AppData>/runtime/launcher-cwd`。该目录只用于安全启动，不作为用户工作区保存业务文件。

### 4.2 数据目录

逻辑目录统一由 `AppPaths` 管理，不允许业务代码自行拼接散落路径：

| 目录 | 内容 | 卸载 |
|---|---|---|
| `InstallRoot` | 客户端程序和卸载组件 | 删除 |
| `AppData/state` | 安装清单、端口、进程所有权信息 | 删除 |
| `AppData/logs` | 安装与运行日志 | 删除 |
| `AppData/runtime/npm-cache` | npx 私有执行缓存和 DSH 包 | 删除 |
| `AppData/dsh-home` | DSH profiles、配置、凭据、插件、会话 | 删除 |
| `AppData/runtime/launcher-cwd` | DSH 安全启动目录 | 删除 |
| `AppData/webview` | Windows WebView2 用户数据 | 删除 |
| 外部 workspace | 用户选择的项目目录 | 永不删除 |

Windows 使用 `%LocalAppData%` 下的产品专属根目录；macOS 分别使用用户级 Application Support 和 Caches 目录，并由统一所有权清单记录实际路径。

### 4.3 进程所有权

- `DshSupervisor` 创建并拥有 npx 进程组及全部子进程，记录 PID、进程启动时间和实例标识。
- Windows 使用进程组与 Job Object；macOS 使用独立 process group。
- 正常停止先发送平台对应的优雅终止信号，并给 DSH 预留清理时间；超时后终止受管进程组。
- 禁止枚举并结束所有 `node`、`npm`、`npx` 或 `dsh` 名称的进程。
- 未通过所有权校验的既有 loopback 服务视为外部进程，客户端不得接管或终止。

### 4.4 端口和健康检查

- 首次优先使用 3080，之后优先复用已持久化端口。
- 未知进程占用时选择新端口并持久化；启动时仍须处理检测后端口被抢占的竞态。
- 健康检查验证 HTTP 成功、DSH 页面特征和进程仍存活。
- WebView 只在健康检查成功后创建并导航到实际 loopback 地址。

## 5. 桌面应用结构

### 5.1 逻辑模块

| 模块 | 职责 |
|---|---|
| `ApplicationCoordinator` | 单实例、应用状态机、窗口与托盘生命周期 |
| `SetupCoordinator` | 环境检测、安装阶段、取消、补偿和安装结果 |
| `DshSupervisor` | npx 启动、输出采集、健康检查、停止与异常退出 |
| `AppPaths` / `InstallManifest` | 跨平台目录与资源所有权清单 |
| `PlatformServices` | 安装、自启动、进程组、卸载和平台差异 |
| `MainWindow` | 原生安装/错误页面与就绪后的 WebView 容器 |
| `AppLog` | 结构化日志、滚动、脱敏和日志定位 |

不再限制 `.cs` 文件数量，也不把平台互操作堆入 `Program.cs`。保持直接、可测试的服务边界，不为简单 UI 引入重型框架。

### 5.2 窗口和 WebView

- 使用操作系统标准窗口装饰和原生标题栏。
- 安装、启动、停止和故障状态由 Avalonia 原生视图呈现。
- `NativeWebView` 直接浏览 DSH Web UI，不实现导航白名单、外部链接拦截或宿主脚本桥接。
- Windows 在 WebView 环境创建前设置私有 `UserDataFolder`。
- macOS 使用固定 `DataStoreIdentifier` 隔离 WKWebView 持久数据；卸载通过平台清理能力删除。
- WebView 销毁且浏览器子进程退出后，卸载器才可删除相关数据。

### 5.3 托盘和单实例

- Avalonia 应用使用显式退出模式，关闭主窗口只隐藏窗口。
- 托盘命令最少包含“打开 DSH”和“退出”；macOS 遵循菜单栏原生点击行为。
- 第二实例通过本机 IPC 通知第一实例显示窗口，然后退出。
- 自启动使用 `--background` 参数；后台启动时创建托盘并启动 DSH，不显示主窗口。

## 6. 平台发行

### 6.1 Windows

- 提供面向当前用户的安装器，安装目录位于用户可管理的应用目录。
- 注册系统卸载入口和当前用户自启动项。
- 安装器、主程序、卸载器共享产品 ID 和安装清单。
- 卸载器在应用退出后删除剩余文件，不使用应用本体自删或按名称杀进程。

### 6.2 macOS

- 分别构建 Apple Silicon 与 Intel 产物，并完成签名、公证和安装器封装。
- 正式安装器执行与 Windows 相同的供应事务；不把拖拽 `.app` 视为完整安装流程。
- 使用 Service Management 注册和注销登录项。
- 提供明确的完整卸载入口，清除 app bundle、Application Support、Caches 和 WebKit 数据存储。

## 7. 日志与错误

- 安装日志和运行日志分离，包含阶段、时间、进程退出码、健康检查结果和平台错误。
- UI 展示简短错误摘要并提供“打开日志位置”和“复制诊断信息”。
- 日志不得记录 API Key、认证令牌和完整敏感环境变量。
- 日志采用大小与数量上限轮转，卸载时删除。

## 8. 编码约束

- 外部进程、HTTP、文件系统和平台 API 是严格错误边界，错误不得静默吞掉。
- 内部状态转换必须显式，禁止用若干布尔值组合隐式表达安装状态。
- 删除操作只能针对 `InstallManifest` 记录且验证位于产品根目录内的路径。
- 平台代码通过窄接口隔离；共享业务逻辑不得散布 OS 条件分支。
- 注释解释业务原因和平台限制，不翻译代码表面行为。
