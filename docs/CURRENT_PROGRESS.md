# RemoteController 当前进度

更新时间：2026-07-12

## 目标

面向 Windows 10/11 局域网的单控制端远程控制工具。控制端最终将能让 AI agent 在另一台 Windows 电脑上执行命令、管理并发且可交互的长任务、读取日志、写入标准输入，并传输文件；GUI 自动化、提权 Broker 与服务化将在后续接入。

## 已提交能力

| 提交 | 能力 |
| --- | --- |
| `3b2c3ea` | Agent 状态持久化、日志配额与受保护秘密存储 |
| `0316064` | 单控制端配对领域模型与本地配对协调器 |
| `534bddf` | UDP 组播局域网发现与 `rcctl discover` |
| `5539f6c` | 支持标准输入、标准输出分段的持久化交互任务 `Rc.TaskHost` |
| `7090feb` | 任务契约与 TaskHost 恢复基础 |
| `c77d92f` | Agent 数据目录安全初始化与 ACL 验证 |
| `d80e4c3` | 已签名的单次远程命令执行 |
| `6be9ca6` | 复用认证会话、长期任务调度/恢复/控制，以及可恢复文件和目录传输 |

## 已完成的控制能力

已完成最小配对与单次命令执行控制面：

- Agent 在 TCP 上提供 TLS 单请求 JSON 控制端点：`hello`、`pair_start`、`pair_round1`、`pair_round2`、`pair_complete`。
- `rcctl probe <IP:port> --fingerprint <SHA256>` 可读取公开设备 ID、TLS 指纹和“是否已配对”状态。
- `rcctl pair <IP:port> --fingerprint <SHA256>` 会在 Agent 本机控制台显示一次性配对码；控制端输入该码后，使用三轮 J-PAKE 和控制端 ECDSA 证书确认完成配对。
- `rcctl exec <IP:port> --fingerprint <SHA256> --command <命令> [--shell powershell|cmd] [--workdir <路径>] [--text]` 已可执行一条当前用户命令；控制端签名绑定 Agent 设备 ID、控制端 ID 与完整执行请求，Agent 验签后串行执行并返回退出码、stdout 与 stderr。
- 控制端身份保存为 P-256 ECDSA 证书；PFX 用当前用户 DPAPI 加密后写入 `%LOCALAPPDATA%\RemoteController\controller-identity.dpapi`。可用 `RC_CONTROLLER_DATA_ROOT` 替换目录，用于隔离测试。
- TLS 采用 Agent 公布的 SHA-256 DER 证书指纹固定信任，不接受系统信任链或名称匹配替代。
- Agent 只允许一个已配对控制端。完成配对后，新的 `pair_start` 将被拒绝，直到未来增加显式取消配对命令。
- Agent 数据根目录默认是 `C:\ProgramData\RemoteController`，也可通过 `RC_AGENT_DATA_ROOT` 指定其他目录。新目录会被设置为仅当前 Agent 账户、SYSTEM 和 Administrators 可写；已有目录 ACL 不安全时会拒绝启动。

## 当前可使用的命令

```powershell
# 被控端：持续运行、发布 UDP 发现信息，并监听 TLS 控制端口。
# 默认端口为 43001；也可设置 RC_AGENT_TCP_PORT。
.\src\Rc.Agent\bin\Debug\net8.0-windows\Rc.Agent.exe

# 控制端：发现局域网 Agent。
.\src\Rc.Cli\bin\Debug\net8.0-windows\Rc.Cli.exe discover --timeout-ms 4000 --text

# 控制端：手动地址 + 发现/现场获得的 TLS 指纹，探测公开状态。
.\src\Rc.Cli\bin\Debug\net8.0-windows\Rc.Cli.exe probe 192.168.1.50:43001 --fingerprint <64位SHA256指纹> --text

# 控制端：发起配对。Agent 控制台会出现一次性码；将其输入当前 CLI。
.\src\Rc.Cli\bin\Debug\net8.0-windows\Rc.Cli.exe pair 192.168.1.50:43001 --fingerprint <64位SHA256指纹> --name MyController --text
# 控制端：已配对后执行单条命令。--text 会把两路输出分别透传到本地 stdout/stderr。
.\src\Rc.Cli\bin\Debug\net8.0-windows\Rc.Cli.exe exec 192.168.1.50:43001 --fingerprint <64位SHA256指纹> --command "python --version" --text
```

