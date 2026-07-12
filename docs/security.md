# Security model

## Trust boundaries

- UDP discovery is an unauthenticated hint. The controller pins the Agent SHA-256 certificate fingerprint before pairing or control traffic.
- Pairing uses a local one-time code and J-PAKE transcript. The endpoint persists only one controller certificate.
- Control calls run through a TLS-pinned ECDSA challenge session. The Agent rejects calls from an unpaired or stale controller session.
- Agent state, keys, audit data, transfer state, and task output reside under an ACL-protected data root. Unsafe pre-existing ACLs prevent startup.
- Elevated work is routed only through the local privileged Broker pipe, protected by explicit SID ACLs and HMAC request authentication. The Broker has no TCP listener.

## UI boundary

`Rc.UiAgent` runs as the selected logged-in user. Its registration and command pipes use explicit SIDs in service deployments; in development they use `CurrentUserOnly`. UI actions require an active session and an explicit display, window, or UI Automation element rooted beneath an explicit window. UAC secure desktop is never automated.

## Operational safeguards

- File operations stay beneath `RC_AGENT_FILE_ROOT`, reject traversal/reparse points, and use atomic replacement for small writes.
- Transfers verify per-chunk and final SHA-256 hashes and expire unfinished sessions.
- Task, log, audit, and transfer quotas bound persistent resource consumption.
- `rc-agent unpair` is local-only and invalidates active authentication sessions.
