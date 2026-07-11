# Windows Remote Controller Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows 10/11 LAN-only remote-controller CLI with durable interactive jobs, resumable files, explicit elevation, and optional GUI-session automation.

**Architecture:** A normal-user `rc-agent` service exposes mTLS gRPC to one paired controller and delegates each command to a durable `rc-taskhost`. A distinct privileged broker accepts only authenticated local IPC, and a per-login UI agent exposes desktop automation only while its selected user session exists.

**Tech Stack:** .NET 8/C#, gRPC over HTTP/2 and TLS 1.3, SQLite, Windows Service hosting, ConPTY/Win32 P/Invoke, named pipes, Windows UI Automation, xUnit, FluentAssertions, Microsoft.Extensions.Hosting.

---

## Project layout

- `src/Rc.Contracts/`: RPC schemas, JSON result envelope, error codes, command DTOs and config records.
- `src/Rc.Agent/`: normal-user service, gRPC host, pairing, discovery, SQLite repositories, scheduler, transfer coordinator and local IPC client.
- `src/Rc.TaskHost/`: one-job process host, ConPTY lifecycle, output segments and task control named pipe.
- `src/Rc.PrivilegedBroker/`: high-privilege service with restricted named pipe and elevated task launches.
- `src/Rc.UiAgent/`: per-user GUI automation agent and session registration client.
- `src/Rc.Cli/`: `rcctl` System.CommandLine surface and JSON Lines renderer.
- `tests/*`: unit, component and Windows-only integration tests mirroring each executable.
- `installer/`: service/task registration and uninstall scripts.

### Task 1: Create the solution and public contracts

**Files:**
- Create: `RemoteController.sln`, `Directory.Build.props`, `src/Rc.Contracts/*`, `tests/Rc.Contracts.Tests/*`

- [x] Create .NET 8 projects for every executable and current test assemblies; enable nullable reference types, implicit usings, deterministic builds and analyzers in `Directory.Build.props`. Broker/UI test assemblies remain part of Tasks 8-9.
- [x] Define `ResultEnvelope<T>`, `RemoteError`, the fixed job state enum, `JobSnapshot`, byte-chunk DTOs, file-manifest DTOs, UI DTOs and the stable error-code enum in `Rc.Contracts`.
- [ ] Define the remaining UI service surface. Pairing, jobs and files currently use versioned JSON-over-TLS control DTOs rather than the originally planned gRPC transport; raw command and byte payloads are preserved.
- [x] Write contract serialization tests proving that success/error envelopes and Base64 byte chunks have stable camelCase JSON field names.
- [x] Run `dotnet test tests/Rc.Contracts.Tests` and commit the contract foundation (`543bd11`, with follow-up hardening commits).

### Task 2: Add secure configuration, state storage and quota handling

**Files:**
- Create: `src/Rc.Agent/Configuration/*`, `src/Rc.Agent/Persistence/*`, `tests/Rc.Agent.Tests/Persistence/*`

- [x] Add a strongly typed `AgentOptions` record with defaults: Windows 10/11 x64, normal-task limit 8, elevated-task limit 2, log quota 200 MB and cancellation grace 10 seconds; file root and transfer/write limits are environment-configurable.
- [x] Build SQLite migrations for device identity, paired controller, job snapshots, output segments, transfer sessions and audit events. Store log data in segment files under the protected data root; SQLite stores paths and offsets.
- [x] Store private keys and the configured execution-account secret with DPAPI; reject a data directory whose ACL permits untrusted users to write it.
- [x] Write tests that migrate an empty database, persist/reload a job snapshot, and evict the oldest completed logs while retaining running-task tails under a test quota.
- [x] Run persistence tests and commit `feat: add durable agent state and log quota` (`3b2c3ea`).

### Task 3: Implement identity, one-controller pairing and mTLS

**Files:**
- Create: `src/Rc.Agent/Security/*`, `src/Rc.Cli/Commands/PairCommand.cs`, `tests/Rc.Agent.Tests/Security/*`

- [x] Generate an agent device key and self-signed TLS certificate on first start; expose only its SHA-256 fingerprint through discovery.
- [ ] Implement the short-lived J-PAKE enrollment transcript and one-time-code flow. Agent/controller/endpoint/fingerprint binding and expiry are implemented; persistent audit/rate-limit completion remains pending.
- [x] On success, store and pin the single controller certificate. Authenticated control calls use a TLS-pinned ECDSA challenge session, with legacy per-request signatures retained for compatibility.
- [ ] `rcctl pair` and JSON output are implemented; the local-only `unpair` administrative command and active-session invalidation remain pending.
- [ ] Pairing, fingerprint and second-controller tests exist; complete expired/wrong-code audit/rate-limit coverage and the user-facing unpair/re-pair test after adding the command.
- [x] Run security tests and commit the one-controller pairing/TLS control plane (`0316064`, `adf22ac`).

