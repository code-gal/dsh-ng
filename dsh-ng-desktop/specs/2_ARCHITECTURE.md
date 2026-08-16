# 技术架构与工程规范

## 1. 技术选型

- **应用框架**：.NET 10 与 Avalonia 12。
- **业务界面**：Avalonia `NativeWebView` 内嵌 DSH Web UI。
- **安装界面**：Avalonia 原生控件，不依赖 WebView。
- **Windows 单文件宿主**：独立的 Native AOT `WinExe` SetupHost，不引用 Avalonia，只负责内嵌负载校验、安全解压、同步进程等待、退出码传递和临时目录清理。
- **托盘**：Avalonia `TrayIcon`、`TrayIcons` 与 `NativeMenu`。
- **进程启动**：系统已有的 Node.js 与 npx；不携带私有 Node.js。
- **AOT 发布**：各目标平台独立的 Native AOT、自包含安装产物；Native AOT 是硬性准入条件。
- **.NET 依赖发布**：Windows 额外提供框架依赖安装产物，要求目标系统已有匹配的 .NET Desktop Runtime；源码开发同样使用框架依赖构建。
- **发行渠道**：仅通过 GitHub Releases 发布版本化安装包、校验值和签名状态；当前 Windows 可发布醒目标记的未签名社区预览，macOS 不生成项目发行安装包。
- **平台范围**：Windows `win-x64` 是唯一发布和验收目标；macOS 仅保留兼容性实现，供外部开发者自行构建验证。

## 2. Native AOT 契约

### 2.1 工程约束

- 除明确标识的 `.NET 依赖`客户端负载外，所有正式发行的 .NET 可执行程序均必须启用 `PublishAot`；若平台安装器本身不是 .NET 程序，则不得引入额外 .NET 运行时依赖。
- 项目持续启用 AOT、Trim 和 Single-file 分析器，AOT/Trim 警告作为构建失败处理。
- 启用依赖 AOT 兼容性检查；第三方依赖的精确抑制必须附带原因和真实 AOT 发布验证，禁止全局关闭分析器。
- JSON 使用 `System.Text.Json` Source Generator；平台调用优先使用 `LibraryImport` Source Generator。
- 禁止 `Reflection.Emit`、运行时程序集加载、反射扫描式注册、动态代理、运行时代码编译和其他依赖 JIT 的机制。
- 必须使用 Avalonia 编译 XAML、显式服务注册、静态命令映射和可静态分析的平台工厂。

### 2.2 构建模式

- **AOT 模式**：桌面客户端使用 `PublishAot=true`、self-contained，按 RID 生成本机负载；Windows SetupHost 同样为 AOT、自包含。
- **.NET 依赖模式**：桌面客户端使用 `PublishAot=false`、`SelfContained=false`，复用相同功能、安装器事务和验收用例，要求目标机器安装匹配的 .NET Desktop Runtime；Windows SetupHost 仍为 AOT、自包含，并在启动客户端负载前检查运行时。
- **源码模式**：开发者使用 .NET SDK 执行普通 `dotnet build`、`dotnet run -- --development` 和测试；`--development` 显式声明非安装目录的开发运行期。
- Native AOT 的项目验证范围固定为本机 `win-x64`；不维护 `win-arm64` 或 macOS 的 RID 构建矩阵。
- 普通编译成功不能代替 Native AOT 发布验证；安装器、托盘、平台互操作和 `NativeWebView` 必须由真实 AOT 产物执行冒烟测试。

## 3. 总体边界

系统由一个不可见的运输宿主和两个产品生命周期组成：

1. **SetupHost 运输生命周期（Windows）**：从单文件读取内嵌客户端负载，校验并安全解压到独占临时目录，以显式安装会话启动客户端，等待其退出后清理临时目录并传递结果。它没有安装向导 UI，不参与产品事务状态机。
2. **安装器生命周期**：由临时负载中的 Avalonia 客户端显示唯一安装界面，部署客户端文件、供应 DSH、健康检查、提交或回滚安装事务。
3. **桌面客户端生命周期**：单实例运行、启动和停止 DSH、承载 WebView、托盘驻留、处理自启动和卸载协作。

