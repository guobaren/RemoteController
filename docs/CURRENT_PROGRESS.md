# RemoteController 当前进度

更新时间：2026-07-10

## 目标

面向 Windows 10/11 局域网的单控制端远程控制工具。控制端将通过命令行让被控端执行命令、管理长时间任务、读取日志并向交互任务写入标准输入；后续再扩展文件传输、GUI 自动化和特权执行。

## 已完成并已提交

| 提交 | 能力 |
| --- | --- |
| 3b2c3ea | 持久化 Agent 状态、日志配额与受保护密钥存储基础。 |
| 0316064 | 单控制端配对领域模型与本地配对协调基础。 |
| 534bddf | UDP 组播局域网设备发现及 rcctl discover。 |
| 5539f6c | 可接收标准输入、输出分段的持久交互式 Rc.TaskHost。 |
| 7090feb | 持久任务契约与 TaskHost 恢复基础。 |

## 当前未提交改动

首次启动时，Agent 数据目录现在由安全初始化器创建：目录 ACL 仅允许当前 Agent 账户、SYSTEM 和 Administrators 写入。已存在但 ACL 不安全的目录仍会被拒绝，不会静默放宽权限。

同时修复了两项问题：

- ACL 位掩码此前会将未知主体的只读权限误判成写权限；
- AgentStateStore 曾在安全检查前按继承 ACL 创建目录，导致首次启动必然失败。

## 已验证

- dotnet test tests\Rc.Agent.Tests\Rc.Agent.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~AgentDataDirectoryAclValidatorTests"：6 项通过。
- dotnet build Rc.RemoteController.sln -p:NuGetAudit=false -v minimal：0 warnings / 0 errors。
- 真实本机验证：Agent 在全新 ProgramData 子目录创建安全 ACL 后启动，控制端通过 rcctl discover --timeout-ms 4000 --text 成功收到 UDP 广播。
- 本地 TaskHost 验证：交互式 PowerShell 测试脚本可输出日志、接收标准输入并正常退出。

## 当前可使用的命令

~~~powershell
# 被控端：持续运行并广播自身
.\src\Rc.Agent\bin\Debug\net8.0-windows\Rc.Agent.exe

# 控制端：发现同一局域网的 Agent
.\src\Rc.Cli\bin\Debug\net8.0\Rc.Cli.exe discover --timeout-ms 4000 --text
~~~

可通过 RC_AGENT_DATA_ROOT 指定状态目录；默认是 C:\ProgramData\RemoteController。新目录会被安全初始化；已有 ACL 不安全的目录会被拒绝。

## 尚未接通的用户功能

发现结果目前只是设备元数据，尚未建立网络控制会话。以下命令仍未实现：

- rcctl pair：TLS 连接、一次性配对码、唯一控制端证书固定；
- rcctl job start/status/logs/input/wait/cancel：通过网络控制 TaskHost；
- fs 与 copy：远程文件读写、分块传输与断点续传；
- ui：仅登录会话可用的 GUI 自动化；
- 特权 Broker、服务安装、开机自启、防火墙配置与卸载/修复。

## 下一里程碑

实现 Agent 的 TLS 控制监听服务和 rcctl pair：控制端发现或手工输入 IP:端口 后连接 Agent，被控端本机显示一次性配对码，配对成功后仅保存一个控制端证书。随后接入 job start、日志流、任务状态、stdin 与取消任务，形成最小可用远程命令执行链路。