配对命令开始后，Agent 只会在本机控制台显示一次性码；该码不会写入 UDP 广播或 TLS 响应。`IP:端口 + TLS 指纹 + 一次性码` 因而可作为发现广播不可用时的备用流程。

## 2026-07-11 持久化任务的启动与状态查询

已在既有的 `Rc.TaskHost` 与 SQLite 任务快照基础上接入最小长期任务控制面：

- `rcctl job start <IP:端口> --fingerprint <SHA256> --command <命令> [--shell powershell|cmd] [--workdir <路径>] [--text]`：以当前 Agent 用户身份异步启动任务，命令立即返回任务 ID、PID 和初始状态，不等待远端进程结束。
- `rcctl job status <IP:端口> --fingerprint <SHA256> --job <任务ID> [--text]`：运行中任务经 TaskHost 的 named pipe 刷新实时状态；完成后的任务从 SQLite 读取持久化快照。`--text` 显示 PID、退出码、stdout/stderr 累计字节数和最近输出时间。
- `rcctl job list <IP:端口> --fingerprint <SHA256> [--state <JobState>] [--text]`：列出已持久化任务快照，可按状态过滤。
- `rcctl job logs/input/close-input/cancel/wait` 已接入签名控制协议：日志按 stdout/stderr 与字节偏移读取，`job logs --follow` 会持续输出并在短暂断线后从相同 offset 重试；输入以 UTF-8 字节写入，关闭输入、取消和带超时等待均返回最新任务状态。
- 配对控制端通过 `session_start` / `session_authenticate` 在同一 TLS 连接上完成一次 ECDSA 挑战认证；认证后 exec/job 请求复用该连接且无需逐条签名。会话十分钟过期并自动续期，旧的逐请求签名协议继续兼容。
- 正常任务使用 8 个并发槽；超出上限的任务持久化为 `Queued`。TaskHost 以独立进程运行并持久化 pipe/PID 注册信息；Agent 启动时重连仍存活的 TaskHost，无法重连的 Running/Queued 快照标记为 `InterruptedByReboot`，不会自动重放命令。

控制端必须继续使用配对时相同的 `RC_CONTROLLER_DATA_ROOT`，以便读取 DPAPI 保护的控制端私钥。长期任务的真实 TLS 回环尚未完成：本轮在受限 Codex 沙箱下尝试回环时，Windows Schannel 客户端报“安全包中没有可用的凭证”，TLS 尚未到达 Agent 证书校验，因此该结果不能用于判断 Agent 或配对协议。此前在普通已登录 Windows 用户会话的自动配对中，输入一次性码后复现过 J-PAKE 对端载荷校验失败；CLI 与 Agent 已加入不泄露秘密的轮次阶段诊断，待在具备 Schannel 凭据的交互会话中复测。J-PAKE 载荷 JSON 往返与任务运行中状态刷新、终态持久化读取均已由单元测试覆盖；在该配对回归修复前，不将 `job` 标为已完成网络端到端验证。

## 2026-07-12 文件读写与可恢复传输验收

文件控制面已完成、提交并通过最终本机验收：

- `rcctl fs list/stat/read/write` 已接入复用认证会话；文件访问被限制在 `RC_AGENT_FILE_ROOT`（默认当前 Agent 用户目录），拒绝 `..` 越界、Windows 设备名和路径中的 reparse point。
- 小文件写入使用同目录临时文件后原子移动，`RC_FILE_MAX_WRITE_BYTES` 控制单次写入上限；已有文件在未指定覆盖时保持不变。
- `rcctl copy upload/download/status` 使用固定大小分块；每块携带 SHA-256，上传接收记录和会话快照持久化到 SQLite，重连后可通过 `--session` 跳过已确认分块。
- 完成上传前重新计算完整文件 SHA-256；落盘分块被修改、重复 offset 内容不同或最终摘要不符时拒绝完成。目录 manifest 使用规范化相对路径，并保留文件、嵌套目录和空目录结构，不复制 ACL 或所有权。
- `RC_TRANSFER_QUOTA_BYTES` 默认 200 MB，限制单次 manifest 的文件总字节数；`RC_TRANSFER_MAX_CHUNK_BYTES` 控制最大分块；会话默认 24 小时过期并清理未完成分块。
- 新增验收覆盖：目录与空目录上传、重复块幂等、持久化分块篡改、过期会话清理、原子写/分块可配置限额，以及真实 TLS 认证会话中的文件读写、状态查询和两段上传恢复。