Windows `win-x64` 产出安装器和应用包。安装流程、状态机、日志和 DSH 调度逻辑保持跨平台可复用；macOS 平台适配代码只作为兼容性实现，不产出、发布或验证项目发行物。

## 4. 安装事务状态机

安装器按以下单向状态推进：

`Preflight -> DeployingClient -> ProvisioningDsh -> WaitingForWebUi -> Registering -> Committed`

任一提交前状态可进入：

`Stopping -> RollingBack -> Failed`

约束：

- `Preflight` 检测 OS、架构、目录权限、Node、npx 和 WebView 前置条件。
- 客户端文件在 `Committed` 前属于可回滚资源。
- `ProvisioningDsh` 使用原生窗口展示单一主状态、单一已等待时长和经整理的实时摘要；npx 无稳定的进度 API，因此只显示“检查 DSH 更新”、实际下载/解压/安装所触发的“更新 DSH”和启动验证等可证明的阶段，不伪造下载百分比。摘要日志面板自动换行、紧凑呈现且可选择复制；仅当视图位于末尾且没有文本选择时才自动跟随新内容。
- `WaitingForWebUi` 必须验证返回内容属于 DSH Web UI。
- 自启动与系统卸载注册放在事务末尾；部分失败必须执行补偿操作。
- 失败日志先供用户查看，退出安装器时再按失败清理策略处理。

### 4.1 SetupCoordinator 与部署边界

- `SetupCoordinator` 是安装事务的唯一编排者。它只通过 `EnvironmentDoctor`、`IClientDeployment`、`DshSupervisor`、`IPlatformServices` 和 `InstallManifest` 操作外部资源，界面不得直接复制文件、注册系统项或结束进程。
- 安装包负载目录与目标 `InstallRoot` 必须不同。`IClientDeployment` 先在目标目录的同级临时目录完整复制负载，再原子移动到受管 `InstallRoot`；已存在的目标目录不覆盖，避免失败安装破坏已提交的客户端。
- Windows SetupHost 单文件内只保存一份压缩客户端发布负载。它将该负载解压到本次运行独占的 staging 目录，使用同一目录同时作为 Avalonia 安装引导的启动闭包和 `IClientDeployment` 的源 payload，避免重复嵌入客户端。
- SetupHost 是无控制台 `WinExe`，不引用 Avalonia，不通过 IExpress、`cmd.exe`、PowerShell 或 shell 关联启动客户端。它以直接子进程方式传入 `--install --installer-session --payload <staging>`，持有进程句柄并等待退出；只有子进程结束后才能清理 staging。
- `DshNgDesktop.exe` 只有在有效的显式安装会话、已验证的受管安装目录，或带 `--development` 的源码宿主三种角色之一时才可启动；普通无参数启动绝不因缺少清单或执行目录而转入安装事务。
- SetupHost 解压每个条目前必须规范化目标路径并验证其仍位于 staging 根目录内，拒绝绝对路径、父目录穿越、符号链接和其他重解析点。启动前必须验证清单中的文件长度和 SHA-256，至少确认主程序及其声明的全部原生运行库存在。
- SetupHost 不复制产品文件、不注册自启动或卸载入口、不启动 DSH，也不解释安装事务的业务阶段；这些行为仍由 `SetupCoordinator` 独占。它只在负载校验、解压或子进程创建失败时显示系统原生错误对话框。
- Avalonia 安装引导以退出码向 SetupHost 报告结果：提交成功且用户关闭完成页后返回 `0`；失败、停止或回滚完成后返回非零。成功时 SetupHost 先清理 staging，再从受管 `InstallRoot` 启动已安装客户端；失败时只清理 staging 并原样返回失败码。
- `Preflight` 只把平台、Node、npx 与产品数据父目录的错误作为阻断条件；端口冲突和 WebView 缺失以可理解的预警显示，DSH 端口由 Supervisor 在后续阶段迁移。
- 事务完成顺序固定为：前置检测、部署客户端、启动并验证 DSH Web UI、注册当前用户自启动、注册平台卸载入口、保存 `InstallManifest`、提交。未到 `Committed` 的资源必须按相反顺序补偿。
- 用户主动停止和外部边界产生的意外取消必须分别记录，但两者都必须进入 `Stopping -> RollingBack -> Failed` 并保留原生失败页；不得让 `OperationCanceledException` 从安装窗口的异步事件处理器逸出导致进程退出。
- 安装窗口仅可在尚未请求停止时将关闭操作转换为“停止并回滚”确认；一旦已进入 `Stopping` 或 `RollingBack`，所有关闭请求均取消并保留当前回滚视图，直至 `Failed` 或 `Committed` 终态已经呈现。
- 回滚只接受本次部署记录和内存中的、已通过 `AppPaths` 校验的 `InstallManifest`。它先停止 Supervisor，再注销自启动与卸载入口，随后删除清单内目录和本次部署目录；不根据名称、PATH 或工作区内容扩展删除范围。
- 回滚时保留安装日志供失败页查看；安装器退出后才删除该失败日志。失败日志本身不是可运行安装的一部分。
- 启动新事务前，安装窗口检测现有产品状态并要求用户选择处理方式。有效安装清单证明其记录路径可安全处理；无清单但位于 `AppPaths` 精确受管路径内的数据视为中断残留。覆盖安装只允许 `IClientDeployment` 以同级备份目录原子替换 `InstallRoot`，并在失败回滚时恢复原目录；`DSH_HOME`、私有 npm cache、WebView 与日志均不得清理。全新安装仅在用户明确选择后，才可基于当前 `AppPaths` 生成仅用于清理的临时 `InstallManifest`，清理精确记录的路径后部署；安装器当前正在写入的日志始终保留到退出。`ProductDataCleaner` 以受管根目录为边界逐项后序删除，先移除只读属性并有限重试；符号链接和其他重解析点只删除链接本身、不进入其目标。无法验证的安装清单不得覆盖，只允许显式全新安装。
- 安装界面通过只读位置服务打开已存在的 `InstallRoot`，或在部署前打开最近的现有父目录；该操作不得创建目录或改变事务状态。

