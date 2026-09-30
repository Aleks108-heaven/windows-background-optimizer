#Requires -RunAsAdministrator
# Undo for disable-winrm.ps1.
Set-Service WinRM -StartupType Automatic
Start-Service WinRM
# No -SkipNetworkProfileCheck: with it, remoting is enabled even on Public networks (e.g. cafe Wi-Fi).
# Without it, this fails on a Public profile - switch the network to Private first if you really need it.
Enable-PSRemoting -Force
Write-Host "WinRM enabled. Status:" (Get-Service WinRM).Status