当前限制：尚未完成真实双机/双 VM 的大文件和目录中断恢复压力验收。

## 验证结果

- `.\.dotnet\dotnet.exe build .\Rc.RemoteController.sln -p:NuGetAudit=false --no-restore -v minimal`：通过，0 warnings / 0 errors。
- `dotnet test .\tests\Rc.Agent.Tests\Rc.Agent.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~AgentCertificateManagerTests|FullyQualifiedName~PairingCoordinatorTests|FullyQualifiedName~JpakePairingSessionTests" -v minimal`：8 通过。
- `git diff --check`：通过（仅有 Git 的既有 LF/CRLF 提示）。
- `.\.dotnet\dotnet.exe test .\Rc.RemoteController.sln -p:NuGetAudit=false --no-restore -v minimal`：192 项测试全部通过（Contracts 108、TaskHost 11、Agent 73）。
- 完成上述构建和测试后，已停止 9 个仅由项目内 SDK 启动的 Roslyn/MSBuild 残留进程，并删除临时 SDK 目录 `E:\RemoteController\.dotnet`；系统当前只有 .NET Runtime、没有可用 SDK，因此后续构建需安装正式 SDK 或重新准备隔离工具链。
- 新增 job 控制回归覆盖：签名字段防篡改、stdin/close-input、按偏移日志读取、等待超时与取消终态。
- 新增调度/恢复回归覆盖：单并发排队、Queued 不重放、缺失 TaskHost 中断标记，以及真实独立 TaskHost 跨注册表重启后重连并完成。
- 新增真实 TLS 会话回归：挑战签名绑定 session/challenge/expiry，同一 TLS 连接认证一次后连续执行两次无签名 job 请求。
- 已在普通已登录 Windows 用户会话完成 TLS 回环配对与单次命令执行：命令的 stdout、stderr 和退出码 `7` 均正确返回。

## 未完全实现功能的优先级

以下排序只包含尚未完全实现的产品功能，不把当前缺少环境的双机/双 VM 测试算作功能阻塞项。优先级按安全边界、依赖关系和核心可用性排序。

### P0：配对撤销、安全控制与审计

1. **本地安全 `unpair` 与活动认证会话失效（已实现并通过本机 TLS 回归）**：`rc-agent unpair` 仅在被控端本地执行；删除配对时递增持久化 generation，旧邀请失效；认证会话和文件/任务请求每次重新核对当前控制器，取消配对后立即返回未认证。
2. **持久化审计事件（核心路径已实现，继续做失败路径穷举审阅）**：SQLite 已持久化配对、认证、任务、文件/传输和取消配对事件，并记录控制器、目标、结果、时间、错误码及详情；审计配额会淘汰最旧事件。核心成功路径、撤销和文件操作失败已覆盖，仍需逐项检查所有异常分支是否都留下审计。
3. **配对滥用防护（已实现核心策略）**：错误密码学轮次会持久化失败计数；达到阈值后进入冷却，`pair_start` 返回 `ResourceExhausted` 并写入不含秘密的审计；成功配对会重置失败状态。持久化状态和监听器阻断已有自动化测试，真实双机重复错误码压力测试后续补齐。
4. **限额耗尽的产品行为（核心文件/传输与审计语义已实现）**：原子写入和 manifest 超限统一为 `ResourceExhausted`；Windows 磁盘满错误映射为 `ResourceExhausted`；失败操作写入审计；日志、审计和传输清理均有配额实现。仍需在可控磁盘不足环境中做故障注入，并复核 SQLite/日志写入失败时的最终一致性。

