# Post-Publish Script - Copy appconfig.json to remote server
param(
    [string]$RemoteServer = "PPCSUSWIIS1",
    [string]$RemotePath = "C:\inetpub\wwwroot\DevApp"
)

Write-Host "=== Post-Publish: Copying appconfig.json to $RemoteServer ==="

$localConfig = Join-Path $PSScriptRoot "appconfig.json"
$remoteUncPath = "\\$RemoteServer\C$\inetpub\wwwroot\DevApp\appconfig.json"

if (Test-Path $localConfig) {
    Write-Host "Copying $localConfig to $remoteUncPath"
    Copy-Item -Path $localConfig -Destination $remoteUncPath -Force
    Write-Host "Successfully copied appconfig.json"
    
    # Recycle app pool
    Write-Host "Recycling DevApp app pool on $RemoteServer..."
    try {
        Invoke-Command -ComputerName $RemoteServer -ScriptBlock {
            Restart-WebAppPool -Name "DevApp"
        } -ErrorAction Stop
        Write-Host "App pool recycled"
    } catch {
        Write-Host "Could not recycle app pool remotely (WinRM may not be enabled)"
        Write-Host "Please manually recycle the DevApp app pool on $RemoteServer"
        Write-Host "Or run: Restart-WebAppPool -Name 'DevApp' on $RemoteServer"
    }
} else {
    Write-Host "ERROR: appconfig.json not found at $localConfig" -ForegroundColor Red
    exit 1
}

Write-Host "=== Post-Publish Complete ===" -ForegroundColor Green
