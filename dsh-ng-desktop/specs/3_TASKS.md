# 开发任务状态机

## M0：重构基线

- [x] **M0.1 方案定稿**：重写产品需求、架构和验收规范，确立安装器事务、npx 私有缓存、原生安装 UI、WebView 业务 UI、Avalonia 托盘和干净卸载边界。
- [x] **M0.2 代码归零**：移除旧 Win32 托盘、`npm install` 调度、更新逻辑、旧安装向导和 WebView 实验窗口，保留可构建的 Avalonia 工程骨架与品牌资源。
- [x] **M0.3 发布约束定稿**：确立 GitHub Releases 单一发行渠道、Native AOT 正式准入、非 AOT self-contained 兼容构建和源码构建边界。

## M1：共享基础与平台边界

- [x] **M1.1 应用状态机**：实现显式的安装、启动、就绪、停止、回滚和故障状态。
- [x] **M1.2 数据所有权**：实现 `AppPaths` 与 `InstallManifest`，覆盖 Windows 和 macOS 的全部受管路径。
- [x] **M1.3 日志系统**：实现安装/运行日志、轮转、脱敏、打开位置和复制诊断信息。
- [x] **M1.4 单实例与 IPC**：重复启动时激活已有主窗口。
- [x] **M1.5 平台服务接口**：隔离进程组、自启动、安装注册和卸载能力。
- [x] **M1.6 环境诊断**：实现安装器、故障页面和 `--doctor` 复用的只读检测与结构化结果。
- [x] **M1.7 AOT 工程门禁**：启用 AOT/Trim/Single-file 分析、Source Generator 约束和依赖兼容性检查。

## M2：DSH 供应与监督

- [x] **M2.1 Node 环境检测**：定位并验证系统 `node` 与 `npx`，缺失时给出原生错误和修复指引。
- [x] **M2.2 私有运行环境**：配置独立 npm cache、`DSH_HOME` 和安全启动目录。
- [x] **M2.3 npx 启动**：使用未锁版本的 `npx @deepseek-ai/dsh web` 后台供应并运行 DSH。
- [x] **M2.4 端口与健康检查**：实现端口复用、冲突迁移、竞态重试和 DSH 身份验证。
- [x] **M2.5 进程所有权**：实现 Windows Job Object、macOS process group、优雅停止和超时强制停止。
- [x] **M2.6 异常恢复**：DSH 异常退出时进入故障状态，提供一次启动重试、一次私有缓存修复重试和日志入口。
- [x] **M2.7 本地测试隔离**：将本机单测工程固定在 `dsh-ng-desktop/LocalTests/`，由 Git 忽略，并覆盖 M2 的可控进程与 HTTP 边界。
- [x] **M2.8 首次供应等待、摘要进度与安全停止**：首次 npx 供应持续等待至就绪、进程退出或用户停止；展示阶段、等待时长和可读摘要日志，并避免 Windows 调试宿主受到 Ctrl+Break 广播影响。
- [x] **M2.9 DSH 更新活动与一致启动状态**：以可验证的 npx 阶段显示检查与更新 DSH，避免重复等待计时；前台原生窗口和后台托盘共享启动状态。
- [x] **M2.10 DSH 运行错误状态**：将启动失败、运行中异常退出、检查更新网络错误和更新失败映射为可读状态，并在主窗口与托盘一致显示。

## M3：安装器事务

- [x] **M3.1 原生安装引导**：实现 Avalonia 环境检测、阶段进度、日志摘要、停止和失败页面。
- [x] **M3.2 事务提交与补偿**：DSH 健康检查通过后才提交；取消或失败时完整回滚。
- [x] **M3.3 Windows 安装器**：实现当前用户安装、系统卸载注册和失败回滚。
- [x] **M3.4 macOS 兼容性实现**：实现 macOS 平台接口、运行时和 UI 兼容性代码，为同一安装事务和桌面生命周期提供跨平台基础。
- [x] **M3.5 中断恢复与安装位置**：旧数据时提供保留数据的覆盖安装或明确确认的全新安装，并在中文安装引导中提供不创建目录的安装位置入口。
- [x] **M3.6 意外取消可见失败**：将非用户触发的取消统一回滚并显示原生错误，不允许其终止安装器进程。
- [x] **M3.7 全新安装清理恢复**：清理只读的产品私有缓存，不跟随重解析点；失败时显示受管根目录和具体失败项。

