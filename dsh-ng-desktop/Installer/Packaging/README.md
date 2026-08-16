# 安装包构建边界

安装包中的角色严格分离：Windows SetupHost 只运输一份完整客户端发布负载；临时负载中的 `DshNgDesktop` 以显式安装会话显示唯一的 Avalonia 原生窗口并运行安装事务；提交后复制到产品目录的仍是同一份客户端负载。SetupHost 不显示第二套安装向导，也不执行部署、DSH 供应、系统注册或卸载业务。

- Windows：`windows/Build-Installer.ps1` 发布客户端负载并构建 `DshDesktop.SetupHost` Native AOT `WinExe`，生成 `win-x64` AOT 与 .NET 依赖两个 EXE。SetupHost 负责内嵌负载校验、安全解压、直接子进程同步等待、退出码传递和 staging 清理。
- macOS：`macos/build-installer.sh` 只生成 `osx-arm64` Native AOT `pkg`。它不接受或读取签名身份、Apple ID、公证配置或密钥；`pkg` 的 postinstall 在当前登录用户会话中启动同一 Avalonia 安装事务，并将 SemVer 与 AOT 形态写入安装清单。

macOS 构建还会在 `artifacts/installer/.internal/dsym/<SemVer>/` 保存匹配的 dSYM，并编译包内的 WebKit 数据清理 helper；dSYM 不进入用户安装包或公开 Release。发布目标为 macOS 14 及以上的 Apple Silicon。发布前必须在真实 Apple Silicon 上运行 `macos/verify-macos-installer.sh` 与受版本控制的 `ReleaseTests`，不能用交叉编译、SHA-256 或 `--doctor` 单独代替运行门禁。

所有安装包和 SHA-256 文件输出到 `artifacts/installer/`，该目录由 Git 忽略，只能作为 GitHub Release 附件上传。`desktop-v*` 标签的工作流会先校验 `CHANGELOG.md` 的同版本条目，再构建 Windows x64 双安装器和 macOS ARM64 `pkg`；两个构建都成功后才创建 Release 并上传六个附件。

Windows EXE、macOS PKG 及其中的 `.app` 当前均为未签名社区预览。Release 必须展示 SHA-256 校验步骤、Windows SmartScreen 与 macOS Gatekeeper 风险，并且不得建议用户导入证书或关闭全局系统安全保护。任何构建入口都不得保存签名私钥、Apple ID、应用专用密码或公证令牌。
