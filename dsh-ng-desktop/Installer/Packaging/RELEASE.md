# 发布操作

桌面客户端独立发布，不与插件或仓库内其他项目共用版本标签。每个版本使用 Git 标签和 GitHub Release 名称 `desktop-v<SemVer>`，例如 `desktop-v0.9.1`。已发布标签和附件不得替换；修复时发布新的版本。

## Windows

Windows 旧 IExpress 构建入口已经移除。当前采用“未签名社区预览”发行策略，使用 Native AOT SetupHost 构建单文件安装器时省略证书指纹：

```powershell
.\Installer\Packaging\windows\Build-Installer.ps1 -Mode Aot -RuntimeIdentifier win-x64 -Version v0.9.1
.\Installer\Packaging\windows\Build-Installer.ps1 -Mode DotNet -RuntimeIdentifier win-x64 -Version v0.9.1
```

构建脚本先创建客户端发布负载与 SHA-256 清单，再将二者内嵌到无控制台 Native AOT SetupHost。无证书输出可上传 GitHub Release，但标题和首段必须标记为“未签名社区预览”，说明 Windows SmartScreen 预期会出现拦截，并给出 SHA-256 校验步骤。绝不要求用户导入根证书或发布者证书。未来取得有效证书后，才在同一脚本中增加 `-CertificateThumbprint <thumbprint>`。

输出为 `artifacts/installer/DSH-Desktop-Setup-v0.9.1-win-x64-aot.exe`、`DSH-Desktop-Setup-v0.9.1-win-x64-dotnet.exe` 及各自 `.sha256` 文件。两者都使用无控制台 Native AOT SetupHost，文件名中的构建形态描述其内嵌和安装的客户端负载；`-dotnet.exe` 要求匹配的 .NET Desktop Runtime。若将来签名，必须在最终单文件封装后执行 Authenticode 签名。

## macOS

macOS 不属于当前项目的打包、发布或验证范围。仓库保留的兼容性代码仅供外部开发者自行从源码构建验证；不得生成或上传项目 macOS Release 附件。

## 上传与验收

1. 在 Windows `win-x64` 目标系统通过安装器完成安装、停止/回滚、托盘、自启动、卸载和残留检查。macOS 不属于当前项目验收。
2. 创建并推送 `desktop-v0.9.1` 标签。
3. 在 GitHub 手动创建同名 Release，上传对应安装器和 `.sha256` 文件，并写明 Node.js 前置条件、签名状态和变更说明。未签名 Windows Release 必须在标题和首段标记“未签名社区预览”，明确 SmartScreen 风险、SHA-256 校验步骤和“不导入根证书”。
4. 安装器、SHA-256、签名文件和 `artifacts/installer/` 均不得提交到 Git。

GitHub Actions 目前不承担本项目发版；未来可增加不持有签名密钥的项目级构建验证，但不能代替以上目标机器验收。
