using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DevApp.Services;

namespace DevApp.Pages
{
    public class InstallModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly ILogger<InstallModel> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly MSIXInstallerService _msixService;

        public InstallModel(IConfiguration config, ILogger<InstallModel> logger, IWebHostEnvironment env, MSIXInstallerService msixService)
        {
            _config = config;
            _logger = logger;
            _env = env;
            _msixService = msixService;
        }

        public string AppName { get; set; } = string.Empty;
        public string BuildName { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public string AppInstallerUrl { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }

        public IActionResult OnGet(string app, string build, string env)
        {
            AppName = app;
            BuildName = build;
            Environment = env;

            if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(build) || string.IsNullOrWhiteSpace(env))
            {
                ErrorMessage = "Missing required parameters (app, build, env).";
                return Page();
            }

            try
            {
                // Extract minor version from build (e.g., "CircaSports_1.9.671.156_x64" -> "1.9")
                var minorVersion = ExtractMinorVersion(build);
                if (string.IsNullOrEmpty(minorVersion))
                {
                    ErrorMessage = $"Cannot extract minor version from build: {build}";
                    return Page();
                }

                // Get the MSIX server URL - use current server instead of hardcoded config
                var request = HttpContext.Request;
                var msixServerUrl = $"{request.Scheme}://{request.Host}";
                
                _logger.LogInformation("Using MSIX server URL from current request: {Url}", msixServerUrl);

                // Check if static .appinstaller exists on the IIS server
                var msixPhysicalPath = _config["MSIXPhysicalPath"];
                
                if (string.IsNullOrWhiteSpace(msixPhysicalPath))
                {
                    ErrorMessage = "MSIXPhysicalPath not configured in appconfig.json";
                    return Page();
                }

                var staticPath = Path.Combine(msixPhysicalPath, "MSIX", minorVersion, $"{app}_{env}.appinstaller");
                
                if (!System.IO.File.Exists(staticPath))
                {
                    _logger.LogWarning("Static .appinstaller not found at {Path}. Creating on-demand...", staticPath);
                    
                    try
                    {
                        // Get staging path
                        var stagingPath = _config["StagingPath"];
                        if (string.IsNullOrWhiteSpace(stagingPath))
                        {
                            ErrorMessage = "StagingPath not configured";
                            return Page();
                        }
                        
                        // Find the MSIX file in staging: StagingPath\WinMobile\{minorVersion}\{env}\{build}.msix
                        var msixFileName = $"{build}.msix";
                        var sourceMsixPath = Path.Combine(stagingPath, "WinMobile", minorVersion, env, msixFileName);
                        
                        if (!System.IO.File.Exists(sourceMsixPath))
                        {
                            // Try without minor version subdirectory: StagingPath\WinMobile\{env}\{build}.msix
                            sourceMsixPath = Path.Combine(stagingPath, "WinMobile", env, msixFileName);
                            
                            if (!System.IO.File.Exists(sourceMsixPath))
                            {
                                ErrorMessage = $"MSIX file not found: {msixFileName} in {Path.Combine(stagingPath, "WinMobile")}";
                                _logger.LogError("MSIX file not found at {Path}", sourceMsixPath);
                                return Page();
                            }
                        }
                        
                        // Create local directory structure
                        var localMsixDir = Path.Combine(msixPhysicalPath, "MSIX", minorVersion);
                        Directory.CreateDirectory(localMsixDir);
                        
                        // Copy MSIX file locally (only if it doesn't already exist)
                        var localMsixPath = Path.Combine(localMsixDir, msixFileName);
                        
                        if (!System.IO.File.Exists(localMsixPath))
                        {
                            _logger.LogInformation("Copying MSIX from {Source} to {Dest}", sourceMsixPath, localMsixPath);
                            System.IO.File.Copy(sourceMsixPath, localMsixPath, overwrite: false);
                            _logger.LogInformation("MSIX file copied successfully");
                        }
                        else
                        {
                            _logger.LogInformation("MSIX file already exists locally, skipping copy: {Path}", localMsixPath);
                        }
                        
                        // Generate .appinstaller file
                        var appInstallerContent = GenerateAppInstallerXml(app, build, minorVersion, env, msixFileName, msixServerUrl);
                        System.IO.File.WriteAllText(staticPath, appInstallerContent);
                        
                        _logger.LogInformation("Created .appinstaller at {Path}", staticPath);
                    }
                    catch (Exception ex)
                    {
                        ErrorMessage = $"Failed to create installer: {ex.Message}";
                        _logger.LogError(ex, "Failed to create installer on-demand");
                        return Page();
                    }
                }

                // Build URL for the static .appinstaller on the current IIS server
                // Example: https://ppcsuswiis1/MSIX/1.9/WinMobile_UAT.appinstaller
                AppInstallerUrl = $"{msixServerUrl}/MSIX/{minorVersion}/{app}_{env}.appinstaller";
                
                _logger.LogInformation("Serving static .appinstaller for {App} {Build} {Env}: {Url}", app, build, env, AppInstallerUrl);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error loading installer: {ex.Message}";
                _logger.LogError(ex, "Error loading .appinstaller for {App} {Build} {Env}", app, build, env);
            }

            return Page();
        }

