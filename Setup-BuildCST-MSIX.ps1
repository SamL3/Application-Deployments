# Setup MSIX configuration on BuildCST
# Run this script as Administrator on BuildCST server

Write-Host "Setting up MSIX configuration on BuildCST..." -ForegroundColor Cyan
Write-Host ""

# Import IIS module and provider
Write-Host "Loading IIS module..." -ForegroundColor Yellow
Import-Module WebAdministration -ErrorAction Stop

# Initialize the IIS provider by accessing it
$null = Get-PSDrive -PSProvider WebAdministration -ErrorAction SilentlyContinue
if (!(Test-Path "IIS:\")) {
    Write-Host "   IIS provider not available, trying alternative approach..." -ForegroundColor Yellow
    $useAlternative = $true
} else {
    Write-Host "   ✓ IIS provider loaded" -ForegroundColor Green
    $useAlternative = $false
}

# 1. Create MSIX directory
Write-Host "1. Creating MSIX directory..." -ForegroundColor Yellow
$msixPath = "C:\inetpub\wwwroot\MSIX"
if (!(Test-Path $msixPath)) {
    New-Item -Path $msixPath -ItemType Directory -Force | Out-Null
    Write-Host "   ✓ Created: $msixPath" -ForegroundColor Green
} else {
    Write-Host "   ✓ Already exists: $msixPath" -ForegroundColor Green
}

# 2. Grant AppPool write permissions
Write-Host "`n2. Granting AppPool write permissions..." -ForegroundColor Yellow
$acl = Get-Acl $msixPath
$identity = "IIS AppPool\DefaultAppPool"
$fileSystemRights = [System.Security.AccessControl.FileSystemRights]"Modify"
$inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit,ObjectInherit"
$propagationFlags = [System.Security.AccessControl.PropagationFlags]"None"
$accessControlType = [System.Security.AccessControl.AccessControlType]"Allow"
$accessRule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity, $fileSystemRights, $inheritanceFlags, $propagationFlags, $accessControlType)
$acl.SetAccessRule($accessRule)
Set-Acl -Path $msixPath -AclObject $acl
Write-Host "   ✓ Granted Modify permissions to $identity" -ForegroundColor Green

# 3. Create MSIX virtual directory
Write-Host "`n3. Creating MSIX virtual directory..." -ForegroundColor Yellow
$siteName = "Default Web Site"
$vdirName = "MSIX"
$existing = Get-WebVirtualDirectory -Site $siteName -Name $vdirName -ErrorAction SilentlyContinue
if (!$existing) {
    New-WebVirtualDirectory -Site $siteName -Name $vdirName -PhysicalPath $msixPath | Out-Null
    Write-Host "   ✓ Created MSIX virtual directory" -ForegroundColor Green
} else {
    Write-Host "   ✓ MSIX virtual directory already exists" -ForegroundColor Green
}

# Enable directory browsing
Set-WebConfigurationProperty -Filter "/system.webServer/directoryBrowse" -Name "enabled" -Value $true -PSPath "IIS:\Sites\$siteName\$vdirName" -ErrorAction SilentlyContinue

# 4. Add MIME types
Write-Host "`n4. Adding MIME types..." -ForegroundColor Yellow

# .msix MIME type
try {
    $existing = Get-WebConfigurationProperty -PSPath "IIS:\" -Filter "//staticContent/mimeMap[@fileExtension='.msix']" -Name "mimeType" -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "   ✓ .msix MIME type already exists" -ForegroundColor Green
    } else {
        Add-WebConfigurationProperty -PSPath "IIS:\" -Filter "//staticContent" -Name "." -Value @{fileExtension='.msix'; mimeType='application/msix'}
        Write-Host "   ✓ Added .msix MIME type" -ForegroundColor Green
    }
} catch {
    Write-Host "   ⚠ .msix MIME type: $($_.Exception.Message)" -ForegroundColor Yellow
}

# .appinstaller MIME type
try {
    $existing = Get-WebConfigurationProperty -PSPath "IIS:\" -Filter "//staticContent/mimeMap[@fileExtension='.appinstaller']" -Name "mimeType" -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "   ✓ .appinstaller MIME type already exists" -ForegroundColor Green
    } else {
        Add-WebConfigurationProperty -PSPath "IIS:\" -Filter "//staticContent" -Name "." -Value @{fileExtension='.appinstaller'; mimeType='application/appinstaller+xml'}
        Write-Host "   ✓ Added .appinstaller MIME type" -ForegroundColor Green
    }
} catch {
    Write-Host "   ⚠ .appinstaller MIME type: $($_.Exception.Message)" -ForegroundColor Yellow
}

# 5. Set AppPool identity
Write-Host "`n5. Setting AppPool identity..." -ForegroundColor Yellow
if ($useAlternative) {
    # Use appcmd instead
    $result = & "$env:SystemRoot\system32\inetsrv\appcmd.exe" list apppool "DefaultAppPool" /text:processModel.identityType
    if ($result -ne "ApplicationPoolIdentity") {
        & "$env:SystemRoot\system32\inetsrv\appcmd.exe" set apppool "DefaultAppPool" /processModel.identityType:ApplicationPoolIdentity
        Write-Host "   ✓ Set to ApplicationPoolIdentity" -ForegroundColor Green
    } else {
        Write-Host "   ✓ Already set to ApplicationPoolIdentity" -ForegroundColor Green
    }
} else {
    $appPool = Get-Item "IIS:\AppPools\DefaultAppPool"
    if ($appPool.processModel.identityType -ne 4) {
        Set-ItemProperty "IIS:\AppPools\DefaultAppPool" -Name processModel.identityType -Value 4
        Write-Host "   ✓ Set to ApplicationPoolIdentity" -ForegroundColor Green
    } else {
        Write-Host "   ✓ Already set to ApplicationPoolIdentity" -ForegroundColor Green
    }
}

# 6. Restart IIS
Write-Host "`n6. Restarting IIS..." -ForegroundColor Yellow
iisreset /restart | Out-Null
Start-Sleep -Seconds 5
Write-Host "   ✓ IIS restarted" -ForegroundColor Green

Write-Host "`n" -ForegroundColor White
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host "✓ BuildCST MSIX configuration complete!" -ForegroundColor Green
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host ""
Write-Host "MSIX installs should now work on BuildCST" -ForegroundColor Cyan
Write-Host "Test at: https://BuildCST/DevApp/Deploy" -ForegroundColor White
