# 正式发布操作

桌面客户端独立发布，不与插件或仓库内其他项目共用版本标签。每个版本使用 Git 标签和 GitHub Release 名称 `desktop-v<SemVer>`，例如 `desktop-v0.9.1`。已发布标签和附件不得替换；修复时发布新的版本。

## Windows

Windows 旧 IExpress 构建入口已经移除。M5.9 的 Native AOT SetupHost 与新构建脚本完成并通过 V8.9 至 V8.11 前，不存在受支持的 Windows 正式发布命令；历史 `artifacts/installer/*.exe` 均为无效本地试验产物，不得上传 Release。

完成后的正式输出必须为 `artifacts/installer/DSH-Desktop-Setup-v0.9.1-win-x64-aot.exe`、`DSH-Desktop-Setup-v0.9.1-win-x64-dotnet.exe` 及各自 `.sha256` 文件。两者都使用无控制台 Native AOT SetupHost，文件名中的构建形态描述其内嵌和安装的客户端负载；`-dotnet.exe` 要求匹配的 .NET Desktop Runtime。正式 Windows 产物必须在完成单文件封装后进行 Authenticode 签名。

## macOS

在对应 macOS 架构机器、已配置 Developer ID 和 notary profile 的 Keychain 中运行：

```bash
./Installer/Packaging/macos/build-installer.sh \
  --rid osx-arm64 \
  --version v0.9.1 \
  --mode Aot \
  --signing-identity 'Developer ID Application: Example' \
  --notary-profile dsh-desktop-notary
```

分别以 `--mode Aot` 和 `--mode DotNet` 构建两种包；输出为 `DSH-Desktop-Setup-v0.9.1-osx-arm64-aot.pkg`、`DSH-Desktop-Setup-v0.9.1-osx-arm64-dotnet.pkg` 及各自 `.sha256` 文件。`.NET 依赖`包要求用户先安装匹配的 .NET Runtime。

## 上传与验收

1. 在每个目标系统通过安装器完成安装、停止/回滚、托盘、自启动、卸载和残留检查。
2. 创建并推送 `desktop-v0.9.1` 标签。
3. 在 GitHub 手动创建同名 Release，上传对应安装器和 `.sha256` 文件，并写明 Node.js 前置条件、签名/公证状态及变更说明。
4. 安装器、SHA-256、签名文件和 `artifacts/installer/` 均不得提交到 Git。

GitHub Actions 目前不承担本项目发版；未来可增加不持有签名密钥的项目级构建验证，但不能代替以上目标机器验收。
