# RemoteController 当前进度

> 最后核对：2026-07-16
>
> 本文件是仓库唯一的进度与验收记录；用户功能、部署流程、命令参考和安全声明统一维护在根目录 [README.md](../README.md)。

## 当前定位

RemoteController 是面向 AI Agent 的 Windows 10/11 x64 远端控制软件。控制端通过已配对、证书指纹固定的 TLS 会话控制受管端 Agent；受管端再将普通任务、显式提权任务和登录用户桌面 UI 操作分发到隔离的本地进程。

## 已完成能力

| 范围 | 当前状态 |
| --- | --- |
| 局域网发现、TLS 指纹探测、一次性配对和签名会话认证 | 已完成。Agent 只保存一个控制端，配对关系可在受管端本地撤销。 |
| 单次命令和持久化任务 | 已完成。支持 PowerShell/cmd、普通/显式提权、stdin、日志 offset/跟随、取消、等待、PTY resize 和 TaskHost 恢复语义。 |
| 文件操作与可恢复传输 | 已完成首版。支持受限文件根目录、文件/目录清单、分块哈希、上传/下载和会话续传。 |
| Privileged Broker 与 Windows 服务 | 已完成首版。Broker 使用 LocalSystem，Agent 使用 LocalService；安装脚本配置服务、SID ACL、防火墙和故障恢复。 |
| UI Agent 会话代理 | 已完成。`Rc.UiAgent` 以指定登录用户运行，注册活动会话、显示器、窗口和能力版本，通过受 ACL 保护的命名管道接受 Agent 转发的请求。 |
| 桌面 UI 自动化 | 已完成主要功能。支持显示器/窗口枚举、PNG 截图、激活/最小化/最大化/还原/关闭/移动、鼠标、键盘、快捷键、Unicode 文本、剪贴板，以及 UI Automation 元素树和 focus/invoke/setvalue/select/expand/collapse。 |
| 浏览器控制 | 已完成主要功能。`rcctl ui browser` 支持 Edge/Chrome 启动、导航和受控 Chromium CDP DOM/可访问性树读取；页面结构按窗口句柄和深度/元素数量限制返回。 |
| 被控端一键初始化 | 已完成。发布包包含配置驱动的 `Setup-RemoteControllerAgent.cmd`；重复运行可刷新文件、重启 Broker/Agent，并按配置清除旧配对、重新生成 TLS 设备身份和一次性配对码。 |
| CLI 与发布 | 已完成主要收口。CLI 提供 `ui` 命令组，非 `--text` 模式统一输出 JSON envelope；发布脚本包含 Agent、Broker、TaskHost、UiAgent、CLI 和 UI 验收程序。 |

## UI/浏览器验收证据

**真实双机 Hyper-V VM UI 验收已完成（2026-07-16）。** Controller 在主机侧通过已配对的 TLS 控制通道连接 Windows VM，受管端测试程序在 VM 的活动登录桌面中运行；不是同进程、单元测试或仅针对发布目录二进制的模拟验证。

已在主机 + Hyper-V Windows VM 的独立登录会话中验证：

- UI Agent 登录任务、Agent/Broker 服务和受限管道正常运行；
- 活动会话、显示器、窗口快照、有效 PNG 截图和 UI Automation 元素树可读取；
- Invoke、SetValue、列表/下拉框选择、嵌套树展开/收起、鼠标移动/按键/拖动/滚轮、键盘和剪贴板路径已验证；
- Edge 浏览器可启动并按窗口句柄导航；Chromium CDP DOM 可读取真实页面节点，而不是浏览器 `WebView`/`NativeViewHost` 外壳；
- 已验证浏览器快捷键、地址栏导航和页面 DOM 内容读取。

本次真实双机 UI 验收使用 `E:\RemoteController\scripts\Test-RemoteControllerUi.ps1`，共通过 25 项检查：控件调用、值写入、下拉框和树节点、鼠标三键/拖拽/滚轮、键盘输入以及剪贴板写入和回读。

**真实双机 Hyper-V VM 多轮交互任务验收已完成（2026-07-16）。** Controller 通过已配对的远程控制通道，在对端 `E:\RemoteController` 项目目录下启动 `Rc.InteractiveTestApp.exe` 的 PTY 持久任务，完成两轮历史计数与随机挑战输入：第一次 `HISTORICAL_RUN_COUNT=0` 成功后计数为 1，第二次读取计数 1 并成功后计数为 2；每轮均先提交历史计数、再提交程序实时生成的随机数，两轮均返回 `RESULT: PASS`、退出码 0。

UI 自动化只在配置的活动登录用户会话中工作。锁屏、注销、没有活动桌面、UAC 安全桌面和 Winlogon 界面不属于支持范围。

## 测试状态

最新发布验收记录显示：

- Release 构建通过，0 warning、0 error；
- UI 合约、UI Agent 管道代理、Chromium DOM 映射和 UI 测试程序均有自动化测试；
- 主机 + Hyper-V VM 核心验收已通过，包括本次真实双机 UI Automation 25 项检查、浏览器启动/导航/DOM、服务恢复、提权、持久化任务和文件续传；
- 真实双机 Hyper-V VM 多轮标准输入持久任务已通过：对端项目目录下连续完成两次历史计数/随机挑战交互，状态文件从 0 正确递增到 2，任务输出和终态均可通过 Controller 读取；
- 最新完整测试记录为 218 项通过、3 项失败。3 项失败发生在当前 Codex 沙箱创建 Schannel 客户端凭据阶段，TLS 握手尚未开始，不能代表真实 VM 网络验收失败。

## 尚待继续的工作

1. 在更多 Windows 版本、浏览器版本和输入法环境中完成完整 UI 回归，尤其是键盘文本输入、剪贴板和复杂元素树。
2. 继续验证 Edge/Chrome 页面 DOM 的兼容性；非 Chromium 浏览器的导航/窗口控制不等同于 Chromium CDP DOM 读取能力。
3. 完成安装、升级、卸载、登录任务重启和服务账户切换的更多真实环境回归。
4. 补充磁盘耗尽、配额耗尽、恶意配对码压力、审计异常分支和多节点压力测试。
5. 继续保持 Windows CI、发布制品哈希校验、第三方许可清单和版本发布说明同步。

## 文档约定

- 根目录 `README.md`：用户入口，维护功能、部署、使用方法、命令、配置和安全声明。
- `docs/CURRENT_PROGRESS.md`：维护实现状态、测试证据和剩余缺口。
- UI/浏览器功能以源码、自动化测试和真实 VM 验收记录共同确认；不能仅凭契约类型或发布目录中的二进制文件宣称功能完成。
