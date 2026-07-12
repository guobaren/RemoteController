# RemoteController

RemoteController is a Windows 10/11 LAN remote-control tool intended for one trusted controller and one managed endpoint. It provides authenticated command execution, persistent interactive jobs, resumable file transfer, explicit elevated execution, and optional desktop automation in a logged-in user session.

## Build and test

```powershell
dotnet restore Rc.RemoteController.sln -p:NuGetAudit=false
dotnet build Rc.RemoteController.sln --no-restore -warnaserror
dotnet test Rc.RemoteController.sln --no-build --no-restore -v minimal
```

Create the self-contained Windows x64 installer payload with:

```powershell
.\scripts\Publish-RemoteController.ps1
```

## Install and pair

Run an elevated PowerShell session on the managed endpoint. `-UiUser` identifies the interactive account that is allowed to run the UI session agent.

```powershell
.\scripts\Install-RemoteController.ps1 -SourcePath .\artifacts\publish -UiUser 'CONTOSO\alice'
```

The Agent advertises its certificate SHA-256 fingerprint at startup. On the controller, discover or probe the endpoint, then pair once using the locally displayed one-time code:

```powershell
rcctl discover --text
rcctl pair 192.168.1.50:43001 --fingerprint <SHA256> --name MyController --text
```

Every CLI command produces one JSON result envelope by default. Add `--text` for human-oriented rendering. See [operations](docs/operations.md), [protocol](docs/protocol.md), and [security](docs/security.md).

## Important limitations

- LAN discovery is unauthenticated; always pin the fingerprint supplied by the endpoint.
- A managed endpoint accepts exactly one paired controller until a local `rc-agent unpair` is performed.
- GUI automation operates only in the configured active login session. UAC secure desktop is deliberately unsupported.
- Reboot never resumes or replays arbitrary commands.
- Real two-machine/VM and elevated-install acceptance tests must be performed before a production release.
