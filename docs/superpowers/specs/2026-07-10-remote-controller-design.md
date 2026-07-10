# Windows Remote Controller 设计

## 目标

为同一局域网内两台 Windows 10/11 x64 电脑提供一套面向 AI agent 的远程控制工具。控制端以 CLI 调用被控端，行为尽量接近本地命令：启动并发命令、持续输入、增量读取输出、查询或取消长任务、读写和断点传输文件，以及在用户登录时控制 GUI。

## 约束与边界

- 仅 Windows 10/11 x64；仅同一局域网；不实现公网穿透、中继或远程桌面视频流。
- 每个被控端仅配对一个控制端。已配对控制端可执行任意命令；默认普通账户，`--elevated` 才走特权执行路径。
- 控制端断线不停止任务；系统重启会停止任务并标记 `interrupted_by_reboot`，不自动重跑。
- GUI 功能是独立模块：用户登录后可用，未登录时返回稳定的不可用错误；不尝试自动处理 UAC 安全桌面。
- 任务日志、任务元数据和审计记录默认总配额为 200 MB，可配置。

## 组件

- `rcctl.exe`：控制端 CLI。默认 JSON Lines 输出，`--text` 为人类显示。
- `rc-agent.exe`：普通账户 Windows 服务。持有局域网 HTTPS/gRPC 端点、配对、任务目录、文件传输与持久状态。
- `rc-taskhost.exe`：每个任务独立的常驻宿主，拥有 ConPTY、子进程树、stdin 管道、分段 stdout/stderr 日志和本机控制管道。
- `rc-elevated-broker.exe`：独立高权限 Windows 服务，只接受来自 `rc-agent` 的经过 ACL 和签名验证的本机命名管道请求。
- `rc-ui-agent.exe`：指定用户登录后启动的会话代理，使用 Windows UI Automation、窗口 API、截图 API 与 SendInput；向核心服务登记会话和显示器。
- `Rc.Contracts`：所有 RPC、JSON DTO、错误码、配置和本机 IPC 消息的唯一公共定义。

## 网络、发现与身份

控制通道使用 HTTP/2 gRPC + TLS 1.3，配对后使用双方证书的双向 TLS。UDP 组播发现仅发布设备 ID、显示名、端口、协议版本和服务证书指纹，不发布密钥、配对码、用户或文件信息。

配对同时支持发现流程与手工 `IP:端口 + 一次性配对码`。被控端在本机命令或已登录的 UI 代理中显示短时码。码参与 PAKE 握手，成功后双方签发并固定对方设备证书；服务拒绝第二个控制端，直到被控端本机显式解除配对。

## 任务模型

`exec` 立即返回 UUID `job_id`。状态为 `queued`、`running`、`exited`、`failed_to_start`、`cancelled` 或 `interrupted_by_reboot`。任务以参数数组直接启动；只有显式 shell 模式才调用 PowerShell 或 CMD。每个任务保存开始/结束时间、PID、执行身份、工作目录、退出码、输出字节偏移、最后输出时间与 CPU/内存观测值。

控制端用 `logs --after <offset>` 拉取 stdout/stderr 的原始字节块，并用 `--follow` 连续等待；JSON 输出将字节编码为 Base64。ConPTY 保留交互式程序的 ANSI/回车行为。`input` 可多次发送字节或 EOF。默认最多并发 8 个普通任务和 2 个提权任务，超出部分排队。

取消先注入 Ctrl+C，等待 10 秒；仍未退出则杀掉整棵进程树。`rc-agent` 重启不应中止 `rc-taskhost`；恢复时通过命名管道重建登记。系统重启后不会重启任务。

## 文件模型

小文本可由 `fs read`/`fs write` 按字节区间读写；写入通过临时文件和原子替换完成。`copy push` 与 `copy pull` 支持单文件和目录，按固定块传输、逐块哈希、会话清单和整体 SHA-256 校验；中断后只补传缺块。目录传输只复制内容和相对路径，不复制 ACL、所有者或 Windows 特殊元数据。

## GUI 模型

`ui status` 明确给出交互会话可用性。可用时支持多显示器、窗口枚举、窗口激活/最小化/最大化/还原/移动/关闭、屏幕或窗口截图、鼠标、键盘、文本输入、快捷键和剪贴板。截图和输入必须显式指定显示器或窗口；无会话、窗口失效或显示器失效均返回稳定错误码。

Windows-MCP 仅作为 UI Automation 设计和测试参考；产品不依赖其 Python 运行时、网络服务或遥测。

## CLI 契约

主要命令为 `discover`、`pair`、`exec`、`job list/status/logs/input/wait/cancel`、`fs list/stat/read/write`、`copy push/pull` 与 `ui status/displays/windows/screenshot/window/mouse/key/type/clipboard`。

每行 JSON 采用 `{ "ok": true, "result": ... }` 或 `{ "ok": false, "error": { "code": "...", "message": "...", "retryable": false } }`。二进制内容统一为 Base64，分页或日志读取统一使用不透明 cursor 或字节 offset。`--text` 只改变呈现方式，不改变命令结果、退出码或安全语义。

## 运行与审计

配置、证书、SQLite 状态库、分段日志、传输临时块和审计记录存放于受 ACL 保护的 `ProgramData` 子目录。配额淘汰最早完成任务的记录；运行中任务仅保留最近输出尾部且继续执行。审计记录包含控制端设备 ID、操作类别、目标、执行身份、时间和结果，但不重复保存大段任务输出。
