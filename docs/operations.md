# 运维说明

## 服务与 UI 代理

`Install-RemoteController.ps1` 会将 Agent 安装为 `LocalService`，将 Broker 安装为 `LocalSystem`，为配置的 TCP 端口设置防火墙访问规则，并创建按用户运行的 `RemoteControllerUiAgent` 登录任务。使用 `-UiUser DOMAIN\user` 选择交互式账户。该任务每十秒刷新一次会话注册。

安装与卸载均幂等。可通过以下命令无修改地预览任一操作：

```powershell
.\scripts\Install-RemoteController.ps1 -SourcePath .\artifacts\publish -WhatIf
.\scripts\Uninstall-RemoteController.ps1 -WhatIf
```

当需要保留配对状态、日志和传输快照以便诊断时，请在卸载时使用 `-KeepData`。

## 一键更新

发布目录包含 `Update-RemoteController.ps1`。控制端通过 `rcctl update apply <IP:port> --fingerprint <SHA256> --package <publish-directory>` 创建文件清单、上传分块并触发更新；`--wait` 会在 Agent 重启期间重连并等待最终状态。

受管端只接受已配对控制端的更新请求，并同时校验每个分块和完整文件的 SHA-256。更新器会先停止受管端服务和 UI 任务，保留当前安装目录作为回滚副本，再调用安装器恢复 Agent、Broker 和 UiAgent。更新失败时会还原旧目录并尝试重启旧服务；`ProgramData` 数据根不会被替换。

使用 `rcctl update status <IP:port> --fingerprint <SHA256> --update <GUID>` 查询已提交更新的状态、安装任务 ID 与失败原因。

## 任务与传输

持久任务可使用 `rcctl job start`、`status`、`list`、`logs`、`input`、`close-input`、`wait` 和 `cancel`。断开连接不会终止正在运行的 TaskHost；使用日志偏移量或 `--follow` 可恢复读取。重启会将保留下来的快照标记为已中断，且绝不会重新运行命令。

使用 `rcctl fs list|stat|read|write` 执行受限的文件操作，使用 `rcctl copy upload|download|status` 执行可断点续传的文件或目录传输。恢复传输时请保留传输会话 ID。

## UI 自动化

开始 GUI 操作前请使用 `rcctl ui status`。`ui screenshot`、输入和鼠标命令都需要 `display <index>` 或 `window <handle>`。`ui elements window <handle>` 会返回受边界限制的 Windows UI Automation 子树；当目标声明相应 UI Automation 模式时，`ui element ...` 支持 `focus`、`invoke`、`setvalue`、`select`、`expand` 和 `collapse`。

没有活动会话、窗口已关闭、显示器已断开、元素运行时 ID 过期或模式不受支持时，会返回错误信封。这些都是预期情况，应通过重新查询会话或树来处理。