## 5. DSH 运行环境

### 5.1 命令和环境

基础命令为：

`npx --yes @deepseek-ai/dsh web --host 127.0.0.1 --port <port>`

每次启动均使用未锁版本的包规格。客户端不查询 registry 版本，也不区分“升级”和“普通启动”。

子进程环境至少固定：

- `npm_config_cache=<AppData>/runtime/npm-cache`
- `DSH_HOME=<AppData>/dsh-home`
- 无交互安装确认和适合日志采集的终端设置

工作目录固定为 `<AppData>/runtime/launcher-cwd`。该目录只用于安全启动，不作为用户工作区保存业务文件。

### 5.2 数据目录

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

### 5.3 进程所有权

- `DshSupervisor` 创建并拥有 npx 进程组及全部子进程，记录 PID、进程启动时间和实例标识。
- Windows 使用进程组与 Job Object；macOS 使用独立 process group。
- 正常停止先发送平台对应的优雅终止信号，并给 DSH 预留清理时间；超时后终止受管进程组。
- 禁止枚举并结束所有 `node`、`npm`、`npx` 或 `dsh` 名称的进程。
- 未通过所有权校验的既有 loopback 服务视为外部进程，客户端不得接管或终止。

### 5.4 端口和健康检查

- 首次优先使用 3080，之后优先复用已持久化端口。
- 未知进程占用时选择新端口并持久化；启动时仍须处理检测后端口被抢占的竞态。
- 健康检查验证 HTTP 成功、DSH 页面特征和进程仍存活。
- WebView 只在健康检查成功后创建并导航到实际 loopback 地址。

### 5.5 诊断与有限修复