P0 当前状态：核心产品功能已补齐并通过单机自动化；剩余项以异常分支穷举审阅和磁盘不足故障注入为主。真实双机/双 VM 验收仍按后续环境测试处理。

### P1：真正的交互终端和任务语义

1. **真正的 ConPTY/HPCON 任务启动（已实现并通过 Windows 测试）**：TaskHost 使用 `CreatePseudoConsole`、`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 和 `CreateProcessW` 启动真实伪终端；PTY 输出合并为 stdout，支持交互输入。
2. **正确的 Ctrl+C 与终止升级（已实现并测试）**：取消先向 ConPTY 写入 Ctrl+C；进程在可配置宽限期内未退出时终止完整进程树。自动化同时覆盖 Ctrl+C 正常退出和忽略中断后的强杀回退。
3. **终端尺寸与 resize（已实现并测试）**：执行契约支持初始列/行，控制协议和 CLI 支持 `job resize`，resize 签名绑定 job/列/行；测试覆盖 resize 后输出保留和非 PTY 任务稳定拒绝。
4. **任务调度配置化和边界行为（核心边界已补齐）**：`RC_NORMAL_TASK_LIMIT`、`RC_ELEVATED_TASK_LIMIT` 和 `RC_CANCELLATION_GRACE_MS` 已接入；调度器会把取消令牌传递给已开始的工作。自动化已覆盖单并发严格 FIFO、普通/提权队列隔离、Queued 重复取消且不启动 TaskHost、自然退出后的取消幂等、无控制端读取时的大输出排空、TaskHost 突然退出后的持久化终态，以及重启恢复。仍需继续做高次数退出/取消竞争压力测试和更多 TaskHost 崩溃时点矩阵。

P1 当前状态：ConPTY、Ctrl+C/强杀、resize 和主要调度边界已完成；剩余工作集中在高次数退出/取消竞争压力测试与更细的 TaskHost 崩溃时点矩阵。

### P2：显式提权执行 Broker

1. **Privileged Broker 本地 IPC**：实现受 ACL 和消息认证保护的 named pipe；Broker 不开放 TCP，非 Agent 本地进程不能伪造请求。
2. **`exec --elevated` 和提权任务路由**：只有显式提权请求进入 Broker，普通任务始终使用普通调度器；任务元数据和审计记录实际执行身份。
3. **独立 elevated 队列与并发限额**：与普通任务队列隔离，支持独立配置、排队、取消和恢复语义。

### P3：服务化、安装、升级和卸载

1. 安装 Agent 与 Privileged Broker Windows Service，配置服务账户、ACL、开机启动和故障恢复策略。
2. 配置明确的 LAN 防火墙规则，不扩大监听范围或开放 Broker 本地接口。
3. 提供幂等安装、修复、升级和卸载流程，清理服务、计划任务、防火墙规则和受控数据。
4. 为安装脚本增加 `-WhatIf`、重复安装/卸载、部分失败恢复和升级测试。

### P4：登录会话内 GUI 自动化

1. 实现 UiAgent 在指定登录用户会话内启动、注册 session ID、显示器和能力版本。
2. 实现显示器/窗口枚举、明确目标的截图、窗口激活/最小化/最大化/恢复/移动/关闭。
3. 实现 UI Automation 元素树、鼠标、键盘、文本、快捷键和剪贴板操作。
4. 严格限制到配置的活动登录会话；不支持 UAC 安全桌面；无会话、窗口关闭或显示器断开时返回稳定错误。

### P5：CLI 契约、文档、CI 和发布

1. **CLI 契约收口**：统一所有命令的 JSON 成功/失败 envelope、稳定错误码、非零失败退出码和不改变 DTO 的 `--text` 渲染。
2. 补齐所有命令组的参数解析、冲突参数、Agent 不可用、认证失败和错误映射测试。
3. 完成安装、运维、协议、安全、限额、恢复、取消配对、提权和 GUI 限制文档。
4. 建立 Windows CI，执行 restore、build、单元测试、契约测试和可条件启用的 Windows 集成测试。
5. 生成 self-contained Windows x64 产物，校验 Agent、CLI、TaskHost、Broker 和 UiAgent 发布文件完整后再创建版本。

推荐功能实现顺序：`unpair`/会话失效 → 完整审计 → 配对滥用防护与限额错误语义 → ConPTY/Ctrl+C/resize → Broker/elevated 队列 → 服务安装 → GUI → CLI 契约收口 → 文档、CI 和发布。

## 后续补齐的环境验收

当前暂无可用双机或双 VM 环境，以下项目标记为**环境具备后补齐的测试**，不阻塞上述单机可开发功能：

- 真实网络下重复执行配对、错误码、过期码、`unpair` 后重配和 J-PAKE 稳定性验证；
- 控制端断开/重连、Agent 重启、并发长期任务和日志 offset 恢复；
- 大文件、多文件目录和空目录传输，以及上传/下载中断续传；
- 分块或最终内容被改变时拒绝完成；
- 网络延迟、反复中断、配额耗尽和磁盘不足场景；
- Broker、Windows Service、GUI 和完整发布流程实现后的端到端验收。

环境具备后应执行完整双机/双 VM 发布门禁；在此之前，继续通过隔离目录、本机 TLS 回环、独立 TaskHost 进程和自动化故障注入扩大覆盖，但不能将这些替代测试表述为真实双机验收。

## 下一里程碑

认证会话、长期任务和文件传输改动已在 `6be9ca6` 提交。P0 核心能力与 P1 的 ConPTY/Ctrl+C/resize、FIFO、Queued 取消幂等、慢消费者和 TaskHost 突然退出收敛已在当前工作树实现并通过单机自动化；下一项是高次数退出/取消竞争压力测试、更细的 TaskHost 崩溃时点矩阵，同时继续审阅 P0 的异常审计及磁盘不足一致性。真实双机/双 VM 验收待测试环境具备后补齐。开始下一轮开发前需安装正式 .NET 8 SDK 或重新准备隔离工具链。

## 2026-07-10 本机回环验证

已在普通已登录 Windows 用户会话中完成真实 TLS 回环验证（不依赖受限 Codex 沙箱的 Schannel）：

- Agent 显示一次性配对码，控制端输入后完成三轮 J-PAKE 与控制端 ECDSA 证书确认。
- `rcctl probe ... --text` 在配对后显示 `paired: True`。
- 重启同一 Agent 数据目录后，TLS 指纹保持不变，`probe` 仍显示 `paired: True`。
- 修复了两个实际运行问题：Agent ECDSA TLS 私钥需要导入当前用户的持久化密钥存储供 Schannel 使用；CLI 必须在每轮先创建本方 J-PAKE 载荷、再接收对端载荷。

## 2026-07-10 单次远程命令执行

已实现已配对控制端的最小远程执行闭环：

- `rcctl exec` 以当前登录用户身份在 Agent 执行一条 PowerShell 或 cmd 命令，等待其结束后返回退出码、标准输出和标准错误。
- 执行请求由控制端配对时持久化的 ECDSA P-256 私钥签名；Agent 仅接受已保存的唯一控制端证书验证通过的请求。
- 当前 Agent 串行执行 `exec_once`，繁忙时明确拒绝第二条请求；每个输出流随响应最多返回 256 KiB，完整输出仍保存在 Agent 数据目录中，供后续任务日志接口复用。
- 已在本机完成真实回环：`pair` 成功后，远端命令同时输出 stdout/stderr 并以退出码 `7` 结束；CLI 原样返回两路输出且自身退出码为 `7`。

控制端使用方法（需先用同一个 `RC_CONTROLLER_DATA_ROOT` 完成过 `rcctl pair`）：

```powershell
rcctl exec <IP:端口> --fingerprint <Agent TLS SHA-256 指纹> `
  --command "python --version" --text

rcctl exec <IP:端口> --fingerprint <Agent TLS SHA-256 指纹> `
  --shell cmd --command "dir C:\\" --text
```

不加 `--text` 时，CLI 输出包含任务快照和 Base64 编码 stdout/stderr 的 JSON 结果；加 `--text` 时，stdout 和 stderr 分别透传到本地对应流，`rcctl` 的退出码等于远端进程退出码。
