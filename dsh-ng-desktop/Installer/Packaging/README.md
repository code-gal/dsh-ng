# 安装包构建入口

安装包始终包含两份不同职责的客户端产物：引导程序以 Avalonia 原生窗口运行安装事务，`payload` 是仅在事务提交后复制到产品安装目录的客户端。引导程序绝不把自身运行目录当作目标安装目录。

- Windows：在 Windows 构建机运行 `windows/Build-Installer.ps1`。脚本发布 self-contained AOT 或 Compatibility 产物，并以 IExpress 生成单文件当前用户安装器；可选证书指纹会用于 Authenticode 签名。
- macOS：在对应架构的 macOS 构建机运行 `macos/build-installer.sh`。脚本为客户端、嵌入的安装引导和 `.pkg` 签名；传入 Keychain 的 notarization profile 后会提交、公证并 stapler。

正式发布前必须在目标 OS 和目标 CPU 上运行生成的安装器，完成 `specs/4_VERIFICATION.md` 中的 M3 与 M5 人在环验收。构建脚本不会保存签名私钥、Apple ID、应用专用密码或 notarization token。