### Task 4: Implement LAN discovery

**Files:**
- Create: `src/Rc.Agent/Discovery/*`, `src/Rc.Cli/Commands/DiscoverCommand.cs`, `tests/Rc.Agent.Tests/Discovery/*`

- [x] Publish a versioned UDP multicast announcement containing only device ID, display name, TCP port, protocol version and certificate fingerprint.
- [x] Implement `rcctl discover` with a bounded listen window, de-duplication by device ID and JSON rows sorted by display name.
- [x] Reject oversized, malformed and unsupported-version announcements; never treat discovery data as authentication.
- [x] Test payload redaction, round-trip decoding, duplicate suppression and the fallback manual pairing path.
- [x] Run discovery tests and commit `feat: add lan device discovery` (`534bddf`).

### Task 5: Build the independent task host and ConPTY output pipeline

**Files:**
- Create: `src/Rc.TaskHost/*`, `tests/Rc.TaskHost.Tests/*`

- [x] Add a `TaskLaunchRequest` file contract passed from agent to task host, containing argv/shell mode, working directory, environment, execution identity and job ID.
- [ ] Independent TaskHost processes, ordered stdout/stderr segment files and job-specific named-pipe control are implemented; true ConPTY/HPCON startup is still pending.
- [x] Track PID, start/end timestamps, exit code, last-output timestamp and CPU/memory counters without a kill-on-close job object, so a restarting agent does not terminate the task host.
- [ ] Cancellation and process-tree termination are implemented; true ConPTY Ctrl+C delivery followed by the configured grace period remains pending.
- [ ] TaskHost tests cover command execution, stdin/EOF, output offsets, cancellation and failed starts; ConPTY-specific Ctrl+C behavior remains pending.
- [x] Run `dotnet test tests/Rc.TaskHost.Tests` on Windows and commit `feat: add durable interactive task host` (`5539f6c`, `7090feb`).

### Task 6: Add scheduling, recovery and job RPCs

**Files:**
- Create: `src/Rc.Agent/Jobs/*`, `src/Rc.Agent/Grpc/JobsService.cs`, `tests/Rc.Agent.Tests/Jobs/*`

- [ ] Normal-job bounded scheduling and queued snapshots are implemented. The separate elevated queue/limit remains pending with the Broker.
- [x] On Agent start, locate live TaskHosts through registered local pipes, reconnect and repair snapshots; mark unrecoverable running/queued jobs `interrupted_by_reboot` without relaunching.
- [x] Implement versioned JSON-over-TLS `Exec`, `ListJobs`, `GetJob`, `ReadLogs(afterOffset)`, follow/retry, `WriteStdin`, `CloseStdin`, `WaitJob` and `CancelJob` control operations.
- [ ] Lifecycle transitions are persisted before reporting; complete durable audit-event emission and audit verification.
- [ ] Tests cover normal queueing, reconnect/recovery, reboot interruption and cancellation. Elevated concurrency and full duplicate-cancellation/audit coverage remain pending.
- [x] Job and session tests pass; expanded scheduler/session work committed in `6be9ca6`.

### Task 7: Implement resumable file and directory transfer

**Files:**
- Create: `src/Rc.Agent/Files/*`, `src/Rc.Agent/Grpc/FilesService.cs`, `tests/Rc.Agent.Tests/Files/*`

- [x] Implement safe path resolution for the configured file root; reject device names, traversal outside the root and access through reparse points.
- [x] Implement `fs list`, `stat`, byte-range `read`, and temporary-file/atomic-replace `write`, with a configurable atomic-write limit.
- [x] Implement persisted transfer sessions with fixed-size chunks, per-chunk SHA-256 receipts, full-file SHA-256 verification, resumable offsets, cleanup expiry and configurable transfer/chunk limits.
- [x] Recursively expand directory manifests using normalized relative paths; preserve content, files and empty-directory structure only, not ACLs or ownership.
- [x] Test interrupted upload persistence/resume, download range resume behavior, altered chunk and persisted-part rejection, final hash verification, atomic-write safety/limits, empty directories, expiry and traversal rejection; add an authenticated TLS file-control integration test.
- [x] File and full-solution tests pass locally (177 total on 2026-07-12); the project-local temporary SDK was removed after verification, and the implementation was committed in `6be9ca6`.

### Task 8: Add the explicit elevated execution broker

