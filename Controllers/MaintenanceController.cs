using Microsoft.AspNetCore.Mvc;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace Application_Deployments.Controllers
{
    public class MaintenanceController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string _localBasePath;

        public MaintenanceController(IConfiguration configuration)
        {
            _configuration = configuration;
            _localBasePath = _configuration["LocalBasePath"] ?? @"C:\Deployments\Servers";
        }

        public IActionResult Index()
        {
            var model = new MaintenanceViewModel
            {
                Servers = Directory.GetDirectories(_localBasePath).Select(Path.GetFileName).ToList(),
                Apps = new List<App>()
            };
            return View(model);
        }

        [HttpPost]
        public IActionResult DeleteBuilds(string selectedServer, List<string> selectedBuilds)
        {
            var model = new MaintenanceViewModel
            {
                Servers = Directory.GetDirectories(_localBasePath).Select(Path.GetFileName).ToList(),
                SelectedServer = selectedServer,
                Apps = new List<App>()
            };

            if (selectedBuilds != null && selectedBuilds.Any())
            {
                foreach (var path in selectedBuilds)
                {
                    try
                    {
                        if (path.StartsWith(_localBasePath, StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.Delete(path, true); // Recursive delete
                            Console.WriteLine($"Deleted: {path}");
                        }
                        else
                        {
                            ModelState.AddModelError("", $"Invalid path: {path}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Delete failed for {path}: {ex.Message}");
                        ModelState.AddModelError("", $"Delete failed for {Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            }

            // Reload apps/builds
            if (!string.IsNullOrEmpty(selectedServer))
            {
                try
                {
                    var serverPath = Path.Combine(_localBasePath, selectedServer);
                    var appDirs = Directory.GetDirectories(serverPath);
                    foreach (var appDir in appDirs)
                    {
                        var app = new App
                        {
                            Name = Path.GetFileName(appDir),
                            Builds = Directory.GetDirectories(appDir).Select(Path.GetFileName).ToList()
                        };
                        model.Apps.Add(app);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reloading apps: {ex.Message}");
                    ModelState.AddModelError("", $"Error reloading apps: {ex.Message}");
                }
            }

            return View("Index", model);
        }

        [HttpPost]
        public IActionResult LoadApps(string selectedServer)
        {
            var model = new MaintenanceViewModel
            {
                Servers = Directory.GetDirectories(_localBasePath).Select(Path.GetFileName).ToList(),
                SelectedServer = selectedServer,
                Apps = new List<App>()
            };

            if (!string.IsNullOrEmpty(selectedServer))
            {
                try
                {
                    var serverPath = Path.Combine(_localBasePath, selectedServer);
                    var appDirs = Directory.GetDirectories(serverPath);
                    foreach (var appDir in appDirs)
                    {
                        var app = new App
                        {
                            Name = Path.GetFileName(appDir),
                            Builds = Directory.GetDirectories(appDir).Select(Path.GetFileName).ToList()
                        };
                        model.Apps.Add(app);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading apps/builds: {ex.Message} | Path: {Path.Combine(_localBasePath, selectedServer)}");
                    ModelState.AddModelError("", $"Error loading apps: {ex.Message}");
                }
            }

            return View("Index", model);
        }
    }

    public class MaintenanceViewModel
    {
        public List<string> Servers { get; set; } = new();
        public string SelectedServer { get; set; }
        public List<App> Apps { get; set; } = new();
    }

    public class App
    {
        public string Name { get; set; }
        public List<string> Builds { get; set; } = new();
    }
}