- `EnvironmentDoctor` 复用安装器的环境探测，检查 Node/npx、目录权限、端口、DSH 健康、WebView 前置条件和自启动状态。
- 诊断默认只读，可供原生故障页面和 `--doctor` 命令使用，并支持结构化输出。
- 运行失败时只允许一次普通重试；确认私有 npx cache 损坏后可删除该缓存并再试一次。
- `DSH_HOME` 属于用户在客户端内形成的数据，不参与自动修复清理；重置它必须是独立、明确、带警告的用户操作，且不属于首轮范围。
- 第一版不引入 Windows Service、macOS LaunchDaemon、常驻更新器或独立 watchdog；仅当真实验收证明进程残留无法由 Job Object/process group 和下次启动恢复解决时再扩展。

### 5.6 DSH Supervisor

- `DshSupervisor` 是唯一允许创建和停止 DSH 进程的共享服务。调用方通过 `DshSupervisorOptions` 提供超时、重试和可替换的进程/HTTP 边界；生产默认值固定为未锁版本的 npx 命令。
- 启动前以受限时的 `node --version` 与 `npx --version` 验证可执行文件。解析顺序遵循当前进程 `PATH`，Windows 同时支持 `.exe`、`.cmd` 和 `.bat`；不调用 npm 安装、不改写系统环境。
- Supervisor 创建 `npm_config_cache`、`DSH_HOME` 和 `launcher-cwd` 后，以 `npx --yes @deepseek-ai/dsh web --host 127.0.0.1 --port <port>` 启动。子进程只继承必要环境，并固定工作目录为 `launcher-cwd`。
- 安装事务中的 DSH 就绪等待没有固定失败时限：它持续至健康检查成功、受管进程退出或安装器停止。Supervisor 定期发布心跳、健康检查状态和经归类的 npx 输出；安装窗口独占已等待时长显示，心跳不得重复改写主状态。npx 输出只能在可识别的下载、解压、安装或服务启动事件时更新用户界面，完整原始输出只写入运行日志。普通运行时启动可使用独立、受限的启动超时；等待、停止和进程退出结果均写入运行日志。
- Supervisor 对 npx 的明确错误输出作保守归类：在更新检查阶段识别网络错误，在实际下载/解压/安装阶段识别更新失败；只在进程退出或健康检查最终失败后进入故障状态。故障快照携带可读错误类别，供主窗口和托盘共享显示；原始 stderr 不直接显示在 UI。
- 端口选择依次尝试已持久化端口、3080 和临时保留的 loopback 端口。启动期间若端口被抢占或进程提前退出，释放候选并换端口重试；从不接管或终止未知监听者。
- 健康检查同时验证 HTTP 成功、响应中的 DSH 页面特征以及所记录子进程仍存活。成功后持久化端口、PID、进程启动时间和实例标识；持久化状态仅用于诊断和下次优先选端口，不能构成对既有进程的所有权声明。
- Windows 的受管进程加入专属 Job Object；macOS 的受管进程加入独立 process group。Windows 不向可能与调试宿主共享的控制台广播 Ctrl+Break；停止先使用安全的进程级优雅请求，超时后仅终止同一 Job Object/process group。绑定失败立即停止刚创建的子进程并报告启动失败。
- 进程异常退出触发一次状态通知和运行日志。`StartWithRecoveryAsync` 最多执行一次普通启动重试；只有调用方明确确认私有 npm cache 损坏时才删除该 cache，并允许一次额外启动，不删除 `DSH_HOME`。

## 6. 桌面应用结构

### 6.1 逻辑模块

| 模块 | 职责 |
|---|---|
| `DshDesktop.SetupHost` | Windows 单文件负载校验、安全解压、同步等待、临时目录清理和退出码传递；无 Avalonia UI |
| `ApplicationCoordinator` | 单实例、应用状态机、窗口与托盘生命周期 |
| `SetupCoordinator` | 环境检测、安装阶段、取消、补偿和安装结果 |
| `DshSupervisor` | npx 启动、输出采集、健康检查、停止与异常退出 |
| `AppPaths` / `InstallManifest` | 跨平台目录与资源所有权清单 |
| `PlatformServices` | 安装、自启动、进程组、卸载和平台差异 |
| `MainWindow` | 原生安装/错误页面与就绪后的 WebView 容器 |
| `AppLog` | 结构化日志、滚动、脱敏和日志定位 |
| `EnvironmentDoctor` | 安装、运行和命令行复用的只读环境诊断与有限修复判定 |
| `DshSupervisor` | Node/npx 验证、私有 npx 启动、端口/健康检查、进程所有权、停止和有限恢复 |

