# RemoteController 使用指南

本指南说明 RemoteController 的标准部署、配对、远程命令、文件传输和自动化测试流程。

## 1. 角色、信任边界与前置条件

- **Controller**：运行 `Rc.Cli.exe`（`rcctl`）并发起操作的机器。
- **受管端**：运行 `RemoteControllerAgent` 和 `RemoteControllerBroker` 的受管理机器。
- **UI Agent**：为可见桌面工作提供服务的逐用户可选组件；要求配置用户已登录。

配对或连接前始终通过独立可信渠道验证受管端证书指纹；不要将发现结果视为认证。首次安装受管端时使用提升权限 PowerShell。配对完成后，可由 Controller 发起普通执行、持久任务、文件传输和测试编排。

## 2. 构建并分发部署包

在构建机或 Controller 的源码检出目录中运行：

```powershell
Set-Location E:\RemoteController
.\scripts\Publish-RemoteController.ps1 -OutputPath .\artifacts\publish
```

将**整个** `E:\RemoteController\artifacts\publish` 目录复制到受管端，例如 `C:\Temp\RemoteController`。该目录包含可执行文件和安装脚本；受管端不需要 `E:\RemoteController\scripts` 源码检出。

在受管端打开提升权限 PowerShell，并从复制的包安装：

```powershell
Set-Location C:\Temp\RemoteController
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-RemoteController.ps1 `
  -UiUser 'CONTOSO\alice'
```

`-UiUser` 为可选项；请使用实际运行 UI 自动化的交互账户。仅测试命令或文件传输时不需要该参数。

验证安装：

```powershell
Get-FileHash 'C:\Program Files\RemoteController\Rc.Agent.exe' -Algorithm SHA256
Get-Service RemoteControllerAgent, RemoteControllerBroker |
  Format-Table Name, Status, StartType
```

两项服务通常应为 `Running` 且启动类型为 `Automatic`。

### 2.1 使用提升 PowerShell 子进程管理 Hyper-V VM

当受管端是本机 Hyper-V VM 时，宿主机的 `Get-VM`、`Copy-VMFile`、虚拟交换机和来宾服务操作通常需要宿主机管理员令牌。不要假定当前 Controller 进程已提升；从普通 PowerShell 启动一个带 UAC 提升的 64 位 PowerShell 子进程，并仅在该子进程中执行需要 Hyper-V 权限的操作。

以下示例先查询 VM 状态；将其中的子进程脚本替换为必要的 `Copy-VMFile` 等 Hyper-V 操作即可。`Sysnative` 确保从 32 位 PowerShell 启动 64 位系统 PowerShell：

```powershell
$vmName = 'RcAcceptanceTarget'

$childCommand = @"
`$ErrorActionPreference = 'Stop'
Import-Module Hyper-V
Get-VM -Name '$vmName' |
  Format-List Name, State, Generation, Path

# 仅在确有部署需要时取消注释；来宾服务接口必须已启用。
# Copy-VMFile -Name '$vmName' `
#   -SourcePath 'E:\RemoteController\artifacts\publish\Rc.UiAgent.exe' `
#   -DestinationPath 'C:\Program Files\RemoteController\Rc.UiAgent.exe' `
#   -FileSource Host -CreateFullPath -Force
"@

$encodedCommand = [Convert]::ToBase64String(
  [Text.Encoding]::Unicode.GetBytes($childCommand))
$powerShell = if (Test-Path "$env:windir\Sysnative\WindowsPowerShell\v1.0\powershell.exe") {
  "$env:windir\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
}
else {
  "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe"
}

$process = Start-Process -FilePath $powerShell -Verb RunAs `
  -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-EncodedCommand', $encodedCommand) `
  -PassThru -Wait

if ($process.ExitCode -ne 0) {
  throw "Elevated Hyper-V operation failed with exit code $($process.ExitCode)."
}
```

在开始复制前，可在**提升的宿主机 PowerShell** 中检查来宾服务接口：

```powershell
Get-VMIntegrationService -VMName 'RcAcceptanceTarget' |
  Format-Table Name, Enabled, PrimaryStatusDescription -AutoSize
```

若“来宾服务接口”未启用，先在**提升的宿主机 PowerShell** 中执行。名称会随 Windows 显示语言本地化，因此先读取实际名称：

```powershell
$guestService = Get-VMIntegrationService -VMName 'RcAcceptanceTarget' |
  Where-Object { $_.Name -in @('Guest Service Interface', '来宾服务接口') }

