# Windows Remote Controller 实施计划

> **面向代理工作者：** 必须使用子技能：`superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans`，按任务逐项实施本计划。各步骤使用复选框（`- [ ]`）语法跟踪。

**目标：** 构建仅适用于 Windows 10/11 局域网的远程控制 CLI，支持持久交互任务、可恢复文件传输、显式提权以及可选的 GUI 会话自动化。

**架构：** 普通用户身份的 `rc-agent` 服务向一个已配对控制端提供 mTLS gRPC，并将每条命令委派给持久的 `rc-taskhost`。独立的特权 Broker 仅接受经验证的本地 IPC；每次用户登录时启动的 UI 代理仅在所选用户会话存在期间提供桌面自动化。

**技术栈：** .NET 8/C#、HTTP/2 与 TLS 1.3 上的 gRPC、SQLite、Windows 服务宿主、ConPTY/Win32 P/Invoke、命名管道、Windows UI Automation、xUnit、FluentAssertions、Microsoft.Extensions.Hosting。

---

## 项目布局

- `src/Rc.Contracts/`：RPC 架构、JSON 结果信封、错误代码、命令 DTO 和配置记录。
- `src/Rc.Agent/`：普通用户服务、gRPC 宿主、配对、发现、SQLite 存储库、调度器、传输协调器和本地 IPC 客户端。
- `src/Rc.TaskHost/`：单任务进程宿主、ConPTY 生命周期、输出分段和任务控制命名管道。
- `src/Rc.PrivilegedBroker/`：具有受限命名管道的高权限服务，以及提权任务启动功能。
- `src/Rc.UiAgent/`：按用户运行的 GUI 自动化代理和会话注册客户端。
- `src/Rc.Cli/`：`rcctl` 的 System.CommandLine 接口与 JSON Lines 渲染器。
- `tests/*`：与各可执行文件对应的单元、组件和 Windows 专用集成测试。
- `installer/`：服务/任务注册与卸载脚本。

### 任务 1：创建解决方案和公共契约

**文件：**
- 创建：`RemoteController.sln`、`Directory.Build.props`、`src/Rc.Contracts/*`、`tests/Rc.Contracts.Tests/*`

- [x] 为每个可执行文件和当前测试程序集创建 .NET 8 项目；在 `Directory.Build.props` 中启用可空引用类型、隐式 using、确定性构建和分析器。Broker/UI 测试程序集仍属于任务 8-9。
- [x] 在 `Rc.Contracts` 中定义 `ResultEnvelope<T>`、`RemoteError`、固定任务状态枚举、`JobSnapshot`、字节分块 DTO、文件清单 DTO、UI DTO 和稳定错误代码枚举。
- [ ] 定义其余 UI 服务接口。配对、任务和文件目前使用带版本的 TLS 上 JSON 控制 DTO，而非最初计划的 gRPC 传输；原始命令和字节载荷已保留。
- [x] 编写契约序列化测试，证明成功/错误信封和 Base64 字节分块具有稳定的 camelCase JSON 字段名。
- [x] 运行 `dotnet test tests/Rc.Contracts.Tests`，并提交契约基础（`543bd11`，后续还有加固提交）。

### 任务 2：添加安全配置、状态存储与配额处理

**文件：**
- 创建：`src/Rc.Agent/Configuration/*`、`src/Rc.Agent/Persistence/*`、`tests/Rc.Agent.Tests/Persistence/*`

- [x] 添加强类型 `AgentOptions` 记录，默认值为：Windows 10/11 x64、普通任务上限 8、提权任务上限 2、日志配额 200 MB、取消宽限期 10 秒；文件根目录及传输/写入限制均可通过环境变量配置。
- [x] 为设备标识、已配对控制端、任务快照、输出分段、传输会话和审计事件构建 SQLite 迁移。日志数据保存在受保护数据根目录下的分段文件中；SQLite 保存路径和偏移量。
- [x] 使用 DPAPI 存储私钥和配置的执行账户机密；若数据目录 ACL 允许不受信任用户写入则拒绝运行。
- [x] 编写测试以迁移空数据库、持久化/重新加载任务快照，并在测试配额下淘汰最旧的已完成日志，同时保留运行中任务的尾部日志。
- [x] 运行持久化测试，并提交 `feat: add durable agent state and log quota`（`3b2c3ea`）。

