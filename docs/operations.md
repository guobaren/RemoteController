# Operations

## Services and UI agent

`Install-RemoteController.ps1` installs Agent as `LocalService`, Broker as `LocalSystem`, firewall access for the configured TCP port, and a per-user `RemoteControllerUiAgent` logon task. Use `-UiUser DOMAIN\user` to choose the interactive account. The task refreshes its session registration every ten seconds.

Install and removal are idempotent. Preview either without mutation:

```powershell
.\scripts\Install-RemoteController.ps1 -SourcePath .\artifacts\publish -WhatIf
.\scripts\Uninstall-RemoteController.ps1 -WhatIf
```

Use `-KeepData` while uninstalling when pairing state, logs, and transfer snapshots must remain for diagnosis.

## Jobs and transfers

Use `rcctl job start`, `status`, `list`, `logs`, `input`, `close-input`, `wait`, and `cancel` for persistent work. A disconnect does not terminate a running TaskHost; use log offsets or `--follow` to resume reads. A reboot marks surviving snapshots as interrupted and never reruns the command.

Use `rcctl fs list|stat|read|write` for bounded file operations and `rcctl copy upload|download|status` for resumable file/directory transfers. Preserve the transfer session ID when resuming.

## UI automation

Use `rcctl ui status` before GUI work. `ui screenshot`, input, and mouse commands require `display <index>` or `window <handle>`. `ui elements window <handle>` returns the bounded Windows UI Automation subtree; `ui element ...` supports `focus`, `invoke`, `setvalue`, `select`, `expand`, and `collapse` when the target advertises the corresponding UI Automation pattern.

No active session, a closed window, a detached display, stale element runtime ID, or unsupported pattern returns an error envelope. These conditions are expected and should be handled by re-querying the session/tree.
