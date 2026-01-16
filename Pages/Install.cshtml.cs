using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text;
using System.Xml.Linq;

namespace DevApp.Pages
{
    public class InstallModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly ILogger<InstallModel> _logger;
        private readonly IWebHostEnvironment _env;

        public InstallModel(IConfiguration config, ILogger<InstallModel> logger, IWebHostEnvironment env)
        {
            _config = config;
            _logger = logger;
            _env = env;
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
                var stagingPath = _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured");
                
                // Get product config
                var productsSection = _config.GetSection("Products");
                var products = productsSection.Get<List<ProductConfig>>();
                var product = products?.FirstOrDefault(p => p.Type == "MSIX");
                
                if (product == null)
                {
                    ErrorMessage = "MSIX product configuration not found.";
                    return Page();
                }

                // Path: staging\ProductSubFolder\Environment\{filename}.msix
                var msixPath = Path.Combine(stagingPath, product.SubFolder, env, build + ".msix");
                
                if (!System.IO.File.Exists(msixPath))
                {
                    ErrorMessage = $"MSIX file not found: {msixPath}";
                    _logger.LogError("MSIX file not found: {Path}", msixPath);
                    return Page();
                }

                // Generate .appinstaller file
                var appInstallerContent = GenerateAppInstaller(app, build, env, msixPath);
                
                // Save to wwwroot for serving
                var wwwPath = Path.Combine(_env.WebRootPath, "msix");
                Directory.CreateDirectory(wwwPath);
                
                var appInstallerFileName = $"{app}_{build}_{env}.appinstaller";
                var appInstallerPath = Path.Combine(wwwPath, appInstallerFileName);
                
                System.IO.File.WriteAllText(appInstallerPath, appInstallerContent);
                
                // Build URL - include path base if app is hosted in subfolder
                var request = HttpContext.Request;
                var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
                AppInstallerUrl = $"{baseUrl}/msix/{appInstallerFileName}";
                
                _logger.LogInformation("Generated .appinstaller for {App} {Build} {Env}: {Url}", app, build, env, AppInstallerUrl);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error generating installer: {ex.Message}";
                _logger.LogError(ex, "Error generating .appinstaller for {App} {Build} {Env}", app, build, env);
            }

            return Page();
        }

        private string GenerateAppInstaller(string appName, string buildName, string environment, string msixPath)
        {
            var request = HttpContext.Request;
            var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
            
            // Copy MSIX to wwwroot for serving
            var wwwMsixPath = Path.Combine(_env.WebRootPath, "msix");
            Directory.CreateDirectory(wwwMsixPath);
            
            var msixFileName = Path.GetFileName(msixPath);
            var destMsixPath = Path.Combine(wwwMsixPath, msixFileName);
            System.IO.File.Copy(msixPath, destMsixPath, true);
            
            var msixUrl = $"{baseUrl}/msix/{msixFileName}";
            
            // Read MSIX package info (simplified - in production, parse the AppxManifest.xml from the package)
            var version = "1.0.0.0"; // Extract from MSIX or use build name
            var publisher = "CN=YourCompany"; // Extract from MSIX
            var packageName = appName.Replace(" ", "");
            
            var xml = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(XName.Get("AppInstaller", "http://schemas.microsoft.com/appx/appinstaller/2018"),
                    new XAttribute("Uri", $"{baseUrl}/msix/{appName}_{buildName}_{environment}.appinstaller"),
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

        private class ProductConfig
        {
            public string Name { get; set; } = string.Empty;
            public string SubFolder { get; set; } = string.Empty;
            public string Type { get; set; } = "Standard";
            public bool RequiresEnvironment { get; set; }
        }
    }
}