不再限制 `.cs` 文件数量，也不把平台互操作堆入 `Program.cs`。保持直接、可测试的服务边界，不为简单 UI 引入重型框架。

### 6.2 窗口和 WebView

- 使用操作系统标准窗口装饰和原生标题栏。
- 安装、启动、停止和故障状态由 Avalonia 原生视图呈现；Windows 安装引导的可见文本使用简体中文。
- `NativeWebView` 直接浏览 DSH Web UI，不实现导航白名单、外部链接拦截或宿主脚本桥接。
- Windows 在 WebView 环境创建前设置私有 `UserDataFolder`。
- macOS 使用固定 `DataStoreIdentifier` 隔离 WKWebView 持久数据；卸载通过平台清理能力删除。
- WebView 销毁且浏览器子进程退出后，卸载器才可删除相关数据。

### 6.3 托盘和单实例

- Avalonia 应用使用显式退出模式，关闭主窗口只隐藏窗口。
- 托盘命令最少包含“打开 DSH”和“退出”；macOS 遵循菜单栏原生点击行为。
- 第二实例通过本机 IPC 通知第一实例显示窗口，然后退出。
- 自启动使用 `--background` 参数；后台启动时创建托盘并启动 DSH，不显示主窗口。

### 6.4 运行时协调与资源销毁顺序

- `ApplicationCoordinator` 是已安装客户端的唯一运行时编排者：它驱动 `Starting -> Ready -> Stopping -> Stopped` 状态，并转发 `DshSupervisor` 的可读启动活动与故障通知；DSH 异常退出只切换为原生故障视图，不自动无限重启。
- 主窗口仅在 Coordinator 已收到健康检查成功的 loopback URI 后创建 `NativeWebView` 并导航。启动、停止和故障阶段只显示 Avalonia 原生视图；不订阅或改写网页导航、外部链接、脚本消息和资源请求。
- `NativeWebView.EnvironmentRequested` 是唯一的浏览器环境配置点：Windows 创建并使用 `AppPaths.WebViewDataDirectory` 作为 WebView2 用户数据目录；macOS 设置固定的产品 `DataStoreIdentifier`。该目录或数据存储的删除仍只由安装清单清理流程执行。
- 托盘图标由应用级 Avalonia `TrayIcon` 创建。Windows 图标点击和两个平台的原生菜单都可显示主窗口；后台启动保持主窗口隐藏，并根据 Coordinator 快照更新托盘提示文本，避免弹窗打断登录；“退出”先销毁 WebView，再停止 Coordinator 所拥有的 DSH，最后显式关闭 Avalonia 生命周期。
- `SingleInstanceCoordinator` 除激活命令外还承载带确认的卸载命令。运行中客户端收到该命令后，立刻阻止新操作，销毁 WebView、停止 Supervisor 并关闭生命周期；临时卸载助手只在收到确认且确认单实例互斥体已释放后，才注销系统项和清理清单路径。无运行实例时，助手取得同一互斥体后可直接继续。未确认或超时必须以失败退出，不删除文件。清理分两阶段：先保留 `state/install-manifest.json` 并删除其余受管路径和安装根目录，最后才删除状态目录；因此中断后的下一次卸载仍可验证同一清单并继续，所有受管路径已不存在时可幂等成功。
- 安装窗口在事务未结束时拦截窗口关闭，显示原生停止与回滚确认。显式安装会话提交成功后保留完成页，用户关闭时有序释放临时进程拥有的 Supervisor 并以成功码退出；SetupHost 清理 staging 后启动受管 `InstallRoot` 中的客户端，由已安装实例重新建立正常运行期的 Supervisor。临时安装进程不得长期承载桌面运行生命周期，否则 SetupHost 无法安全清理负载。

