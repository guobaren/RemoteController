# 协议与 CLI 契约

所有未使用文本模式的 `rcctl` 命令都会恰好输出一行 JSON：

```json
{ "ok": true, "result": {} }
```

或：

```json
{ "ok": false, "error": { "code": "invalid_request", "message": "...", "retryable": false } }
```

退出码 `0` 表示控制命令成功；`2` 表示无效的命令行请求，`130` 表示已取消。控制命令失败时会返回非零退出码和失败信封。即使控制操作本身成功，`rcctl exec` 也会有意返回远程进程的退出码，因此非零的 exec 结果仍会带有成功信封。JSON 模式中的二进制字段（截图、剪贴板数据、分块和日志）均采用 Base64 编码。

控制传输采用 TLS 上的按行分隔、带版本的 JSON。通过身份验证的控制端会先建立 ECDSA 挑战会话；后续的任务、文件和 UI 请求均复用该会话。UI 请求只会转发给刚刚完成注册的本地 `Rc.UiAgent` 会话。

UI 命令需要提供指纹和受管端地址。例如：

```powershell
rcctl ui windows 192.168.1.50:43001 --fingerprint <SHA256>
rcctl ui elements 192.168.1.50:43001 --fingerprint <SHA256> window <handle> --depth 6
rcctl ui element 192.168.1.50:43001 --fingerprint <SHA256> window <handle> <runtime-id> invoke
```

`runtime-id` 是 `ui elements` 返回的逗号分隔 `runtimeId`；操作会被限制在指定窗口的子树内。