## M4：桌面运行体验

- [x] **M4.1 标准主窗口**：使用原生标题栏承载安装/故障原生视图和 DSH WebView。
- [x] **M4.2 WebView 数据隔离**：实现 Windows WebView2 私有目录与 macOS WKWebView 独立数据存储。
- [x] **M4.3 Avalonia 托盘**：实现平台原生菜单、“打开 DSH”和“退出”。
- [x] **M4.4 窗口生命周期**：正常关闭隐藏到托盘；安装中关闭进入停止与回滚确认。
- [x] **M4.5 后台自启**：安装成功默认注册，后台启动不弹窗，并尊重操作系统禁用状态。

## M5：干净卸载与发布

- [x] **M5.0 回滚窗口关闭竞争**：停止/回滚阶段拦截再次关闭请求，保留窗口直至回滚终态和清理结果可见。
- [x] **M5.1 卸载协作**：运行中应用接收带确认的卸载请求，销毁 WebView、停止 DSH，并由卸载助手等待实例完全退出。
- [x] **M5.2 精确清理**：删除安装清单内的客户端、npx、DSH_HOME、日志和 WebView 数据，保留工作区和系统 Node。
- [x] **M5.3 Windows 验收**：完成安装成功、失败回滚、自启、托盘、退出、卸载和残留检查。
- [x] **M5.4 macOS 平台基础**：完成 macOS 平台接口、运行时、托盘、进程组和安装事务的兼容性基础。
- [x] **M5.5 Windows Native AOT 基线验证**：完成本机 `win-x64` AOT 产物验证，确保 AOT/Trim 警告为零。
- [x] **M5.6 Windows .NET 依赖与源码构建**：验证 `win-x64` .NET 依赖安装器和普通源码构建复用相同行为。
- [x] **M5.8 Windows 旧包装链清理**：移除 IExpress、`cmd.exe` 和 PowerShell 包装实现及失效构建入口；保留 Avalonia 安装事务、部署回滚、系统注册和卸载协调代码。
- [x] **M5.9 Windows SetupHost 单文件入口**：新增无控制台、无 Avalonia 依赖的 Native AOT SetupHost；内嵌一份完整客户端负载，实施安全解压、文件清单与 SHA-256 校验、直接子进程同步等待、退出码传递和 staging 清理。
- [x] **M5.10 Windows 显式安装会话**：普通客户端不再根据执行位置隐式进入安装模式；SetupHost 以显式参数启动唯一的 Avalonia 安装窗口，成功关闭后先清理临时负载再启动已安装客户端。
- [x] **M5.11 Windows win-x64 双构建安装器体验**：AOT 与 .NET 依赖客户端包使用清晰文件名和同一 AOT SetupHost；安装过程不显示终端或第二套安装 UI，完成系统应用列表、自启动、失败回滚和卸载验收。
- [x] **M5.12 Windows 快捷方式闭环**：安装事务创建当前用户桌面和开始菜单快捷方式，失败回滚、全新安装清理与卸载精确删除快捷方式。
- [x] **M5.13 隐藏窗口 WebView 回收**：关闭主窗口时销毁 NativeWebView 并保留 DSH/托盘，重新打开时按 Ready 快照重建 WebView，避免隐藏期间保留 WebView2 管理器进程。
- [x] **M5.14 Windows GitHub 自动发布基线**：`desktop-v*` 标签在 Windows Runner 构建双安装器和 SHA-256，并创建未签名社区预览 Release，无需手工上传附件。
- [x] **M5.15 项目 README 收尾**：完善根 README 的生态总览与简要安装入口，完善桌面子项目 README 的功能、前置条件、安装、运行、卸载、开发与发布说明。
- [x] **M5.16 运行中安装协作**：为保留数据的安装替换增加安装专用 IPC、维护锁和限时失败路径；运行中客户端先销毁 WebView、停止 DSH 并退出，安装器确认交接后才部署。安装事务整体在后台执行，避免交叉形态的停止等待和目录操作冻结 Avalonia UI；用户已完成 Windows 交叉安装验证。
- [x] **M5.17 安装版本与构建形态识别**：将 SemVer 和 AOT/.NET 依赖元数据写入安装负载与清单，动态显示升级、修复、降级和构建形态切换提示，并兼容旧清单及早期错误路径化的版本字段；用户已确认升级、降级和构建形态切换检测正常。
- [x] **M5.18 交叉构建单元测试**：补充跨 AOT/.NET 依赖运行中替换、IPC 超时/拒绝、维护锁、本地版本分类和错误版本字段迁移的简单测试；本地测试与用户交叉测试均已完成。
- [x] **M5.19 AOT 安装选择页响应性**：将窗口打开阶段的旧清单读取与版本/构建形态分类移出 Avalonia UI 线程，读取失败显示可关闭错误；用户已确认 .NET 依赖版到 AOT 安装选择页和安装流程正常。