## 7. 平台发行

### 7.1 Windows

- 提供面向当前用户的安装器，安装目录位于用户可管理的应用目录。
- GitHub Release 中每种构建形态只提供一个 `DshDesktopSetup.exe` 下载物；SetupHost 内嵌完整客户端负载，安装时只显示一套 Avalonia 原生引导 UI。
- 注册系统卸载入口和当前用户自启动项。
- 安装器、主程序、卸载器共享产品 ID 和安装清单。
- 卸载器在应用退出后删除剩余文件，不使用应用本体自删或按名称杀进程。
- 卸载入口从安装目录复制最小卸载助手到临时目录；助手向现有实例发送卸载请求并在限定时间内等待退出。只有不存在现有实例或已观察到其互斥体释放后，才执行清理。
- 每个 Windows 版本只发布和验证本机 `win-x64` AOT 与 .NET 依赖安装器；文件名以 `-aot` 或 `-dotnet` 明确区分构建形态。

### 7.2 macOS

- macOS 代码仅保留平台接口、运行时与 UI 的兼容性实现，不生成安装器、签名/公证包或 GitHub Release 附件。
- 不维护 Intel、Apple Silicon 或 .NET 依赖的项目构建/验证矩阵；外部开发者可自行从源码构建和验证，结果不构成项目发行准入。

### 7.3 GitHub Releases

- GitHub Release 是唯一正式下载入口，不创建 Microsoft Store、WinGet 或客户端更新清单。
- 每个 Windows `win-x64` 版本分别上传 AOT 和 .NET 依赖安装器；.NET 依赖包明确标记为需要 .NET Desktop Runtime。macOS 不上传安装包。
- Release 同时提供 SHA-256 校验值、变更说明、系统与 Node 前置条件和签名状态。未签名 Windows 社区预览必须明确标记 SmartScreen 风险、校验步骤和“不得导入根证书”。
- 正式发行采用目标操作系统上的本机构建与手动上传。桌面客户端使用 `desktop-v<SemVer>` 作为 Git 标签和 Release 名称；安装器文件使用 `DSH-Desktop-Setup-v<SemVer>-<RID>`，使其可与其他子项目的发行物并存。`artifacts/installer/` 是本地输出目录，必须由 Git 忽略。
- 当前不包含自动发布的 GitHub Actions 工作流。未来如加入 CI，只可作为不持有签名私钥的构建/测试门禁，并以路径过滤和 `desktop-v*` 标签限定到本子项目；它不能代替目标机器上的安装、卸载、签名或未签名社区预览风险验收。
- 客户端不查询 GitHub Release，也不提示或安装客户端更新；用户自行获取新版本。

## 8. 日志与错误

- 安装日志和运行日志分离，包含阶段、时间、进程退出码、健康检查结果和平台错误。
- UI 展示简短错误摘要并提供“打开日志位置”和“复制诊断信息”。
- 安装 UI 维护独立的、有界摘要日志缓冲：保留阶段变化、下载/解析/启动/健康检查等可理解事件与必要警告，折叠依赖安装的逐行噪声；完整脱敏日志仍写入文件。新增摘要时不得覆盖用户正在选择的文本或改变其手动滚动位置。
- 日志不得记录 API Key、认证令牌和完整敏感环境变量。
- 日志采用大小与数量上限轮转，卸载时删除。

## 9. 编码约束

- 外部进程、HTTP、文件系统和平台 API 是严格错误边界，错误不得静默吞掉。
- 内部状态转换必须显式，禁止用若干布尔值组合隐式表达安装状态。
- 删除操作只能针对 `InstallManifest` 记录且验证位于产品根目录内的路径。
- 平台代码通过窄接口隔离；共享业务逻辑不得散布 OS 条件分支。
- 注释解释业务原因和平台限制，不翻译代码表面行为。
- 任何新增依赖和实现都必须在合并前通过本机 `win-x64` 的 Native AOT 发布；非 AOT 成功不得作为豁免理由。