        private string? ExtractMinorVersion(string build)
        {
            // Extract major.minor from version string (e.g., "CircaSports_1.9.671.156_x64" -> "1.9")
            // Pattern looks for version numbers anywhere in the string (supports 3 or 4 part versions)
            var match = Regex.Match(build, @"(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?");
            if (match.Success)
            {
                return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
            }
            return null;
        }

        private string GenerateAppInstallerXml(string appName, string build, string minorVersion, string environment, string msixFileName, string serverUrl)
        {
            try
            {
                // Extract package identity from the actual MSIX file
                var msixPath = Path.Combine(_config["MSIXPhysicalPath"], "MSIX", minorVersion, msixFileName);
                
                if (!System.IO.File.Exists(msixPath))
                {
                    _logger.LogError("MSIX file not found at {Path} when generating appinstaller", msixPath);
                    throw new FileNotFoundException($"MSIX file not found: {msixPath}");
                }
                
                // Read the AppxManifest.xml from the MSIX package
                using (var zip = System.IO.Compression.ZipFile.OpenRead(msixPath))
                {
                    var manifestEntry = zip.Entries.FirstOrDefault(e => e.Name.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase));
                    
                    if (manifestEntry == null)
                    {
                        throw new InvalidOperationException("AppxManifest.xml not found in MSIX package");
                    }
                    
                    using (var stream = manifestEntry.Open())
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        var manifestContent = reader.ReadToEnd();
                        var manifest = System.Xml.Linq.XDocument.Parse(manifestContent);
                        
                        // Extract Identity element
                        var ns = manifest.Root?.Name.Namespace ?? XNamespace.None;
                        var identity = manifest.Root?.Element(ns + "Identity");
                        
                        if (identity == null)
                        {
                            throw new InvalidOperationException("Identity element not found in AppxManifest.xml");
                        }
                        
                        var packageName = identity.Attribute("Name")?.Value ?? appName;
                        var publisher = identity.Attribute("Publisher")?.Value ?? "CN=Unknown";
                        var version = identity.Attribute("Version")?.Value ?? "1.0.0.0";
                        var architecture = identity.Attribute("ProcessorArchitecture")?.Value ?? "x64";
                        
                        _logger.LogInformation("Extracted MSIX identity: Name={Name}, Version={Version}, Publisher={Publisher}", 
                            packageName, version, publisher.Substring(0, Math.Min(50, publisher.Length)));
                        
                        var appInstallerUrl = $"{serverUrl}/MSIX/{minorVersion}/{appName}_{environment}.appinstaller";
                        var msixUrl = $"{serverUrl}/MSIX/{minorVersion}/{msixFileName}";
                        
                        var xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<AppInstaller xmlns=""http://schemas.microsoft.com/appx/appinstaller/2018"" 
              Uri=""{appInstallerUrl}"" 
              Version=""{version}"">
  <MainPackage xmlns=""http://schemas.microsoft.com/appx/appinstaller/2018"" 
               Name=""{packageName}"" 
               Publisher=""{System.Security.SecurityElement.Escape(publisher)}"" 
               Version=""{version}"" 
               Uri=""{msixUrl}"" 
               ProcessorArchitecture=""{architecture}"" />
  <UpdateSettings xmlns=""http://schemas.microsoft.com/appx/appinstaller/2018"">
    <OnLaunch HoursBetweenUpdateChecks=""0"" />
  </UpdateSettings>
</AppInstaller>";
                        
                        return xml;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate AppInstaller XML from MSIX package");
                throw;
            }
        }

        private class ProductConfig
        {
            public string Name { get; set; } = string.Empty;
            public string SubFolder { get; set; } = string.Empty;
            public string Type { get; set; } = "Standard";
            public bool RequiresEnvironment { get; set; }
        }
    }
}