## M6：跨平台未签名发行与人在环协作

- [x] **M6.0 发行与协作 Spec 定稿**：发行范围固定为 Windows `win-x64` 与 macOS `osx-arm64`；版本由维护者通过 Changelog 和标签决定，发布流程按规划、人工批准、执行、验收和人工发布批准拆分。
- [x] **M6.1 规划门禁（强模型）**：已检查现有 macOS 打包资料、Windows 构建入口、平台安装边界和 Release 工作流，确认 macOS `pkg` 基础、当前用户安装事务和 Windows 双安装器可复用；执行范围包含移除 macOS 签名/公证、补足 macOS 安装元数据传递、跨 Runner 汇总发布及文档更新。
- [x] **M6.2 人工批准实施计划**：维护者已批准 M6.1 计划并授权按其顺序执行。
- [x] **M6.3 macOS `osx-arm64` 未签名安装包（执行模型）**：macOS 打包入口已收敛为单一 Native AOT `pkg`，已移除签名与公证输入及命令，可生成可复现的 SHA-256，并通过显式安装参数将 SemVer 和 AOT 构建形态写入 macOS 安装清单；保留当前用户安装事务。
- [x] **M6.4 跨平台构建与发布（执行模型）**：已实现 Changelog/标签校验、Windows `win-x64` 双安装器和 macOS `osx-arm64` AOT 包的并行构建，以及仅在全部成功后创建 Release 的汇总任务；Windows 双安装器已在隔离目录完成真实构建和 SHA-256 复核。
- [x] **M6.5 发行说明与文档（执行模型）**：已添加桌面项目 Changelog；Release 正文使用精炼 Changelog、提交数量和比较链接；已更新打包文档、GitHub 专用的提交/标签推送顺序和 Release 创建前的误推恢复规则，并在根 README 引用 `dsh-ng-desktop-install-white.png`、在项目 README 引用其余三张截图。
- [x] **M6.6 验收门禁（强模型）**：已独立核对实现、工作流最小权限、产物命名、SHA-256、未签名风险说明、Changelog/标签一致性和自动检查证据；项目编译、Bash 语法检查、Windows 双安装器真实构建与 SHA-256 复核均通过。未覆盖风险为真实 Apple Silicon 安装与首次实际 GitHub 标签运行，保留给 M6.7/M6.8。
- [ ] **M6.7 人工验收与发布批准**：Windows `win-x64` 实机安装和使用保持通过；macOS `osx-arm64` 因真实 0.9.11 包在安装入口暴露跨平台阻断而重新打开，必须完成 M7 的修复与独立验收后才能批准跨平台发布。
- [ ] **M6.8 首次跨平台发行验证**：M7 通过并获得人工发布批准后，推送新的 `desktop-v<SemVer>` 标签，确认 Release 只含 Windows `win-x64` AOT/.NET 安装器、macOS `osx-arm64` AOT `pkg`、各自 SHA-256、Changelog 更新说明和提交范围摘要；不得替换已发布的 0.9.11 附件。

