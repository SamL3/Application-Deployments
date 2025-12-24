# Web Deploy Setup for BuildCST
# Run this script ON BuildCST server (via RDP) as Administrator

Write-Host "=== Web Deploy Setup for BuildCST ===" -ForegroundColor Cyan

# 1. Check if Web Deploy is installed
Write-Host "`n1. Checking Web Deploy installation..." -ForegroundColor Yellow
$webDeployPath = "C:\Program Files\IIS\Microsoft Web Deploy V3"
if (Test-Path $webDeployPath) {
    Write-Host "   ✓ Web Deploy is already installed" -ForegroundColor Green
} else {
    Write-Host "   ✗ Web Deploy NOT installed" -ForegroundColor Red
    Write-Host "   Download from: https://www.microsoft.com/en-us/download/details.aspx?id=43717" -ForegroundColor Yellow
    Write-Host "   Or run: choco install webdeploy" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "   After installing, re-run this script." -ForegroundColor Yellow
    exit 1
}

# 2. Check if Web Management Service is installed
Write-Host "`n2. Checking Web Management Service..." -ForegroundColor Yellow
$wmsvc = Get-Service -Name "WMSvc" -ErrorAction SilentlyContinue
if (!$wmsvc) {
    Write-Host "   ✗ Web Management Service NOT installed" -ForegroundColor Red
    Write-Host "   Installing via Server Manager..." -ForegroundColor Yellow
    
    # Install Management Service
    Install-WindowsFeature -Name Web-Mgmt-Service
    
    Write-Host "   ✓ Web Management Service installed" -ForegroundColor Green
} else {
    Write-Host "   ✓ Web Management Service is installed" -ForegroundColor Green
}

# 3. Configure Web Management Service
Write-Host "`n3. Configuring Web Management Service..." -ForegroundColor Yellow

# Enable remote connections
Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\WebManagement\Server" -Name "EnableRemoteManagement" -Value 1

# Start the service
Start-Service WMSvc -ErrorAction SilentlyContinue
Set-Service WMSvc -StartupType Automatic

Write-Host "   ✓ Remote management enabled" -ForegroundColor Green
Write-Host "   ✓ Service started and set to automatic" -ForegroundColor Green

# 4. Configure Firewall
Write-Host "`n4. Configuring Windows Firewall..." -ForegroundColor Yellow
$firewallRule = Get-NetFirewallRule -DisplayName "Web Management Service (HTTPS-In)" -ErrorAction SilentlyContinue
if (!$firewallRule) {
    New-NetFirewallRule -DisplayName "Web Management Service (HTTPS-In)" -Direction Inbound -Protocol TCP -LocalPort 8172 -Action Allow
    Write-Host "   ✓ Firewall rule created for port 8172" -ForegroundColor Green
} else {
    Write-Host "   ✓ Firewall rule already exists" -ForegroundColor Green
}

# 5. Create SSL Certificate for Web Deploy
Write-Host "`n5. Checking SSL certificate..." -ForegroundColor Yellow
$cert = Get-ChildItem -Path "Cert:\LocalMachine\My" | Where-Object { $_.Subject -like "*BuildCST*" } | Select-Object -First 1
if (!$cert) {
    Write-Host "   Creating self-signed certificate..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -DnsName "BuildCST" -CertStoreLocation "Cert:\LocalMachine\My"
    Write-Host "   ✓ Certificate created: $($cert.Thumbprint)" -ForegroundColor Green
} else {
    Write-Host "   ✓ Certificate exists: $($cert.Thumbprint)" -ForegroundColor Green
}

# 6. Trust the certificate
Write-Host "`n6. Trusting certificate..." -ForegroundColor Yellow
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
$store.Open("ReadWrite")
$store.Add($cert)
$store.Close()
Write-Host "   ✓ Certificate trusted" -ForegroundColor Green

# 7. Verify service status
Write-Host "`n7. Verifying services..." -ForegroundColor Yellow
$wmsvc = Get-Service -Name "WMSvc"
Write-Host "   Web Management Service: $($wmsvc.Status)" -ForegroundColor $(if ($wmsvc.Status -eq "Running") { "Green" } else { "Red" })

$msdepsvc = Get-Service -Name "MsDepSvc" -ErrorAction SilentlyContinue
if ($msdepsvc) {
    Write-Host "   Web Deployment Agent: $($msdepsvc.Status)" -ForegroundColor $(if ($msdepsvc.Status -eq "Running") { "Green" } else { "Yellow" })
}

Write-Host "`n=== Web Deploy Setup Complete! ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Web Deploy is now configured on BuildCST" -ForegroundColor White
Write-Host "Publish URL: https://BuildCST:8172/msdeploy.axd" -ForegroundColor Gray
Write-Host ""
Write-Host "Next step: Publish from Visual Studio using BuildCST profile" -ForegroundColor White
Write-Host ""
