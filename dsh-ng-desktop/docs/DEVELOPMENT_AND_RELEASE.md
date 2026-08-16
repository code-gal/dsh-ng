# dsh-ng-desktop 开发、打包与发布指南

本文面向开发者和发行维护者，说明当前可执行的 Windows 开发、打包、签名、GitHub Release 与 CI/CD 边界，以及 macOS 源代码兼容性的范围。产品行为和验收要求以 `specs/` 为准；本文不替代 Spec。

## 1. 当前发行模型

- 正式下载渠道只有 GitHub Releases，版本标签格式为 `desktop-v<SemVer>`，例如 `desktop-v0.9.1`。
- Windows `win-x64` 提供 Native AOT 和 .NET 依赖两种单 EXE 安装器；两者都由 Native AOT `DshDesktop.SetupHost` 承载。当前可在 GitHub 发布明确标记的“未签名社区预览”，并必须提供 SHA-256 与 SmartScreen 风险说明。
- macOS 仅保留源码级兼容性实现，不属于当前项目的打包、发布或验证范围；有兴趣的开发者可自行构建和验证。
- `artifacts/installer/` 是本地发行输出，已被 Git 忽略；安装器、校验文件和签名副产物不得提交。
- 当前没有自动发布工作流。真实安装验收和 GitHub Release 上传均由维护者手动完成；将来取得证书后才增加签名与公证步骤。

## 2. 开发环境

### Windows

安装 .NET 10 SDK。当前未签名社区预览不需要证书或 `signtool.exe`；将来启用 Windows Authenticode 签名时，才额外需要 Windows SDK 中的 `signtool.exe` 与可访问私钥的代码签名证书。Node.js 不是编译前置条件，但最终安装器会在目标机器检查 `node` 与 `npx`。

在 `dsh-ng-desktop` 目录执行：

```powershell
dotnet restore DshNgDesktop.csproj --configfile NuGet.Config
dotnet build DshNgDesktop.csproj --configfile NuGet.Config
dotnet run -- --development
```

`--development` 是源码运行的显式入口。无参数的 `DshNgDesktop.exe` 只代表已安装客户端；它不会根据当前目录或缺失清单隐式启动安装事务。

SetupHost 可独立编译：

```powershell
dotnet build DshDesktop.SetupHost\DshDesktop.SetupHost.csproj --configfile NuGet.Config
```

### macOS

macOS 不设项目管理的构建 RID、安装器或验收流程。外部开发者可自行在 macOS 安装 .NET SDK 后从源码构建和运行；该结果不构成项目发行准入。

## 3. 可选的 Windows 代码签名证书与“证书指纹”

Windows Authenticode 签名证书包含公开证书和受保护的私钥。证书指纹（thumbprint）是该证书的 SHA-1 唯一标识，用于让 `signtool.exe` 选择要使用的证书；它不是私钥、PFX 密码或时间戳凭据，可以在发布命令中出现。

如果未来启用签名发布，证书应由受信任的代码签名 CA 签发，并且必须：

- 包含可用私钥和 Code Signing 用途；
- 安装在运行构建脚本的用户或计算机证书库中，且该用户有权使用私钥；
- 不将 PFX、私钥、PFX 密码或硬件令牌 PIN 放入 Git、脚本参数、日志或 CI 变量明文。

在当前用户证书库列出可用于代码签名的证书及其指纹：

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
  Select-Object Subject, Thumbprint, NotAfter, HasPrivateKey
```

将输出中的 `Thumbprint` 原样填入 `-CertificateThumbprint`。若从证书管理器复制时带有空格，应先去掉空格。测试自签名证书只能用于本机验证，Windows 会将其显示为不受信任，不能替代正式签名。

若上述命令没有任何输出，表示当前用户没有可用的代码签名证书。可额外检查计算机证书库：

```powershell
Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert |
  Select-Object Subject, Thumbprint, NotAfter, HasPrivateKey
```

本地功能验证无需证书，直接省略 `-CertificateThumbprint` 即可；构建脚本会明确标记产物为未签名。若只需验证签名流程，可创建本机自签名测试证书：

```powershell
New-SelfSignedCertificate -Type CodeSigningCert `
  -Subject 'CN=DSH Desktop Local Test' `
  -CertStoreLocation Cert:\CurrentUser\My
```

该证书只能用于本机测试。当前公开 Windows 社区预览采用未签名包和 SHA-256 校验，不使用自签名证书；未来如启用受信任签名，应从受信任 CA 获取代码签名证书并安装其私钥。

## 4. Windows 打包

从 `dsh-ng-desktop` 目录执行。`Aot` 产物无需目标机安装 .NET Runtime；`DotNet` 产物要求目标机预装匹配的 .NET 10 Desktop Runtime。

```powershell
.\Installer\Packaging\windows\Build-Installer.ps1 `
  -Mode Aot `
  -RuntimeIdentifier win-x64 `
  -Version v0.9.1

.\Installer\Packaging\windows\Build-Installer.ps1 `
  -Mode DotNet `
  -RuntimeIdentifier win-x64 `
  -Version v0.9.1
