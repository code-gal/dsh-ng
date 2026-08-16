# 安装包构建边界

安装包中的角色严格分离：SetupHost 只运输一份完整客户端发布负载；临时负载中的 `DshNgDesktop` 以显式安装会话显示唯一的 Avalonia 原生窗口并运行安装事务；提交后复制到产品目录的仍是同一份客户端负载。SetupHost 不显示第二套安装向导，也不执行部署、DSH 供应、系统注册或卸载业务。

- Windows：旧 IExpress、CMD 和 PowerShell 包装入口已经移除。`windows/Build-Installer.ps1` 发布客户端负载并构建 `DshDesktop.SetupHost` Native AOT `WinExe`，生成唯一下载 EXE。SetupHost 负责内嵌负载校验、安全解压、直接子进程同步等待、退出码传递和 staging 清理；它不显示安装向导或执行安装业务。
- macOS：不属于当前项目的打包、发布或验证范围。仓库保留的平台兼容性代码与实验性构建资料仅供外部开发者自行构建验证，不能生成或上传项目发行包。

所有安装器和 SHA-256 文件输出到 `artifacts/installer/`，该目录已由 Git 忽略，只能作为 GitHub Release 附件上传。`desktop-v*` 标签触发 GitHub Actions 自动重建并上传 AOT/.NET 双安装器及校验文件，无需维护者手工上传。无证书构建的 Windows EXE 只作为“未签名社区预览”附件发布，Release 必须展示 SmartScreen 风险、SHA-256 校验步骤和“不导入根证书”的说明。macOS 不生成项目发行安装包；外部开发者如需验证，可自行构建并承担验证责任。

正式发布前必须在目标 OS 和目标 CPU 上运行生成的安装器，完成 `specs/4_VERIFICATION.md` 中的 M3 与 M5 人在环验收。任何构建入口都不得保存签名私钥、Apple ID、应用专用密码或 notarization token。