### M6.1 执行计划

1. **macOS 包**：将 `build-installer.sh` 固定为 `osx-arm64` + Native AOT，改用项目 NuGet 配置恢复并显式传入版本；删除 `codesign`、`productsign`、`notarytool` 和 `stapler`。构建时从模板生成 postinstall 脚本，使 bootstrap 收到 `--package-version <SemVer>` 与 `--build-flavor Aot`；应用入口在有效 macOS 安装会话中读取并保存该元数据。同步修正仅描述签名假设的注释。
2. **工作流**：采用 `prepare -> {build-windows, build-macos} -> publish`。准备任务校验标签和 Changelog 条目；Windows 任务继续在 `windows-latest` 构建两个 EXE，macOS 任务固定在 ARM64 `macos-14` 构建一个 PKG。两个任务分别上传附件，发布任务仅在二者成功后下载并精确核对六个文件，再创建 Release。
3. **Release 正文**：发布任务从 Changelog 提取目标版本的小节，附加从上一个桌面标签到当前标签的提交数量和 GitHub 比较链接；使用 `gh release create --notes-file`，不使用 `--generate-notes`、逐条提交列表或自动版本写入。
4. **文档与验收**：新增 Changelog 模板并更新打包/发布指南与两份 README 截图。自动验证包括 Bash 语法检查、工作流 YAML 结构检查、Windows 现有构建及 macOS Runner AOT/PKG 构建；人工验证包括 Windows `win-x64` 与 macOS `osx-arm64` 的安装、启动、托盘、卸载、SHA-256 与安全提示。任一构建或人工验收失败时不推送发行标签；已发布版本只能通过新版本修复，不能替换附件。

## M7：macOS ARM64 兼容性修复与发布门禁

