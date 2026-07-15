# RemoteController

RemoteController 是面向一台受信任控制端与一台受管端的 Windows 10/11 局域网远程控制工具。它提供经身份验证的命令执行、可持续交互任务、可断点续传的文件传输、显式提权执行，以及登录用户会话中的可选桌面自动化。

## 构建和测试

```powershell
dotnet restore Rc.RemoteController.sln -p:NuGetAudit=false
dotnet build Rc.RemoteController.sln --no-restore -warnaserror
dotnet test Rc.RemoteController.sln --no-build --no-restore -v minimal
```

使用以下命令创建自包含的 Windows x64 安装包内容：

```powershell
.\scripts\Publish-RemoteController.ps1
```

## 安装和配对

请在受管端以提升权限运行 PowerShell。`-UiUser` 用于指定允许运行 UI 会话代理的交互式账户。

```powershell
.\scripts\Install-RemoteController.ps1 -SourcePath .\artifacts\publish -UiUser 'CONTOSO\alice'
```

Agent 启动时会公布其证书 SHA-256 指纹。在控制端发现或探测受管端，然后使用受管端本地显示的一次性代码完成一次配对：

```powershell
rcctl discover --text
rcctl pair 192.168.1.50:43001 --fingerprint <SHA256> --name MyController --text
```

默认情况下，每条 CLI 命令都会输出一个 JSON 结果信封。添加 `--text` 可获得面向人工阅读的输出。参见[运维说明](docs/operations.md)、[协议](docs/protocol.md)和[安全模型](docs/security.md)。

## 重要限制

- 局域网发现未经身份验证；务必固定受管端提供的指纹。
- 在本地执行 `rc-agent unpair` 前，一个受管端只能接受一个已配对控制端。
- GUI 自动化仅在配置的活动登录会话中工作；有意不支持 UAC 安全桌面。
- 重启绝不会恢复或重放任意命令。
- 在生产发布前，必须完成真实双机/虚拟机和提权安装验收测试。
