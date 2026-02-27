# Script to setup IIS virtual directories for MSIX files on network share
# Run this on each IIS server (BuildCST, PPCSUSWIIS1, etc.)

param(
    [Parameter(Mandatory=$false)]
    [string]$NetworkSharePath = "\\csdvnas\builds\Staging\WinMobile",
    
    [Parameter(Mandatory=$false)]
    [string]$ServerName = $env:COMPUTERNAME
)

Write-Host "Setting up MSIX IIS configuration on $ServerName..." -ForegroundColor Cyan
Write-Host "Network share: $NetworkSharePath" -ForegroundColor Cyan

# Import IIS module
Import-Module WebAdministration -ErrorAction Stop

$siteName = "Default Web Site"
$vdirName = "MSIXFiles"

# Check if virtual directory already exists
$existingVDir = Get-WebVirtualDirectory -Site $siteName -Name $vdirName -ErrorAction SilentlyContinue

if ($null -ne $existingVDir) {
    Write-Host "Removing existing virtual directory: $vdirName" -ForegroundColor Yellow
    Remove-WebVirtualDirectory -Site $siteName -Name $vdirName
}

Write-Host "Creating virtual directory: $vdirName -> $NetworkSharePath" -ForegroundColor Green
New-WebVirtualDirectory -Site $siteName -Name $vdirName -PhysicalPath $NetworkSharePath

# Configure authentication for the virtual directory (allow anonymous access)
Write-Host "Configuring authentication for $vdirName..." -ForegroundColor Green

# Enable Anonymous Authentication
Set-WebConfigurationProperty -Filter "/system.webServer/security/authentication/anonymousAuthentication" `
    -Name "enabled" -Value $true -PSPath "IIS:\Sites\$siteName\$vdirName"

# Disable Windows Authentication (if you want)
# Set-WebConfigurationProperty -Filter "/system.webServer/security/authentication/windowsAuthentication" `
#     -Name "enabled" -Value $false -PSPath "IIS:\Sites\$siteName\$vdirName"

# Set MIME types for MSIX files
Write-Host "Configuring MIME types for MSIX files..." -ForegroundColor Green

$mimeTypes = @(
    @{Extension=".msix"; MimeType="application/msix"},
    @{Extension=".appinstaller"; MimeType="application/appinstaller+xml"}
)

foreach ($mime in $mimeTypes) {
    try {
        # Remove if exists
        Remove-WebConfigurationProperty -PSPath "IIS:\Sites\$siteName\$vdirName" `
            -Filter "system.webServer/staticContent" -Name "." `
            -AtElement @{fileExtension=$mime.Extension} -ErrorAction SilentlyContinue
        
        # Add MIME type
        Add-WebConfigurationProperty -PSPath "IIS:\Sites\$siteName\$vdirName" `
            -Filter "system.webServer/staticContent" -Name "." `
            -Value @{fileExtension=$mime.Extension; mimeType=$mime.MimeType}
        
        Write-Host "  Added MIME type: $($mime.Extension) -> $($mime.MimeType)" -ForegroundColor Gray
    } catch {
        Write-Host "  MIME type already exists: $($mime.Extension)" -ForegroundColor Gray
    }
}

Write-Host "`n✓ Setup complete!" -ForegroundColor Green
Write-Host "Virtual directory created: https://$ServerName/MSIXFiles" -ForegroundColor Green
Write-Host "MSIX files will be served from: $NetworkSharePath" -ForegroundColor Green

# Test access
Write-Host "`nTesting network share access..." -ForegroundColor Cyan
if (Test-Path $NetworkSharePath) {
    Write-Host "✓ Network share is accessible" -ForegroundColor Green
    $fileCount = (Get-ChildItem -Path $NetworkSharePath -Recurse -Filter "*.msix" -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "  Found $fileCount MSIX files" -ForegroundColor Gray
} else {
    Write-Host "✗ Warning: Network share is NOT accessible from this server!" -ForegroundColor Red
    Write-Host "  Make sure the IIS Application Pool identity has access to $NetworkSharePath" -ForegroundColor Yellow
}

Write-Host "`nIMPORTANT: Update appsettings.json with:" -ForegroundColor Yellow
Write-Host '  "MSIXFilesBaseUrl": "https://$ServerName/MSIXFiles"' -ForegroundColor White