```

项目打包与 Native AOT 验证固定为本机 `win-x64`。省略 `-CertificateThumbprint` 会生成未签名产物；它可上传 GitHub，但只能作为明确标记的“未签名社区预览”，并在 Release 首段说明 SmartScreen 风险、SHA-256 校验步骤和“不导入根证书”。

脚本执行顺序是：发布客户端完整负载、生成每个文件的长度/SHA-256 清单、压缩并内嵌到 Native AOT SetupHost、如提供证书则对最终单 EXE 签名、再生成最终 EXE 的 `.sha256`。不会使用 IExpress、CMD、PowerShell 或第二套安装界面作为安装器运行链的一部分。

输出示例：

```text
artifacts/installer/DSH-Desktop-Setup-v0.9.1-win-x64-aot.exe
artifacts/installer/DSH-Desktop-Setup-v0.9.1-win-x64-aot.exe.sha256
artifacts/installer/DSH-Desktop-Setup-v0.9.1-win-x64-dotnet.exe
artifacts/installer/DSH-Desktop-Setup-v0.9.1-win-x64-dotnet.exe.sha256
```

构建完成后检查校验值；将来使用证书时再检查签名：

```powershell
Get-AuthenticodeSignature .\artifacts\installer\DSH-Desktop-Setup-v0.9.1-win-x64-aot.exe
Get-FileHash .\artifacts\installer\DSH-Desktop-Setup-v0.9.1-win-x64-aot.exe -Algorithm SHA256
Get-Content .\artifacts\installer\DSH-Desktop-Setup-v0.9.1-win-x64-aot.exe.sha256
```

后两条命令显示的 SHA-256 值必须一致；若提供了证书，`Get-AuthenticodeSignature` 的 `Status` 应为 `Valid`。

## 5. macOS 兼容性

macOS 不生成安装包、不发布 GitHub Release 附件，也不纳入项目验收或 CI。仓库中的 macOS 平台代码仅供感兴趣的开发者自行从源码构建、运行和验证。

## 6. 发布与人工验收

每次正式版本按以下顺序进行：

1. 确认版本号未发布，运行源码构建和 Windows Native AOT 打包。
2. 在目标 Windows 系统从最终安装器完成安装、首次 DSH 供应、托盘、自启动、退出、卸载和残留检查。
3. 确认安装器没有终端窗口或第二套向导；.NET 依赖包在缺少 Runtime 时应由 SetupHost 在创建产品目录前显示前置条件错误。
4. 检查 SHA-256。若未来提供 Windows 证书，再检查 Authenticode 状态。
5. 创建并推送 `desktop-v<SemVer>` 标签，例如 `desktop-v0.9.1`。
6. 在 GitHub 手动创建同名 Release，上传 Windows 安装器与 `.sha256`，在标题和首段标记“未签名社区预览”，注明 Node.js/.NET Runtime 前置条件、SmartScreen 风险、SHA-256 校验步骤和“绝不导入根证书”。

已发布标签和同名附件不得替换；修复使用新的版本号。不要上传 PDB、发布缓存、未标记的未签名包或任何包含私钥的文件。

## 7. CI/CD 当前状态与后续边界

当前仓库没有自动 CI/CD 或自动 GitHub Release 工作流。这是有意的：未来的发布签名密钥不应交给普通 CI 运行器。

未来可以添加不持有签名私钥的 CI 门禁，建议仅在 `dsh-ng-desktop/**` 变更或 `desktop-v*` 标签上触发，并至少执行：

- Windows `win-x64` 的普通构建；
- Windows `win-x64` Native AOT 发布验证；
- 不含签名凭据的静态检查与测试；
- 仅上传短期保留的未签名测试工件，不创建正式 Release。

最终 SHA-256、Windows `win-x64` 人在环安装/卸载验收和 GitHub Release 上传仍应留在受控的目标系统上手动完成；未来启用的 Windows 正式签名也应留在受控目标系统。

## 8. 常见问题

- NuGet 读取用户级配置失败：始终在仓库项目目录使用 `--configfile NuGet.Config`。
- 出现编译 DLL 锁定：关闭 IDE 中相关调试进程后执行 `dotnet build-server shutdown`，再重新打包。
- 未找到证书：确认 `Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert` 能看到目标指纹，且 `HasPrivateKey` 为 `True`。
- 未签名社区预览被 SmartScreen 拦截：这是预期行为。只应引导用户从 GitHub Release 下载、核对 SHA-256 后自行选择是否运行；不要让用户导入根证书。将来启用签名后仍可能需要积累信誉。
