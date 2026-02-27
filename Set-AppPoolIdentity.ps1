# Script to configure IIS Application Pool to use a domain account
# This allows the app pool to access network shares

param(
    [Parameter(Mandatory=$true)]
    [string]$AppPoolName = "DefaultAppPool",
    
    [Parameter(Mandatory=$true)]
    [string]$DomainUsername,  # e.g., "DOMAIN\username"
    
    [Parameter(Mandatory=$true)]
    [System.Security.SecureString]$Password
)

Write-Host "Configuring Application Pool: $AppPoolName" -ForegroundColor Cyan
Write-Host "Setting identity to: $DomainUsername" -ForegroundColor Cyan

Import-Module WebAdministration

try {
    # Convert secure string to plain text for IIS
    $BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    $PlainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)
    
    # Set the application pool identity to the domain account
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value "SpecificUser"
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.userName -Value $DomainUsername
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.password -Value $PlainPassword
    
    # Clear the password from memory
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($BSTR)
    
    Write-Host "✓ Application pool identity configured successfully" -ForegroundColor Green
    
    # Restart the app pool
    Write-Host "Restarting application pool..." -ForegroundColor Yellow
    Restart-WebAppPool -Name $AppPoolName
    
    Write-Host "✓ Application pool restarted" -ForegroundColor Green
    
    # Verify the settings
    $appPool = Get-Item "IIS:\AppPools\$AppPoolName"
    Write-Host "`nApplication Pool Settings:" -ForegroundColor Cyan
    Write-Host "  Name: $($appPool.Name)" -ForegroundColor Gray
    Write-Host "  Identity Type: $($appPool.processModel.identityType)" -ForegroundColor Gray
    Write-Host "  Username: $($appPool.processModel.userName)" -ForegroundColor Gray
    Write-Host "  State: $($appPool.State)" -ForegroundColor Gray
    
} catch {
    Write-Host "✗ Error configuring application pool: $_" -ForegroundColor Red
    exit 1
}

Write-Host "`n✓ Configuration complete!" -ForegroundColor Green
Write-Host "The application pool can now access network shares with the specified credentials." -ForegroundColor Green
