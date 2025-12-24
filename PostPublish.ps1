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
    Invoke-Command -ComputerName $RemoteServer -ScriptBlock {
        Restart-WebAppPool -Name "DevApp"
    }
    Write-Host "App pool recycled"
} else {
    Write-Host "ERROR: appconfig.json not found at $localConfig" -ForegroundColor Red
    exit 1
}

Write-Host "=== Post-Publish Complete ===" -ForegroundColor Green
