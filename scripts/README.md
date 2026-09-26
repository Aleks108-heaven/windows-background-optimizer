# Hardening scripts

Optional Windows hardening scripts. Run each one from an **Administrator** terminal:

```powershell
cd "<path-to-repo>\scripts"
powershell -ExecutionPolicy Bypass -File .\disable-winrm.ps1
```

| Script | What it does |
|---|---|
| `disable-winrm.ps1` | Turns off PowerShell Remoting and stops WinRM (ports 5985 and 47001). |
| `enable-winrm.ps1` | Undoes the WinRM change. |
| `block-smb-inbound.ps1` | Adds a firewall rule that blocks other machines from connecting to ports 139 and 445. Your own access to a NAS or shared folders keeps working. |
| `unblock-smb-inbound.ps1` | Removes that firewall rule. |

Verify WinRM is off: `Get-Service WinRM | Select Status,StartType` should show `Stopped` / `Disabled`, and
`Get-NetTCPConnection -State Listen | Where LocalPort -in 5985,47001` should return nothing.

Docker Desktop does not depend on WinRM or inbound SMB.
