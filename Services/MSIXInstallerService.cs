using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DevApp.Services
{
    public class MSIXInstallerService
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<MSIXInstallerService> _logger;

        public MSIXInstallerService(
            IConfiguration config,
            IWebHostEnvironment env,
            ILogger<MSIXInstallerService> logger)
        {
            _config = config;
            _env = env;
            _logger = logger;
        }

        public class MinorVersionInfo
        {
            public string AppName { get; set; } = string.Empty;
            public string Environment { get; set; } = string.Empty;
            public string MinorVersion { get; set; } = string.Empty; // e.g., "1.8"
            public string LatestFullVersion { get; set; } = string.Empty; // e.g., "1.8.3.0"
            public string MsixFileName { get; set; } = string.Empty;
            public string MsixPath { get; set; } = string.Empty;
            public string? BaseUrl { get; set; }
        }

        /// <summary>
        /// Scans staging directory and generates/updates .appinstaller files for each minor version
        /// Structure: wwwroot/Server/MSIX/{MinorVersion}/{AppName}_{Environment}.appinstaller
        /// </summary>
        public void GenerateStaticInstallers(string? baseUrl = null)
        {
            try
            {
                var stagingPath = _config["StagingPath"];
                if (string.IsNullOrWhiteSpace(stagingPath))
                {
                    _logger.LogError("StagingPath not configured");
                    return;
                }

                // Get MSIX product configuration
                var productsSection = _config.GetSection("Products");
                var products = productsSection.Get<List<ProductConfig>>();
                var msixProducts = products?.Where(p => p.Type == "MSIX").ToList();

                if (msixProducts == null || msixProducts.Count == 0)
                {
                    _logger.LogWarning("No MSIX products configured");
                    return;
                }

                foreach (var product in msixProducts)
                {
                    ProcessMSIXProduct(product, stagingPath, baseUrl);
                }

                _logger.LogInformation("Static installer generation completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate static installers");
            }
        }

        private void ProcessMSIXProduct(ProductConfig product, string stagingPath, string? baseUrl)
        {
            var productPath = Path.Combine(stagingPath, product.SubFolder);
            
            if (!Directory.Exists(productPath))
            {
                _logger.LogWarning("Product path does not exist: {Path}", productPath);
                return;
            }

            // Get environment directories
            var envDirs = Directory.GetDirectories(productPath);
            
            foreach (var envDir in envDirs)
            {
                var envName = Path.GetFileName(envDir);
                if (string.IsNullOrWhiteSpace(envName)) continue;

                ProcessEnvironment(product, envDir, envName, baseUrl);
            }
        }

        private void ProcessEnvironment(ProductConfig product, string envDir, string envName, string? baseUrl)
        {
            // Get all MSIX files in this environment
            var msixFiles = Directory.GetFiles(envDir, "*.msix", SearchOption.TopDirectoryOnly);
            
            if (msixFiles.Length == 0)
            {
                _logger.LogInformation("No MSIX files found in {EnvDir}", envDir);
                return;
            }

            // Group by minor version (e.g., 1.8, 1.9, 1.10)
            var versionGroups = msixFiles
                .Select(f => new
                {
                    FilePath = f,
                    FileName = Path.GetFileNameWithoutExtension(f),
                    Version = ExtractVersion(Path.GetFileNameWithoutExtension(f))
                })
                .Where(x => x.Version != null)
                .GroupBy(x => GetMinorVersion(x.Version!))
                .ToList();

            foreach (var group in versionGroups)
            {
                var minorVersion = group.Key;
                
                // Get the latest patch version within this minor version
                var latest = group
                    .OrderByDescending(x => x.Version)
                    .FirstOrDefault();

                if (latest == null) continue;

                var info = new MinorVersionInfo
                {
                    AppName = product.Name,
                    Environment = envName,
                    MinorVersion = minorVersion,
                    LatestFullVersion = latest.Version.ToString(),
                    MsixFileName = Path.GetFileName(latest.FilePath),
                    MsixPath = latest.FilePath,
                    BaseUrl = baseUrl
                };

                GenerateAppInstallerFile(info);
            }
        }

        private void GenerateAppInstallerFile(MinorVersionInfo info)
        {
            try
            {
                // Get the physical path on the IIS server (e.g., C:\inetpub\wwwroot)
                var msixPhysicalPath = _config["MSIXPhysicalPath"];
                
                if (string.IsNullOrWhiteSpace(msixPhysicalPath))
                {
                    _logger.LogError("MSIXPhysicalPath not configured in appconfig.json");
                    return;
                }

                // Create directory structure on IIS server: C:\inetpub\wwwroot\MSIX\{MinorVersion}\
                var staticDir = Path.Combine(msixPhysicalPath, "MSIX", info.MinorVersion);
                Directory.CreateDirectory(staticDir);

                // NOTE: NOT copying MSIX file - it remains on network share (\\csdvnas\builds\Staging\WinMobile)
                // The MSIX file will be served from a separate virtual directory mapped to the network share

                // Generate .appinstaller filename: {AppName}_{Environment}.appinstaller
                var appInstallerFileName = $"{info.AppName}_{info.Environment}.appinstaller";
                var appInstallerPath = Path.Combine(staticDir, appInstallerFileName);

                // Generate .appinstaller content
                var appInstallerContent = GenerateAppInstallerXml(info);
                File.WriteAllText(appInstallerPath, appInstallerContent);

                _logger.LogInformation(
                    "Generated static installer: {AppName} {Env} v{Version} -> {Path}",
                    info.AppName, info.Environment, info.MinorVersion, appInstallerPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate appinstaller for {AppName} {Env} {Version}",
                    info.AppName, info.Environment, info.MinorVersion);
            }
        }

        private string GenerateAppInstallerXml(MinorVersionInfo info)
        {
            // Get the MSIX server URL from config - use current request or fallback
            var msixServerUrl = _config["MSIXServerUrl"];
            
            if (string.IsNullOrWhiteSpace(msixServerUrl))
            {
                _logger.LogWarning("MSIXServerUrl not configured, using baseUrl from request");
                msixServerUrl = info.BaseUrl ?? "http://localhost:5000";
            }
            
            // Get MSIX files base URL - separate from server URL if needed
            var msixFilesBaseUrl = _config["MSIXFilesBaseUrl"];
            if (string.IsNullOrWhiteSpace(msixFilesBaseUrl))
            {
                // Default: MSIX files are in /MSIXFiles/{MinorVersion}/{Environment}/
                msixFilesBaseUrl = $"{msixServerUrl}/MSIXFiles";
            }
            
            // URLs for .appinstaller and MSIX files
            var appInstallerUrl = $"{msixServerUrl}/MSIX/{info.MinorVersion}/{info.AppName}_{info.Environment}.appinstaller";
            var msixUrl = $"{msixFilesBaseUrl}/{info.MinorVersion}/{info.Environment}/{info.MsixFileName}";

            // Parse version (use latest full version)
            var version = info.LatestFullVersion;
            if (!version.Contains('.'))
            {
                version = $"{version}.0.0.0";
            }
            else
            {
                var parts = version.Split('.');
                while (parts.Length < 4)
                {
                    version += ".0";
                }
            }

            var packageName = info.AppName.Replace(" ", "");
            var publisher = "CN=YourCompany"; // TODO: Extract from MSIX package

            var xml = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(XName.Get("AppInstaller", "http://schemas.microsoft.com/appx/appinstaller/2018"),
                    new XAttribute("Uri", appInstallerUrl),
                    new XAttribute("Version", version),
                    new XElement(XName.Get("MainPackage", "http://schemas.microsoft.com/appx/appinstaller/2018"),
                        new XAttribute("Name", packageName),
                        new XAttribute("Publisher", publisher),
                        new XAttribute("Version", version),
                        new XAttribute("Uri", msixUrl),
                        new XAttribute("ProcessorArchitecture", "x64")
                    ),
                    new XElement(XName.Get("UpdateSettings", "http://schemas.microsoft.com/appx/appinstaller/2018"),
                        new XElement(XName.Get("OnLaunch", "http://schemas.microsoft.com/appx/appinstaller/2018"),
                            new XAttribute("HoursBetweenUpdateChecks", "0")
                        )
                    )
                )
            );

            return xml.ToString();
        }

        private Version? ExtractVersion(string fileName)
        {
            // Try to extract version from filename (e.g., "WinMobile_1.8.3.0" -> "1.8.3.0")
            var match = Regex.Match(fileName, @"(\d+)\.(\d+)\.(\d+)\.(\d+)");
            if (match.Success && Version.TryParse(match.Value, out var version))
            {
                return version;
            }

            // Try shorter versions
            match = Regex.Match(fileName, @"(\d+)\.(\d+)\.(\d+)");
            if (match.Success && Version.TryParse(match.Value + ".0", out version))
            {
                return version;
            }

            match = Regex.Match(fileName, @"(\d+)\.(\d+)");
            if (match.Success && Version.TryParse(match.Value + ".0.0", out version))
            {
                return version;
            }

            return null;
        }

        private string GetMinorVersion(Version version)
        {
            // Return major.minor (e.g., "1.8" from "1.8.3.0")
            return $"{version.Major}.{version.Minor}";
        }

        private class ProductConfig
        {
            public string Name { get; set; } = string.Empty;
            public string SubFolder { get; set; } = string.Empty;
            public string Type { get; set; } = "Standard";
            public bool RequiresEnvironment { get; set; }
            public List<AppConfig> Apps { get; set; } = new();
        }

        private class AppConfig
        {
            public string Name { get; set; } = string.Empty;
            public string Exe { get; set; } = string.Empty;
        }
    }
}