if ($null -eq $guestService) {
  throw 'Hyper-V Guest Service Interface was not found.'
}

if (-not $guestService.Enabled) {
  Enable-VMIntegrationService -VMName 'RcAcceptanceTarget' `
    -Name $guestService.Name
}
```

这只提升宿主机上的 Hyper-V 管理操作；它不会让受管端 Agent 自动提权。受管端内需要管理员身份的命令仍应使用 `rcctl exec ... --elevated` 或 `rcctl job start ... --elevated`，并受已配对关系与 Broker 策略约束。

## 3. 首次配对：受管端等待，Controller 连接

### 3.1 在受管端本地准备

在受管端控制台启用短期配对代码：

```powershell
& 'C:\Program Files\RemoteController\Rc.Agent.exe' identity
& 'C:\Program Files\RemoteController\Rc.Agent.exe' arm-pairing
```

`arm-pairing` 返回包含以下字段的 JSON：

- `agentDeviceId`
- `oneTimeCode`
- `expiresAtUtc`
- `certificateSha256Fingerprint`

一次性代码仅在短时间内有效（当前为 10 分钟），仅用于一次配对，必须通过可信渠道发送给 Controller。

### 3.2 从 Controller 配对

在 Controller 上使用部署包安装的 CLI 或已发布的 CLI：

```powershell
$rcctl = 'C:\Program Files\RemoteController\Rc.Cli.exe'
$endpoint = '192.168.1.50:43001'
$fingerprint = '<已验证的 SHA-256 证书指纹>'
$pairingCode = '<arm-pairing 提供的一次性代码>'

& $rcctl discover --text
& $rcctl probe $endpoint --fingerprint $fingerprint --text
& $rcctl pair $endpoint --fingerprint $fingerprint --code $pairingCode `
  --name 'TestController' --text
```

`discover` 仅用于定位候选设备。在 Windows 上，它会确保当前 `Rc.Cli.exe` 的专用 `RemoteController Discovery UDP` 入站防火墙规则已在 UDP 43000 启用；因此首次调用必须在提升权限 PowerShell 或命令提示符中运行。它会加入每个活动且支持多播的 IPv4 接口，这对同时拥有 Hyper-V Default Switch 和外部虚拟交换机的主机很重要。该规则只用于未认证发现广播，不用于远程控制。配对前请使用 `probe`，并将其指纹与受管端控制台获得的值对比。

手动交互式配对时省略 `--code`，CLI 会提示输入代码。要删除受管端的 Controller 关系，请在受管端本地执行：

```powershell
& 'C:\Program Files\RemoteController\Rc.Agent.exe' unpair
```

## 4. 运行远程命令

以下 Controller 命令均使用配对章节中的变量。

### 4.1 单次命令

```powershell
& $rcctl exec $endpoint --fingerprint $fingerprint `
  --command '$PSVersionTable.PSVersion.ToString()' --text

& $rcctl exec $endpoint --fingerprint $fingerprint `
  --command 'Get-ChildItem Env:' --shell powershell --text
```

命令需要显式 Shell 时使用 `--shell powershell` 或 `--shell cmd`；需要工作目录时使用 `--workdir <path>`。输出捕获存在上限；长时间测试套件应使用持久任务和日志。

### 4.2 提权命令

特权 Broker 处理经过批准的提权执行，必须显式请求：

```powershell
& $rcctl exec $endpoint --fingerprint $fingerprint `
  --command 'whoami /groups' --shell cmd --elevated --text
```

仅在确实需要时请求提权；配对和证书指纹始终是必需条件。

## 5. 面向长时间测试的持久任务

对安装程序、测试套件、构建和需要持续收集输出的命令使用任务：

```powershell
& $rcctl job start $endpoint --fingerprint $fingerprint `
  --command '& C:\TestTools\Run-Tests.ps1 -Case case-001' --text
```

保存返回的任务 ID，然后监控并收集：

```powershell
& $rcctl job status $endpoint --fingerprint $fingerprint --job '<jobId>' --text
& $rcctl job logs $endpoint --fingerprint $fingerprint --job '<jobId>' --follow --text
& $rcctl job wait $endpoint --fingerprint $fingerprint --job '<jobId>' --text
```

其他常用任务操作：

```powershell
& $rcctl job list $endpoint --fingerprint $fingerprint --text
& $rcctl job input $endpoint --fingerprint $fingerprint --job '<jobId>' --data '输入行'
& $rcctl job close-input $endpoint --fingerprint $fingerprint --job '<jobId>'
& $rcctl job cancel $endpoint --fingerprint $fingerprint --job '<jobId>'
```

### 5.1 多轮输入验收目标

`Rc.InteractiveTestApp.exe` 安装在受管端二进制旁，用于验证持久任务输出收集和多轮标准输入。它从 JSON 状态文件读取成功运行计数、打印该计数并要求其作为第一次输入，随后打印随机六位挑战并要求其作为第二次输入。仅当两次输入均正确时才递增保存的计数并以退出码 `0` 结束；第一次或第二次响应错误时计数保持不变并以非零退出。

可能在稍后请求输入的命令应使用基于 PTY 的任务。程序为 Controller 解析输出稳定标记：`HISTORICAL_RUN_COUNT`、`CHALLENGE_NUMBER`、`RESULT` 和 `NEXT_RUN_COUNT`。

```powershell
& $rcctl job start $endpoint --fingerprint $fingerprint `
  --command '& "C:\Program Files\RemoteController\Rc.InteractiveTestApp.exe" --state-file C:\ProgramData\RemoteController\interactive-acceptance.state.json' `
  --pty --text

# 从 job logs 读取第一个标记，再发送显示的历史计数。
& $rcctl job input $endpoint --fingerprint $fingerprint --job '<jobId>' --data '<historicalRunCount>'

# 从 job logs 读取 CHALLENGE_NUMBER，再发送完全一致的数字。
& $rcctl job input $endpoint --fingerprint $fingerprint --job '<jobId>' --data '<challengeNumber>'
& $rcctl job wait $endpoint --fingerprint $fingerprint --job '<jobId>' --text
```

不要同时对同一状态文件运行多个验收任务；每个并行运行应使用不同的 `--state-file` 路径。

## 6. 文件操作与可恢复传输

文件协议路径相对于受管端 Agent 文件根目录。不要假定它们映射到任意机器路径，例如 `C:\Program Files`。要确定实际默认根目录，请运行：

```powershell
& $rcctl exec $endpoint --fingerprint $fingerprint --command '$env:USERPROFILE' --text
```

测试输入和输出应使用该根目录下的相对路径。

### 6.1 上传测试输入并下载产物

```powershell
& $rcctl copy upload $endpoint 'E:\TestAssets\case-001' `
  --to 'test-input\case-001' --fingerprint $fingerprint

& $rcctl copy download $endpoint 'test-output\case-001' `
  --to 'E:\TestResults\case-001' --fingerprint $fingerprint
```

复制传输可恢复。默认传输配额为 200 MiB；更大数据应单独打包或配置合适的部署限制。

### 6.2 小文件操作

```powershell
& $rcctl fs list $endpoint 'test-input' --recursive --fingerprint $fingerprint
& $rcctl fs stat $endpoint 'test-input\run.json' --fingerprint $fingerprint
& $rcctl fs read $endpoint 'test-input\run.json' --fingerprint $fingerprint
& $rcctl fs write $endpoint 'test-input\run.json' `
  --data '{"case":"case-001"}' --overwrite --fingerprint $fingerprint
```

`fs write` 用于小型原子文件更新，也可使用 `--source <local-file>` 替代 `--data`。

## 7. 自动化测试模式

受管端完成一次安装与配对后，Controller 侧 Agent、CI Runner 或编排脚本可无需交互式受管端操作，按以下顺序执行：

1. 用 `copy upload` 上传测试输入。
2. 用 `job start` 启动持久测试任务。
3. 轮询 `job status`、流式读取 `job logs` 或使用 `job wait`。
4. 用 `copy download` 下载报告、转储、截图和日志。
5. 根据退出状态和收集的产物标记测试结果。

示例 Controller 脚本骨架：

```powershell
$job = & $rcctl job start $endpoint --fingerprint $fingerprint `
  --command '& C:\TestTools\Run-Tests.ps1 -Input test-input\case-001 -Output test-output\case-001' `
  --text

# 从命令输出读取任务 ID，然后等待并收集产物。
& $rcctl job wait $endpoint --fingerprint $fingerprint --job '<jobId>' --text
& $rcctl copy download $endpoint 'test-output\case-001' `
  --to 'E:\TestResults\case-001' --fingerprint $fingerprint
```

RemoteController 刻意不公开未认证的引导监听器，因此不能在配对前安装软件或执行任意命令。受管端必须先在本地安装并启用配对；认证配对完成后，Controller 才可自动化测试生命周期。

## 8. 可选 UI 自动化

UI 命令用于已配置且已登录的桌面用户，先检查可用性：

```powershell
& $rcctl ui status $endpoint --fingerprint $fingerprint --text
& $rcctl ui displays $endpoint --fingerprint $fingerprint --text
& $rcctl ui windows $endpoint --fingerprint $fingerprint --text
```

UI 自动化无法与 Windows 安全桌面交互，包括 UAC 同意提示。特权设置请使用显式提权的持久任务，目标桌面可用后再使用 UI 自动化。

### 8.1 UI 验收测试应用程序

部署包包含 `Rc.UiTestApp.exe`：一个仅用于验证认证 UI 自动化通道的可见、非破坏性 WinForms 应用程序。在受管端配置的交互用户会话中用 `Start-RemoteControllerUiTest.cmd` 启动，它会打开标题为 `RemoteController UI Acceptance Test` 的窗口。

应用程序公开稳定的 UI Automation 名称和可见的 `Verification` 标签。控件覆盖 `invoke`、`set value`、下拉框展开/收起与选择、列表选择、嵌套树展开/收起、键盘输入、剪贴板读回以及鼠标移动/按键/拖动/滚轮事件。每次成功动作都会更新标签，使 Controller 能经由 UI Automation 树断言观察结果，而无需点击不相关桌面应用。

Controller 上在受管端已配对且测试窗口可见后运行包内 `Test-RemoteControllerUi.ps1`：

```powershell
& 'C:\Program Files\RemoteController\Start-RemoteControllerUiTest.cmd'

# 在 Controller 上运行；使用已发布包中的脚本副本。
& '.\Test-RemoteControllerUi.ps1' `
  -Endpoint '192.168.1.50:43001' `
  -Fingerprint '<SHA256>' `
  -Verbose
```

脚本先验证活动 UI 会话，按标题查找可见测试窗口，再通过读取 `Verification` 标签验证每条命令。退出前会恢复原剪贴板文本。仅在可丢弃测试桌面运行：脚本会刻意向测试应用发送键盘、鼠标和剪贴板输入。测试窗口过期或被遮挡时，先关闭并启动新实例再重试。

### 8.2 浏览器自动化

浏览器命令与其他 UI 命令使用相同的认证 UI 通道和活动登录用户。可直接打开 Edge 和 Chrome；`default` 打开用户默认 HTTP(S) 浏览器。DOM 命令仅返回浏览器可访问性文档，不包括地址栏、标签页和其他浏览器框架，类似 Windows-MCP 面向 DOM 的状态模式。

```powershell
# 响应包含浏览器窗口句柄。
& $rcctl ui browser $endpoint --fingerprint $fingerprint launch edge 'https://example.com'

# 导航现有 Edge、Chrome 或 Firefox 窗口。
& $rcctl ui browser $endpoint --fingerprint $fingerprint navigate '<handle>' 'https://example.com/docs'

# 获取页面可访问性树，再通过 ui element 调用或设置值。
& $rcctl ui browser $endpoint --fingerprint $fingerprint dom '<handle>' --depth 10 --limit 2000
```

`dom` 基于可访问性树：现代 Chromium 浏览器通过 UI Automation 公开网页内容；Firefox 是否可用取决于其可访问性配置。浏览器导航对活动桌面用户可见，且不会绕过浏览器提示、站点权限、登录挑战或 Windows 安全桌面。

### 8.3 交互桌面中的后台命令

不要在受管端的交互桌面会话中打开可见的 PowerShell 控制台。该窗口会抢占浏览器或 UI 测试程序的前台焦点，使键盘、鼠标和剪贴板验证出现非确定性结果。

部署、服务管理、诊断和测试准备使用 PowerShell 时，必须在后台/隐藏方式运行（例如使用 `-WindowStyle Hidden`），或在非交互会话中运行。UI 输入必须继续通过 UI Agent 协议发送，不能依赖可见的 Shell 窗口。
