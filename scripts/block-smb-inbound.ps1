#Requires -RunAsAdministrator
# Blocks OTHER machines from connecting to your SMB (139/445). Outbound (your access to NAS/shares) is unaffected.
# Undo with unblock-smb-inbound.ps1.
New-NetFirewallRule -DisplayName "BgOptimizer Block SMB Inbound" -Direction Inbound -Protocol TCP `
  -LocalPort 139,445 -Action Block -Profile Any | Out-Null
Write-Host "Inbound SMB blocked."