### 任务 3：实现身份、单控制端配对和 mTLS

**文件：**
- 创建：`src/Rc.Agent/Security/*`、`src/Rc.Cli/Commands/PairCommand.cs`、`tests/Rc.Agent.Tests/Security/*`

- [x] 首次启动时生成 Agent 设备密钥和自签名 TLS 证书；发现服务仅公开其 SHA-256 指纹。
- [ ] 实现短期有效的 J-PAKE 注册记录和一次性代码流程。Agent/控制端/受管端/指纹绑定及过期处理已实现；持久审计/速率限制的完善仍待完成。
- [x] 成功后保存并固定唯一的控制端证书。经身份验证的控制调用使用已固定 TLS 身份的 ECDSA 挑战会话；为兼容性保留旧版逐请求签名。
- [ ] `rcctl pair` 和 JSON 输出已实现；仅本地可用的 `unpair` 管理命令及活动会话失效机制仍待完成。
- [ ] 已有配对、指纹和第二控制端测试；添加命令后，补齐过期/错误代码的审计与速率限制覆盖，以及面向用户的取消配对/重新配对测试。
- [x] 运行安全测试，并提交单控制端配对/TLS 控制平面（`0316064`、`adf22ac`）。

### 任务 4：实现局域网发现

**文件：**
- 创建：`src/Rc.Agent/Discovery/*`、`src/Rc.Cli/Commands/DiscoverCommand.cs`、`tests/Rc.Agent.Tests/Discovery/*`

- [x] 发布带版本的 UDP 组播公告，仅包含设备 ID、显示名称、TCP 端口、协议版本和证书指纹。
- [x] 实现 `rcctl discover`，具有有界监听窗口、按设备 ID 去重以及按显示名称排序的 JSON 行。
- [x] 拒绝过大、格式错误和不支持版本的公告；绝不将发现数据当作身份验证。
- [x] 测试载荷脱敏、往返解码、重复抑制及手动配对后备路径。
- [x] 运行发现测试，并提交 `feat: add lan device discovery`（`534bddf`）。

### 任务 5：构建独立任务宿主与 ConPTY 输出管线

**文件：**
- 创建：`src/Rc.TaskHost/*`、`tests/Rc.TaskHost.Tests/*`

- [x] 添加从 Agent 传给 TaskHost 的 `TaskLaunchRequest` 文件契约，其中包含 argv/shell 模式、工作目录、环境、执行身份和任务 ID。
- [ ] 独立 TaskHost 进程、有序 stdout/stderr 分段文件和按任务隔离的命名管道控制均已实现；真正的 ConPTY/HPCON 启动仍待完成。
- [x] 跟踪 PID、开始/结束时间戳、退出码、最后输出时间戳和 CPU/内存计数器；不使用 kill-on-close 作业对象，以便 Agent 重启不会终止 TaskHost。
- [ ] 取消和进程树终止已实现；真正的 ConPTY Ctrl+C 发送及随后按配置宽限期等待仍待完成。
- [ ] TaskHost 测试覆盖命令执行、stdin/EOF、输出偏移、取消和启动失败；ConPTY 专用的 Ctrl+C 行为仍待完成。
- [x] 在 Windows 上运行 `dotnet test tests/Rc.TaskHost.Tests`，并提交 `feat: add durable interactive task host`（`5539f6c`、`7090feb`）。

### 任务 6：添加调度、恢复和任务 RPC

**文件：**
- 创建：`src/Rc.Agent/Jobs/*`、`src/Rc.Agent/Grpc/JobsService.cs`、`tests/Rc.Agent.Tests/Jobs/*`

