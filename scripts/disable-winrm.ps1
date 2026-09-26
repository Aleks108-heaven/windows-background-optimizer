#Requires -RunAsAdministrator
# Turns off WinRM / PowerShell Remoting (ports 5985, 47001). Undo with enable-winrm.ps1.
Disable-PSRemoting -Force
Stop-Service WinRM -Force
Set-Service WinRM -StartupType Disabled
Write-Host "WinRM disabled. Status:" (Get-Service WinRM).Status
