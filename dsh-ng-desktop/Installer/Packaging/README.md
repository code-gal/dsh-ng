# 安装包构建边界

安装包中的角色严格分离：SetupHost 只运输一份完整客户端发布负载；临时负载中的 `DshNgDesktop` 以显式安装会话显示唯一的 Avalonia 原生窗口并运行安装事务；提交后复制到产品目录的仍是同一份客户端负载。SetupHost 不显示第二套安装向导，也不执行部署、DSH 供应、系统注册或卸载业务。

- Windows：旧 IExpress、CMD 和 PowerShell 包装入口已经移除。后续由 `DshDesktop.SetupHost` Native AOT `WinExe` 生成唯一下载 EXE，负责内嵌负载校验、安全解压、直接子进程同步等待、退出码传递和 staging 清理。M5.9 完成前没有受支持的 Windows 安装包构建命令，不得把历史 `artifacts/installer/*.exe` 作为发行候选。
- macOS：在对应架构的 macOS 构建机运行 `macos/build-installer.sh`。`--mode Aot` 和 `--mode DotNet` 分别生成 `-aot.pkg` 与需要匹配 .NET Runtime 的 `-dotnet.pkg`。脚本为客户端、嵌入的安装引导和 `.pkg` 签名；传入 Keychain 的 notarization profile 后会提交、公证并 stapler。

所有安装器和 SHA-256 文件输出到 `artifacts/installer/`，该目录已由 Git 忽略，只能作为 GitHub Release 附件上传。

正式发布前必须在目标 OS 和目标 CPU 上运行生成的安装器，完成 `specs/4_VERIFICATION.md` 中的 M3 与 M5 人在环验收。任何构建入口都不得保存签名私钥、Apple ID、应用专用密码或 notarization token。
