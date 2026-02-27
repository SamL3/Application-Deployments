using DevApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DevApp.Pages
{
    public class MSIXManagerModel : PageModel
    {
        private readonly MSIXInstallerService _msixService;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<MSIXManagerModel> _logger;
        private readonly IConfiguration _config;

        public MSIXManagerModel(MSIXInstallerService msixService, IWebHostEnvironment env, ILogger<MSIXManagerModel> logger, IConfiguration config)
        {
            _msixService = msixService;
            _env = env;
            _logger = logger;
            _config = config;
        }

        public class StaticSiteInfo
        {
            public string MinorVersion { get; set; } = string.Empty;
            public string Environment { get; set; } = string.Empty;
            public int FileCount { get; set; }
        }

        public List<StaticSiteInfo> StaticSites { get; set; } = new();
        public bool Success { get; set; }
        public bool Error { get; set; }
        public string Message { get; set; } = string.Empty;

        public void OnGet()
        {
            LoadStaticSites();
        }

        public IActionResult OnPostRegenerate()
        {
            try
            {
                _logger.LogInformation("Starting static installer regeneration");
                
                // Get base URL from current request
                var request = HttpContext.Request;
                var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
                
                _msixService.GenerateStaticInstallers(baseUrl);
                
                Success = true;
                Message = "All static installers have been regenerated successfully.";
                _logger.LogInformation("Static installer regeneration completed successfully");
            }
            catch (Exception ex)
            {
                Error = true;
                Message = $"Failed to regenerate installers: {ex.Message}";
                _logger.LogError(ex, "Failed to regenerate static installers");
            }

            LoadStaticSites();
            return Page();
        }

        private void LoadStaticSites()
        {
            try
            {
                // Get the physical path on the IIS server
                var msixPhysicalPath = _config["MSIXPhysicalPath"];
                
                if (string.IsNullOrWhiteSpace(msixPhysicalPath))
                {
                    _logger.LogWarning("MSIXPhysicalPath not configured");
                    StaticSites = new List<StaticSiteInfo>();
                    return;
                }

                var msixRoot = Path.Combine(msixPhysicalPath, "MSIX");
                
                if (!Directory.Exists(msixRoot))
                {
                    StaticSites = new List<StaticSiteInfo>();
                    return;
                }

                var versionDirs = Directory.GetDirectories(msixRoot);
                
                StaticSites = versionDirs
                    .Select(dir =>
                    {
                        var minorVersion = Path.GetFileName(dir);
                        var files = Directory.GetFiles(dir, "*.appinstaller");
                        var environments = files
                            .Select(f => Path.GetFileNameWithoutExtension(f))
                            .Select(name => name.Split('_').LastOrDefault() ?? "")
                            .Where(env => !string.IsNullOrEmpty(env))
                            .Distinct()
                            .ToList();

                        return new StaticSiteInfo
                        {
                            MinorVersion = minorVersion,
                            Environment = string.Join(", ", environments),
                            FileCount = Directory.GetFiles(dir).Length
                        };
                    })
                    .OrderByDescending(s => s.MinorVersion)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load static sites");
                StaticSites = new List<StaticSiteInfo>();
            }
        }
    }
}
