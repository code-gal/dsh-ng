# dsh-ng-desktop 开发、打包与发布指南

产品行为和验收要求以 `specs/` 为准；本文说明如何执行当前的开发和发行流程。

## 当前发行模型

- GitHub Releases 是唯一下载渠道，标签格式固定为 `desktop-v<SemVer>`。
- Windows `win-x64` 发布 Native AOT 与 .NET 依赖两个单 EXE 安装器；macOS `osx-arm64` 只发布 Native AOT `pkg`。
- Windows ARM64、macOS Intel 和其他架构不进入 CI、发行或验收矩阵。
- 两个平台当前都是未签名社区预览。macOS `pkg` 不签名、不公证；工作流不持有签名私钥、Apple ID 或公证凭据。
- `artifacts/installer/` 是本地发行输出，已由 Git 忽略；安装包和校验文件不得提交。

## 开发环境

### Windows x64

安装 .NET 10 SDK，在 `dsh-ng-desktop` 目录执行：

```powershell
dotnet restore DshNgDesktop.csproj --configfile NuGet.Config
dotnet build DshNgDesktop.csproj --configfile NuGet.Config
dotnet run -- --development
```

### macOS Apple Silicon

安装 .NET 10 SDK 和 Xcode Command Line Tools，在项目目录执行：

```bash
dotnet restore DshNgDesktop.csproj --runtime osx-arm64 --configfile NuGet.Config
dotnet build DshNgDesktop.csproj --runtime osx-arm64 --configfile NuGet.Config
dotnet run -- --development
```

`--development` 是源码运行的显式入口。无参数的发布应用只代表已安装客户端，不会根据当前目录或缺失清单隐式启动安装事务。

## 本地打包

Windows `win-x64`：

```powershell
.\Installer\Packaging\windows\Build-Installer.ps1 -Mode Aot -RuntimeIdentifier win-x64 -Version v0.9.1
.\Installer\Packaging\windows\Build-Installer.ps1 -Mode DotNet -RuntimeIdentifier win-x64 -Version v0.9.1
```

Apple Silicon macOS `osx-arm64`：

```bash
bash Installer/Packaging/macos/build-installer.sh --version v0.9.1
```

macOS 脚本固定为 Native AOT 与 `osx-arm64`，不接受签名身份或公证参数。它生成：

```text
artifacts/installer/DSH-Desktop-Setup-v0.9.1-osx-arm64-aot.pkg
artifacts/installer/DSH-Desktop-Setup-v0.9.1-osx-arm64-aot.pkg.sha256
```

构建同时把与本次版本精确匹配的 dSYM 保存到 `artifacts/installer/.internal/dsym/<SemVer>/`，不装入用户 PKG，也不上传公开 Release。Apple Silicon 构建完成后必须在同一 arm64 主机执行包结构与 AOT 门禁：

```bash
bash Installer/Packaging/macos/verify-macos-installer.sh \
  --package artifacts/installer/DSH-Desktop-Setup-v0.9.1-osx-arm64-aot.pkg
dotnet test ReleaseTests/DshNgDesktop.ReleaseTests.csproj -r osx-arm64
```

门禁会拒绝非 Darwin/非 arm64/macOS 14 以下宿主，并覆盖 PKG 中的 arm64 可执行文件、Unix 执行位、bundle 元数据、postinstall 同步退出契约和真实 Native AOT `--doctor` 执行；受版本控制的 ReleaseTests 补充维护锁、Node/npx 解析、私有工作目录和 spawn-time 进程组路径。

所有下载物必须在目标平台校验 SHA-256。Windows 使用 `Get-FileHash`，macOS 使用 `shasum -a 256`；散列值必须与同名 `.sha256` 文件一致。

## 发布与人工验收

1. 在 [`CHANGELOG.md`](../CHANGELOG.md) 的 `Unreleased` 下整理用户可见更新，决定 major、minor 或 patch，并创建同版本小节；提交该变更。
2. 将发布提交推送到承载 GitHub Actions 的 GitHub 远程分支。须显式执行 `git push github <branch>`；用 `git log github/<branch>..HEAD` 确认没有待推送的发布提交。
3. 在真实 Windows `win-x64` 与 macOS `osx-arm64` 上分别从最终安装包完成安装、首次 DSH 供应、托盘、自启动、退出、卸载、残留、SHA-256 和安全提示验收。
4. 在该发布提交上创建并推送标签，例如 `git tag desktop-v0.9.11 <release-commit>` 和 `git push github refs/tags/desktop-v0.9.11`。
5. 查看 `DSH Desktop Release` 工作流和生成的 GitHub Release。失败只能通过新提交与新版本修复，不能替换已发布标签或附件。

### 标签误推恢复（仅限尚未创建 GitHub Release）

若标签先于 Changelog 推送，`prepare` 作业会失败。补齐并提交 Changelog 后，先推送发布提交到 GitHub；然后用 `git ls-remote --tags github refs/tags/desktop-v0.9.11` 确认远端标签已经删除。远端删除不会删除本地标签，所以本地再次执行 `git tag desktop-v0.9.11` 会报“already exists”。用 `git show --no-patch desktop-v0.9.11` 检查现有标签；必要时以 `git tag -f desktop-v0.9.11 <release-commit>` 将其移到正确提交，再以 `git push github refs/tags/desktop-v0.9.11` 重新触发工作流。若 Release 或任一附件已创建，禁止删除、移动或强推同名标签，必须使用新版本。

Windows SmartScreen 与 macOS Gatekeeper 的警告或首次阻止属于未签名社区预览的预期行为。只应在来源和 SHA-256 已确认后按系统提供的单次人工打开流程继续；不要导入根证书或发布者证书，也不要关闭 Gatekeeper、SIP 或其他全局安全保护。

## CI/CD 边界

`.github/workflows/desktop-release.yml` 的作业顺序为：

1. `prepare` 校验标签与 Changelog 版本条目。
2. `build-windows` 在 Windows Runner 构建 x64 AOT/.NET 双安装器。
3. `build-macos` 在 Apple Silicon macOS Runner 构建 ARM64 AOT `pkg`。
4. `publish` 仅在两个构建成功后下载六个附件、复核 SHA-256，并使用 Changelog、提交数量和比较链接创建未签名预发布 Release。

CI 不自动推断或修改版本号，不自动生成详细提交日志，也不能替代推送标签前的目标机器验收。若未来启用受信任的 Windows 签名或 macOS 签名/公证，必须先更新 Spec，再使用受控签名环境或经过审批的独立任务。
