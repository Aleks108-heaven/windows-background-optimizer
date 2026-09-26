#Requires -RunAsAdministrator
# Undo for disable-winrm.ps1.
Set-Service WinRM -StartupType Automatic
Start-Service WinRM
Enable-PSRemoting -Force -SkipNetworkProfileCheck
Write-Host "WinRM enabled. Status:" (Get-Service WinRM).Status
