#Requires -RunAsAdministrator
# Undo for block-smb-inbound.ps1.
Remove-NetFirewallRule -DisplayName "BgOptimizer Block SMB Inbound" -ErrorAction SilentlyContinue
Write-Host "Inbound SMB block removed."
