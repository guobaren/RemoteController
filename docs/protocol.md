# Protocol and CLI contract

All non-text `rcctl` commands emit exactly one JSON line:

```json
{ "ok": true, "result": {} }
```

or:

```json
{ "ok": false, "error": { "code": "invalid_request", "message": "...", "retryable": false } }
```

Exit code `0` means a successful control command. `2` is an invalid command-line request and `130` is cancellation. A failed control command has a nonzero exit code and a failure envelope. `rcctl exec` deliberately returns the remote process exit code even when the control operation itself succeeds, so a nonzero exec result still has a success envelope. Binary fields (screenshots, clipboard data, chunks, and logs) are Base64 in JSON mode.

The control transport is newline-delimited versioned JSON over TLS. An authenticated controller first creates an ECDSA challenge session; subsequent job, file, and UI requests reuse that session. UI requests are forwarded only to a freshly registered local `Rc.UiAgent` session.

UI commands require a fingerprint and endpoint. Examples:

```powershell
rcctl ui windows 192.168.1.50:43001 --fingerprint <SHA256>
rcctl ui elements 192.168.1.50:43001 --fingerprint <SHA256> window <handle> --depth 6
rcctl ui element 192.168.1.50:43001 --fingerprint <SHA256> window <handle> <runtime-id> invoke
```

`runtime-id` is the comma-separated `runtimeId` returned by `ui elements`; actions are constrained to the specified window subtree.