- [x] **M7.0 规划（Plan）**：基于真实 Apple Silicon 0.9.11 崩溃、`--doctor` 结果、发布包文件 mode 和 macOS 平台代码完成只读审计；范围限定为 macOS 维护锁、Unix 权限复制、Node/npx 定位、进程组、pkg 结果传播与暂存清理、WKWebView 数据清理、崩溃符号和真实产物门禁。Windows 现有安装行为作为回归基线，不在本里程碑重写。
- [ ] **M7.1 人工批准（Approve）**：维护者审阅 M7 范围、执行顺序、风险、验证证据与回退策略，并明确授权新会话进入实现；未批准前不得修改产品代码、打包脚本或发布工作流。
- [ ] **M7.2 跨平台维护锁（Execute）**：保留 Windows 已验证行为；macOS 以按用户隔离、进程退出自动释放、可跨异步等待持有的排他文件锁或等价平台租约替代命名 `Semaphore`。覆盖首次启动、第二实例、安装维护和卸载锁交接，不以捕获异常后跳过互斥作为修复。
- [ ] **M7.3 Unix 负载权限（Execute）**：部署 `.app` 时复制普通文件 Unix mode，在 staging 和最终 `InstallRoot` 分别验证主程序及卸载命令的用户执行位；构造权限缺失测试确认安装可见失败并回滚，不能提交 `0644` 主程序。
- [ ] **M7.4 macOS Node/npx 解析（Execute）**：抽取统一解析器，使安装前置检查、`--doctor`、Finder 启动和 LaunchAgent 登录启动均能解析并验证绝对路径；覆盖 Apple Silicon Homebrew 和至少一种用户级版本管理器，日志只记录来源类别、路径和版本，不记录完整环境。
- [ ] **M7.5 spawn-time 进程所有权（Execute）**：把 macOS npx 启动与 process group 创建收敛到执行前平台边界，移除 `Process.Start` 后调用 `setpgid(childPid, childPid)` 的竞态；绑定失败只停止本次创建的进程，停止测试证明不会影响其他 Node 进程。
- [ ] **M7.6 macOS pkg 事务闭环（Execute）**：postinstall 在有效控制台用户会话中同步等待 Avalonia bootstrap、保存可定位诊断并传播真实退出码；无控制台用户、bootstrap 崩溃、Node 缺失和用户停止均使 Installer 失败。事务退出后精确删除系统 bootstrap 暂存，成功才启动受管客户端，失败不留可运行半安装。
- [ ] **M7.7 macOS WebView 与卸载（Execute）**：增加 WKWebsiteDataStore 平台清理能力，分别覆盖 macOS 13 兼容存储和 macOS 14+ identifier 存储；卸载后确认用户安装根、LaunchAgent、产品数据、WebView 数据及 `/Library/Application Support/DSH Desktop Installer` 均不存在，只允许保留操作系统维护且无对应负载的 package receipt。
- [ ] **M7.8 包元数据与诊断（Execute）**：补齐 macOS `.app` 的 bundle 类型与 `.icns` 元数据；构建阶段验证 arm64 架构、执行位、dylib 和 Info.plist，并把匹配 dSYM 保存为内部验证工件而不装入用户 pkg。
- [ ] **M7.9 自动验证（Verify）**：增加能实际执行 `osx-arm64` AOT 二进制的门禁，至少覆盖维护锁、权限复制、Node 解析、进程组所有权、postinstall 退出码和包结构；运行宿主不是 arm64 时必须明确失败或转交真实 Apple Silicon 门禁，不能只完成交叉编译、SHA-256 或 `--doctor`。
- [ ] **M7.10 Windows 回归（Verify）**：运行现有 `win-x64` 构建、安装事务、单实例、运行中替换、DSH 启停和卸载验证，确认共享接口调整未改变当前稳定的 Windows 行为。
- [ ] **M7.11 macOS 实机验收（Verify）**：在干净 Apple Silicon 用户环境安装真实新版本 pkg，依次验证首次安装、Finder 启动、DSH WebView、托盘、退出、登录自启、覆盖安装、失败回滚和卸载；分别使用 Homebrew Node 与用户级版本管理器 Node，并记录 `stat`、进程组、launchctl、残留路径和日志证据。
- [ ] **M7.12 发布批准（Verify）**：独立验收 Agent 核对 M7.9-M7.11 证据，维护者确认新 SemVer 和 Changelog 后才允许推送新标签；0.9.11 保持不可变并明确标记 macOS 已知不可用，不覆盖其附件或校验值。

### M7.0 执行计划

1. **先修安装入口**：依次处理维护锁和 Unix mode，使 bootstrap 能进入安装事务且部署出的 `.app` 可执行；每步先补可自动复现的失败用例，再改平台实现。
2. **再修运行链**：统一 macOS Node/npx 解析并建立 spawn-time process group；所有共享接口保留 Windows 默认实现，禁止借机重构已稳定的 Windows 安装器。
3. **闭合 pkg 与卸载**：让 postinstall 同步传播结果、清理系统暂存并在成功后启动目标；通过平台 WebKit API 和安装清单完成用户数据清理，package receipt 仅作为无负载的系统历史记录。
4. **补发布可观测性**：包构建验证架构、权限、依赖和 bundle 元数据，保存匹配 dSYM；自动门禁必须执行真实 arm64 产物的高风险路径，`--doctor` 只作为其中一项而非发布结论。
5. **风险控制**：维护锁不得退化为无锁；Node 解析不得执行来自路径或环境的拼接 shell 命令；权限修复不得给非必要文件增加执行位；进程停止不得按名称枚举；清理只处理精确产品路径与 WebKit 产品存储。
6. **回退与发布**：每个 Execute 任务保持可独立回退且完成后立即更新状态。任一 macOS 或 Windows 验证失败时停止发布、保留日志和 dSYM，不移动或覆盖既有标签；仅以新的 SemVer 修复版本重新进入 M7.9-M7.12。
