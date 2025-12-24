# Setup Script for BuildCST IIS Server
# Run this script ON BuildCST server (via RDP)

Write-Host "=== DevApp IIS Setup for BuildCST ===" -ForegroundColor Cyan

# Import IIS modules properly
Import-Module WebAdministration -ErrorAction SilentlyContinue
Import-Module IISAdministration -ErrorAction SilentlyContinue

# 1. Create DevApp Application Pool
Write-Host "`n1. Creating DevApp application pool..." -ForegroundColor Yellow
if (!(Get-IISAppPool -Name "DevApp" -ErrorAction SilentlyContinue)) {
    New-WebAppPool -Name "DevApp"
    Write-Host "   ✓ Created DevApp app pool" -ForegroundColor Green
} else {
    Write-Host "   ✓ DevApp app pool already exists" -ForegroundColor Green
}

# 2. Configure app pool identity
Write-Host "`n2. Configuring app pool to run as CS-DEV\slarab..." -ForegroundColor Yellow

# Prompt for password
$cred = Get-Credential -UserName "CS-DEV\slarab" -Message "Enter password for app pool identity"
$password = $cred.GetNetworkCredential().Password

# Use appcmd to set identity
$appcmd = "$env:SystemRoot\System32\inetsrv\appcmd.exe"
& $appcmd set config /section:applicationPools "/[name='DevApp'].processModel.identityType:SpecificUser" "/commit:apphost"
& $appcmd set config /section:applicationPools "/[name='DevApp'].processModel.userName:CS-DEV\slarab" "/commit:apphost"
& $appcmd set config /section:applicationPools "/[name='DevApp'].processModel.password:$password" "/commit:apphost"

Write-Host "   ✓ App pool configured to run as CS-DEV\slarab" -ForegroundColor Green

# 3. Set app pool settings
Write-Host "`n3. Configuring app pool settings..." -ForegroundColor Yellow
& $appcmd set apppool "DevApp" /processModel.idleTimeout:00:20:00
& $appcmd set apppool "DevApp" /recycling.periodicRestart.time:00:00:00
Write-Host "   ✓ Idle timeout: 20 minutes" -ForegroundColor Green
Write-Host "   ✓ Periodic restart: Disabled" -ForegroundColor Green

# 4. Create physical directory
Write-Host "`n4. Creating physical directory..." -ForegroundColor Yellow
$physicalPath = "C:\inetpub\wwwroot\DevApp"
if (!(Test-Path $physicalPath)) {
    New-Item -Path $physicalPath -ItemType Directory -Force | Out-Null
    Write-Host "   ✓ Created: $physicalPath" -ForegroundColor Green
} else {
    Write-Host "   ✓ Directory exists: $physicalPath" -ForegroundColor Green
}

# 5. Create logs directory
$logsPath = "$physicalPath\logs"
if (!(Test-Path $logsPath)) {
    New-Item -Path $logsPath -ItemType Directory -Force | Out-Null
    Write-Host "   ✓ Created logs directory" -ForegroundColor Green
}

# 6. Create IIS Application
Write-Host "`n5. Creating IIS application..." -ForegroundColor Yellow
$existingApp = Get-WebApplication -Site "Default Web Site" -Name "DevApp" -ErrorAction SilentlyContinue
if (!$existingApp) {
    New-WebApplication -Site "Default Web Site" -Name "DevApp" -PhysicalPath $physicalPath -ApplicationPool "DevApp"
    Write-Host "   ✓ Created DevApp application under Default Web Site" -ForegroundColor Green
} else {
    # Update existing app to use correct app pool
    & $appcmd set app "Default Web Site/DevApp" /applicationPool:DevApp
    Write-Host "   ✓ DevApp application configured" -ForegroundColor Green
}

# 7. Grant NAS permissions
Write-Host "`n6. Granting NAS permissions..." -ForegroundColor Yellow
$computerAccount = "CS-DEV\BuildCST$"
$nasPath = "\\csdvnas\builds\Staging\CSTRetail"

try {
    $acl = Get-Acl $nasPath
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $computerAccount, 
        "ReadAndExecute", 
        "ContainerInherit,ObjectInherit", 
        "None", 
        "Allow"
    )
    $acl.AddAccessRule($rule)
    Set-Acl -Path $nasPath -AclObject $acl
    Write-Host "   ✓ Granted $computerAccount access to NAS" -ForegroundColor Green
} catch {
    Write-Host "   ! Could not grant NAS permissions automatically" -ForegroundColor Yellow
    Write-Host "   ! Manually grant BuildCST$ read access to $nasPath" -ForegroundColor Yellow
}

# 8. Start app pool
Write-Host "`n7. Starting app pool..." -ForegroundColor Yellow
Start-WebAppPool -Name "DevApp"
Write-Host "   ✓ App pool started" -ForegroundColor Green

Write-Host "`n=== Setup Complete! ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Configuration Summary:" -ForegroundColor White
Write-Host "  App Pool: DevApp (Running as CS-DEV\slarab)" -ForegroundColor Gray
Write-Host "  Physical Path: $physicalPath" -ForegroundColor Gray
Write-Host "  URL: http://BuildCST/DevApp" -ForegroundColor Gray
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor White
Write-Host "  1. Verify .NET 8 Hosting Bundle is installed" -ForegroundColor Gray
Write-Host "  2. Publish from Visual Studio using 'BuildCST' profile" -ForegroundColor Gray
Write-Host "  3. Access http://BuildCST/DevApp" -ForegroundColor Gray
Write-Host ""
