# 运维说明

## 服务与 UI 代理

`Install-RemoteController.ps1` 会将 Agent 安装为 `LocalService`，将 Broker 安装为 `LocalSystem`，为配置的 TCP 端口设置防火墙访问规则，并创建按用户运行的 `RemoteControllerUiAgent` 登录任务。使用 `-UiUser DOMAIN\user` 选择交互式账户。该任务每十秒刷新一次会话注册。

安装与卸载均幂等。可通过以下命令无修改地预览任一操作：

```powershell
.\scripts\Install-RemoteController.ps1 -SourcePath .\artifacts\publish -WhatIf
.\scripts\Uninstall-RemoteController.ps1 -WhatIf
```

当需要保留配对状态、日志和传输快照以便诊断时，请在卸载时使用 `-KeepData`。

## 任务与传输

持久任务可使用 `rcctl job start`、`status`、`list`、`logs`、`input`、`close-input`、`wait` 和 `cancel`。断开连接不会终止正在运行的 TaskHost；使用日志偏移量或 `--follow` 可恢复读取。重启会将保留下来的快照标记为已中断，且绝不会重新运行命令。

使用 `rcctl fs list|stat|read|write` 执行受限的文件操作，使用 `rcctl copy upload|download|status` 执行可断点续传的文件或目录传输。恢复传输时请保留传输会话 ID。

## UI 自动化

开始 GUI 操作前请使用 `rcctl ui status`。`ui screenshot`、输入和鼠标命令都需要 `display <index>` 或 `window <handle>`。`ui elements window <handle>` 会返回受边界限制的 Windows UI Automation 子树；当目标声明相应 UI Automation 模式时，`ui element ...` 支持 `focus`、`invoke`、`setvalue`、`select`、`expand` 和 `collapse`。

没有活动会话、窗口已关闭、显示器已断开、元素运行时 ID 过期或模式不受支持时，会返回错误信封。这些都是预期情况，应通过重新查询会话或树来处理。