- [ ] 普通任务的有界调度和排队快照已实现。独立的提权队列/限制仍与 Broker 一同待完成。
- [x] Agent 启动时，通过已注册的本地管道查找存活 TaskHost、重新连接并修复快照；将无法恢复的运行中/排队任务标记为 `interrupted_by_reboot`，而不重新启动。
- [x] 实现 TLS 上带版本 JSON 的 `Exec`、`ListJobs`、`GetJob`、`ReadLogs(afterOffset)`、follow/retry、`WriteStdin`、`CloseStdin`、`WaitJob` 和 `CancelJob` 控制操作。
- [ ] 状态流转会在报告前持久化；完成持久审计事件写入和审计校验。
- [ ] 测试覆盖普通排队、重新连接/恢复、重启中断和取消。提权并发及完整的重复取消/审计覆盖仍待完成。
- [x] 任务和会话测试通过；扩展调度器/会话工作已提交于 `6be9ca6`。

### 任务 7：实现可恢复文件与目录传输

**文件：**
- 创建：`src/Rc.Agent/Files/*`、`src/Rc.Agent/Grpc/FilesService.cs`、`tests/Rc.Agent.Tests/Files/*`

- [x] 为配置的文件根目录实现安全路径解析；拒绝设备名、根目录外的路径遍历及经由重解析点的访问。
- [x] 实现 `fs list`、`stat`、字节范围 `read` 和临时文件/原子替换 `write`，并提供可配置的原子写入上限。
- [x] 实现持久传输会话，包含固定大小分块、每块 SHA-256 回执、完整文件 SHA-256 校验、可恢复偏移、清理过期和可配置的传输/分块限制。
- [x] 使用规范化相对路径递归展开目录清单；仅保留内容、文件和空目录结构，不保留 ACL 或所有权。
- [x] 测试中断上传的持久化/恢复、下载范围恢复行为、篡改分块和已持久化分块的拒绝、最终哈希校验、原子写入安全/限制、空目录、过期和路径遍历拒绝；添加经身份验证的 TLS 文件控制集成测试。
- [x] 文件和完整解决方案测试在本地通过（2026-07-12 共 177 项）；验证后移除了项目内临时 SDK，实施内容已提交于 `6be9ca6`。

### 任务 8：添加显式提权执行 Broker

**文件：**
- 创建：`src/Rc.PrivilegedBroker/*`、`src/Rc.Agent/Elevation/*`、`tests/Rc.PrivilegedBroker.Tests/*`

- [ ] 将 Broker 注册为独立的特权 Windows 服务。其命名管道 ACL 必须只允许配置的 Agent 服务标识；除管道 ACL 外，还要校验每次安装生成的请求 MAC。
- [ ] 仅接受已签名的任务启动/取消/状态消息，并返回 Broker 任务宿主注册；绝不从特权 Broker 暴露 TCP 监听器。
- [ ] 将 `exec --elevated` 通过 Broker 路由，其他所有命令均走普通调度器。在任务元数据和审计事件中记录所选身份。
- [ ] 测试非 Agent 本地进程无法连接、无效 MAC 被拒绝、普通执行绝不调用 Broker，以及提权调用返回可见的提权身份。
- [ ] 在管理员 Windows 测试环境中运行 Broker 测试，并提交 `feat: add explicit elevated task broker`。

### 任务 9：实现 GUI 会话代理

**文件：**
- 创建：`src/Rc.UiAgent/*`、`src/Rc.Agent/Ui/*`、`tests/Rc.UiAgent.Tests/*`

- [ ] 为选定交互式账户安装按用户运行的登录任务。UI 代理通过本地命名管道向 `rc-agent` 注册会话 ID、显示器和能力版本。
- [ ] 实现多显示器和窗口枚举、针对窗口/显示器的截图、UI Automation 快照、激活/最小化/最大化/还原/移动/关闭、鼠标、键盘、文本、快捷键和剪贴板操作。
- [ ] 对截图和输入要求有效活动会话及显式显示器/窗口目标；将窗口关闭、显示器断开和无会话情况映射到已文档化的错误代码。
- [ ] 以 Windows-MCP 作为 UI Automation 覆盖范围的行为参考，但保留全部 .NET 实现并禁用所有遥测。
- [ ] 在单元测试中测试序列化和目标校验；可用时添加 Windows 集成测试，在两个虚拟/物理显示器上自动操作一次性测试窗口。
- [ ] 运行 UI 测试，并提交 `feat: add optional interactive gui session agent`。