**Files:**
- Create: `src/Rc.PrivilegedBroker/*`, `src/Rc.Agent/Elevation/*`, `tests/Rc.PrivilegedBroker.Tests/*`

- [ ] Register the broker as a distinct privileged Windows service. Its named pipe ACL must allow only the configured agent service identity; validate a per-installation request MAC in addition to the pipe ACL.
- [ ] Accept only a signed task launch/cancel/status message and return a broker task-host registration; never expose a TCP listener from the privileged broker.
- [ ] Route `exec --elevated` through the broker and all other commands through the normal scheduler. Record the selected identity in job metadata and audit events.
- [ ] Test that a non-agent local process cannot connect, an invalid MAC is rejected, ordinary execution never invokes the broker, and elevated invocation returns a visible elevated identity.
- [ ] Run broker tests under an administrative Windows test environment and commit `feat: add explicit elevated task broker`.

### Task 9: Implement the GUI session agent

**Files:**
- Create: `src/Rc.UiAgent/*`, `src/Rc.Agent/Ui/*`, `tests/Rc.UiAgent.Tests/*`

- [ ] Install a per-user logon task for the chosen interactive account. The UI agent registers session ID, displays and capability version with `rc-agent` through a local named pipe.
- [ ] Implement multi-display and window enumeration, target-window/display screenshots, UI Automation snapshots, activate/minimize/maximize/restore/move/close, mouse, keyboard, text, shortcuts and clipboard operations.
- [ ] Require a valid active session and explicit display/window target for screenshot and input; map closed windows, detached displays and no-session conditions to documented error codes.
- [ ] Use Windows-MCP as a behavioral reference for UI Automation coverage, but retain all implementation in .NET and disable any telemetry.
- [ ] Test serialization and target validation in unit tests; add Windows integration tests that automate a disposable test window on two virtual/physical displays when available.
- [ ] Run UI tests and commit `feat: add optional interactive gui session agent`.

### Task 10: Build the JSON-first CLI and installation workflow

**Files:**
- Create: `src/Rc.Cli/*`, `installer/Install.ps1`, `installer/Uninstall.ps1`, `tests/Rc.Cli.Tests/*`

- [ ] Implement all approved command groups: discover/pair, exec, job, fs/copy and ui. Route direct argv and explicit shell calls to distinct request DTOs.
- [ ] Make every successful command emit one JSON Line with `ok: true`; make every failure emit `ok: false` with a stable error code and set a nonzero process exit code. Add `--text` renderers without changing DTOs.
- [ ] Render `job logs --follow --text` as a local terminal stream; JSON mode must return raw Base64 chunks, stream name and next byte offset.
- [ ] Install normal agent and privileged broker services, create the selected-user UI logon task, configure firewall rules for the explicit LAN port, and provide idempotent uninstall/repair paths.
- [ ] Test CLI argument parsing, envelope shape, agent-unavailable error, stable exit codes and installation-script dry-run/what-if behavior.
- [ ] Publish self-contained single-file x64 executables and commit `feat: ship json cli and windows installer`.

### Task 11: Document, validate and package the first release

**Files:**
- Create: `README.md`, `docs/security.md`, `docs/protocol.md`, `docs/operations.md`, `.github/workflows/ci.yml`

- [ ] Document first installation, normal-account configuration, discovery pairing, manual `IP:port + code` pairing, unpairing, logs quota, elevation, transfer resume and GUI-session limitations.
- [ ] Document threat boundaries: trusted single controller, LAN discovery is unauthenticated, PAKE/mTLS protects control traffic, UAC desktop is unsupported, and reboot does not resume arbitrary jobs.
- [ ] Add CI for restore, build, unit tests, contract tests and Windows integration-test gating; fail release packaging unless every self-contained binary is present.
- [ ] Run an end-to-end two-VM acceptance scenario: pair, start two concurrent Python/pip commands, stream output, write stdin, reconnect, transfer/resume a directory, run an elevated command, login and control a test GUI window, then verify audit/log quota behavior.
- [ ] Commit `docs: document remote controller setup and operation` and tag the first tested Windows x64 release.

## Final acceptance checks

- [ ] An AI agent can parse every CLI result without screen scraping.
- [ ] A paired controller can disconnect/reconnect without killing a running job and can resume output from a byte offset.
- [ ] A reboot never silently replays a command.
- [ ] An unpaired LAN host cannot call control RPCs or attach to privileged/UI local IPC.
- [ ] A file/directory transfer resumes after interruption and rejects altered content.
- [ ] GUI operations only work in the configured logged-in user session and correctly report unavailability otherwise.
