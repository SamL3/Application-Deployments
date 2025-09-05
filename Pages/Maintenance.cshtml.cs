using ApplicationDeployment.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ApplicationDeployment.Pages
{
    public class MaintenanceModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<MaintenanceModel> _logger;

        public MaintenanceModel(IConfiguration config, IWebHostEnvironment env, ILogger<MaintenanceModel> logger)
        {
            _config = config;
            _env = env;
            _logger = logger;
        }

        public List<SelectListItem> Servers { get; set; } = new();
        public List<ServerInfo> ServerList { get; set; } = new();

        public string CstApps => _config["CSTApps"] ?? string.Empty;

        public void OnGet()
        {
            var csvPath = Path.Combine(_env.WebRootPath, _config["CsvFilePath"] ?? throw new InvalidOperationException("CsvFilePath not configured"));
            var serverLines = System.IO.File.ReadAllLines(csvPath);
            
            // Parse CSV with hostname, userid, description
            ServerList = serverLines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line =>
                {
                    var parts = line.Split(',', StringSplitOptions.TrimEntries);
                    return new ServerInfo
                    {
                        HostName = parts.Length > 0 ? parts[0] : string.Empty,
                        UserID = parts.Length > 1 ? parts[1] : string.Empty,
                        Description = parts.Length > 2 ? parts[2] : string.Empty
                    };
                })
                .Where(s => !string.IsNullOrWhiteSpace(s.HostName))
                .ToList();

            // Keep legacy format for backward compatibility
            Servers = ServerList.Select(s => new SelectListItem { Value = s.HostName, Text = s.HostName }).ToList();
        }

        // GET handler: /Maintenance?handler=Deployments&server=NAME
        public IActionResult OnGetDeployments(string server)
        {
            if (string.IsNullOrWhiteSpace(server)) return BadRequest("server required");

            try
            {
                var root = _config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured");
                var unc = $@"\\{server}\C$\{root}";
                if (!Directory.Exists(unc)) return new JsonResult(new List<object>());

                var groups = Directory.GetDirectories(unc)
                    .Select(appDir =>
                    {
                        var appName = Path.GetFileName(appDir);
                        var builds = Directory.Exists(appDir)
                            ? Directory.GetDirectories(appDir).Select(d => Path.GetFileName(d)).ToList()
                            : new List<string>();
                        return new { app = appName, builds };
                    })
                    .OrderBy(g => g.app)
                    .ToList();

                return new JsonResult(groups);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate deployments on {Server}", server);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // POST handler: /Maintenance?handler=Remove
        public IActionResult OnPostRemove([FromForm] string server, [FromForm] string app, [FromForm] string? build, [FromForm] bool removeApp)
        {
            _logger.LogInformation("OnPostRemove called. server={Server} app={App} build={Build} removeApp={RemoveApp}", server, app, build, removeApp);

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(app))
                return BadRequest(new { success = false, message = "server & app required" });

            try
            {
                var root = _config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured");
                // Build UNC carefully
                var basePath = $@"\\{server}\C$\{root}\{app}";

                // safety: prevent path traversal by rejecting path separators inside names
                if (app.IndexOfAny(new[] { '\\', '/' }) >= 0 || (build != null && build.IndexOfAny(new[] { '\\', '/' }) >= 0))
                    return BadRequest(new { success = false, message = "Invalid app/build names" });

                if (removeApp)
                {
                    if (!Directory.Exists(basePath))
                    {
                        _logger.LogWarning("Remove requested for app but path not found: {Path}", basePath);
                        return new JsonResult(new { success = false, message = "App folder not found on target server." });
                    }

                    SafeDeleteDirectory(basePath);
                    _logger.LogInformation("Removed app folder {Path} on {Server}", basePath, server);
                    return new JsonResult(new { success = true, message = $"App removed: {basePath}" });
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(build))
                        return BadRequest(new { success = false, message = "build required for partial remove" });

                    var buildPath = Path.Combine(basePath, build);
                    if (!Directory.Exists(buildPath))
                    {
                        _logger.LogWarning("Remove requested for build but path not found: {Path}", buildPath);
                        return new JsonResult(new { success = false, message = "Build folder not found on target server." });
                    }

                    SafeDeleteDirectory(buildPath);
                    _logger.LogInformation("Removed build folder {Path} on {Server}", buildPath, server);
                    return new JsonResult(new { success = true, message = $"Build removed: {buildPath}" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove deployment on {Server} app={App}", server, app);
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // Ensure read-only attributes don't block deletion; attempt to delete recursively.
        private void SafeDeleteDirectory(string path)
        {
            // Normalize and verify still inside expected share (extra safety)
            var full = Path.GetFullPath(path);
            // Attempt to clear attributes then delete
            void ClearAttributesRecursive(string dir)
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    try { System.IO.File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    try { DirectoryInfo di = new DirectoryInfo(sub); di.Attributes = FileAttributes.Normal; } catch { }
                    ClearAttributesRecursive(sub);
                }
            }

            try
            {
                ClearAttributesRecursive(full);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed clearing attributes under {Path}", full);
            }

            // Final delete (may still throw if permissions lacking)
            Directory.Delete(full, true);
        }
    }
}