### 任务 10：构建 JSON 优先 CLI 与安装流程

**文件：**
- 创建：`src/Rc.Cli/*`、`installer/Install.ps1`、`installer/Uninstall.ps1`、`tests/Rc.Cli.Tests/*`

- [ ] 实现所有已批准命令组：discover/pair、exec、job、fs/copy 和 ui。将直接 argv 与显式 shell 调用路由到不同的请求 DTO。
- [ ] 每条成功命令均输出一行 `ok: true` 的 JSON；每条失败命令均输出带稳定错误代码的 `ok: false` 并设置非零进程退出码。添加 `--text` 渲染器，但不改变 DTO。
- [ ] 将 `job logs --follow --text` 渲染为本地终端流；JSON 模式必须返回原始 Base64 分块、流名称和下一个字节偏移。
- [ ] 安装普通 Agent 和特权 Broker 服务，创建所选用户的 UI 登录任务，为显式局域网端口配置防火墙规则，并提供幂等的卸载/修复路径。
- [ ] 测试 CLI 参数解析、信封形状、Agent 不可用错误、稳定退出码和安装脚本的 dry-run/what-if 行为。
- [ ] 发布自包含单文件 x64 可执行文件，并提交 `feat: ship json cli and windows installer`。

### 任务 11：记录、验证并打包首个版本

**文件：**
- 创建：`README.md`、`docs/security.md`、`docs/protocol.md`、`docs/operations.md`、`.github/workflows/ci.yml`

- [ ] 记录首次安装、普通账户配置、发现配对、手动 `IP:port + code` 配对、取消配对、日志配额、提权、传输恢复和 GUI 会话限制。
- [ ] 记录威胁边界：受信任的单一控制端、局域网发现未经身份验证、PAKE/mTLS 保护控制流量、不支持 UAC 桌面，以及重启不会恢复任意任务。
- [ ] 添加用于还原、构建、单元测试、契约测试和 Windows 集成测试门控的 CI；如果缺少任何自包含二进制文件，则发布打包必须失败。
- [ ] 运行双 VM 端到端验收场景：配对、启动两个并发 Python/pip 命令、流式输出、写入 stdin、重新连接、传输/恢复目录、运行提权命令、登录并控制测试 GUI 窗口，最后验证审计/日志配额行为。
- [ ] 提交 `docs: document remote controller setup and operation`，并为第一个经过测试的 Windows x64 版本打标签。

### 任务 12：实现控制端与被控端一键更新

**目标：** 由控制端发起一次更新操作，安全地完成控制端组件与被控端 Agent/Broker/UiAgent 组件的版本检查、下载、校验、切换、重启和结果确认。

- [x] 控制端实现版本检查、更新清单生成、签名/哈希校验、更新包上传、进度展示、更新结果确认，以及断线后的状态查询与重连。
- [x] 被控端实现经过认证的更新协议，接收并暂存更新包，校验版本兼容性、签名和完整性，并拒绝未授权或降级的更新。
- [x] 被控端实现 Agent、Broker 和 UiAgent 的有序停止、原子替换、配置/证书/配对关系与业务数据保留，以及更新后的服务和登录任务恢复。
- [x] 更新失败时保留可启动的旧版本并支持回滚；更新过程记录审计事件，避免重复执行或并发更新破坏服务状态。
- [ ] 在控制端和被控端分别补齐版本协商、权限边界、断点/重试、失败恢复、升级/回滚和真实双节点一键更新验收测试。

## 最终验收检查

- [ ] AI agent 可以解析每一条 CLI 结果，无需屏幕抓取。
- [ ] 已配对控制端可以断开/重新连接而不杀死运行中的任务，并能从字节偏移处恢复输出。
- [ ] 重启绝不会悄然重放命令。
- [ ] 未配对的局域网主机不能调用控制 RPC，也不能连接特权/UI 本地 IPC。
- [ ] 文件/目录传输可在中断后恢复，并拒绝被篡改的内容。
- [ ] GUI 操作仅在配置的已登录用户会话中工作；不可用时能正确报告状态。
