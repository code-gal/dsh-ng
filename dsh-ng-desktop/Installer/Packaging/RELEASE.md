# 发布操作

桌面客户端独立发布，不与插件或仓库内其他项目共用版本标签。维护者决定 SemVer 的 major、minor 或 patch，并在 `CHANGELOG.md` 添加同版本的精炼用户可见更新；随后创建同名 Git 标签和 GitHub Release：`desktop-v<SemVer>`，例如 `desktop-v0.9.1`。已发布标签和附件不得替换，修复必须发布新版本。

## 本机构建

Windows `win-x64`：

```powershell
.\Installer\Packaging\windows\Build-Installer.ps1 -Mode Aot -RuntimeIdentifier win-x64 -Version v0.9.1
.\Installer\Packaging\windows\Build-Installer.ps1 -Mode DotNet -RuntimeIdentifier win-x64 -Version v0.9.1
```

Apple Silicon macOS `osx-arm64`：

```bash
bash Installer/Packaging/macos/build-installer.sh --version v0.9.1
```

输出为两个 Windows EXE、一个 macOS PKG 和各自的 `.sha256` 文件，全部位于 `artifacts/installer/`。Windows `-dotnet.exe` 需要 .NET 10 Desktop Runtime；所有 AOT 包不需要 .NET Runtime。两个平台都需要系统已有的 Node.js 与 npx。

## 自动发布与验收

1. 在 `CHANGELOG.md` 添加与目标 SemVer 匹配的版本小节，内容只写用户可见的 Added、Changed、Fixed 或 Removed 更新。
2. 在真实 Windows `win-x64` 和 macOS `osx-arm64` 目标机器分别完成安装、停止/回滚、托盘、自启动、卸载、残留、SHA-256 和安全提示验收。
3. 创建并推送 `desktop-v0.9.1` 标签。
4. 工作流先校验标签与 Changelog；然后在 Windows Runner 构建 AOT/.NET 双安装器、在 macOS ARM64 Runner 构建 AOT PKG，并各自校验 SHA-256。只有两项构建都成功，发布任务才会创建预发布 Release、上传六个附件，并用 Changelog、提交数量和比较链接生成正文。
5. 检查 Release 明确标示“未签名社区预览”：Windows 提示 SmartScreen 风险且不导入根证书；macOS 提示未签名、未公证的 Gatekeeper 风险，仅允许单次人工打开，不关闭 Gatekeeper、SIP 或其他全局安全保护。

GitHub Actions 不持有签名密钥或 Apple 凭据，只负责从已验收标签重现未签名产物并发布；它不能代替目标机器验收。
