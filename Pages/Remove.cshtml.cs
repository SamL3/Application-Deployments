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
    public class RemoveModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<RemoveModel> _logger;

        public RemoveModel(IConfiguration config, IWebHostEnvironment env, ILogger<RemoveModel> logger)
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

            Servers = ServerList.Select(s => new SelectListItem { Value = s.HostName, Text = s.HostName }).ToList();
        }

        // GET: /Remove?handler=Deployments&server=NAME
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
                        var buildsWithSizes = new List<object>();

                        if (Directory.Exists(appDir))
                        {
                            var buildDirs = Directory.GetDirectories(appDir);
                            buildsWithSizes = buildDirs.Select(buildDir =>
                            {
                                var buildName = Path.GetFileName(buildDir);
                                long sizeBytes = 0;

                                try
                                {
                                    sizeBytes = GetDirectorySize(buildDir);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to calculate size for {BuildDir}", buildDir);
                                }

                                var sizeMB = Math.Round(sizeBytes / (1024.0 * 1024.0), 2);

                                return new
                                {
                                    name = buildName,
                                    sizeMB = sizeMB
                                };
                            }).OrderByDescending(b => b.name).ToList<object>();
                        }

                        return new
                        {
                            app = appName,
                            builds = buildsWithSizes,
                            buildCount = buildsWithSizes.Count
                        };
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

        // POST: /Remove?handler=RemoveMultiple (legacy app-level removal)
        public IActionResult OnPostRemoveMultiple([FromForm] string server, [FromForm] string[] selectedApps)
        {
            _logger.LogInformation("OnPostRemoveMultiple called. server={Server} selectedApps={SelectedApps}",
                server, selectedApps == null ? "null" : string.Join(",", selectedApps));

            if (string.IsNullOrWhiteSpace(server))
                return BadRequest(new { success = false, message = "server required" });

            if (selectedApps == null || selectedApps.Length == 0)
                return BadRequest(new { success = false, message = "No applications selected for removal" });

            try
            {
                var root = _config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured");
                var results = new List<object>();
                var successCount = 0;
                var errorCount = 0;

                foreach (var app in selectedApps)
                {
                    if (string.IsNullOrWhiteSpace(app)) continue;

                    if (app.IndexOfAny(new[] { '\\', '/' }) >= 0)
                    {
                        results.Add(new { app, success = false, message = "Invalid app name" });
                        errorCount++;
                        continue;
                    }

                    var basePath = $@"\\{server}\C$\{root}\{app}";

                    try
                    {
                        if (!Directory.Exists(basePath))
                        {
                            results.Add(new { app, success = false, message = "App folder not found" });
                            errorCount++;
                            continue;
                        }

                        SafeDeleteDirectory(basePath);
                        var delCount = TryDeleteShortcuts(server, app, null);
                        _logger.LogInformation("Removed app folder {Path} and {Count} shortcut(s) on {Server}", basePath, delCount, server);

                        results.Add(new { app, success = true, message = "Removed successfully" });
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove app {App} on {Server}", app, server);
                        results.Add(new { app, success = false, message = ex.Message });
                        errorCount++;
                    }
                }

                return new JsonResult(new
                {
                    success = errorCount == 0,
                    message = $"Removal completed: {successCount} successful, {errorCount} failed",
                    results,
                    successCount,
                    errorCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process multiple removals on {Server}", server);
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // NEW: POST /Remove?handler=RemoveMultipleBuilds (build-level removal used by Remove.cshtml JS)
        public IActionResult OnPostRemoveMultipleBuilds([FromForm] string server, [FromForm] List<string> selectedBuilds)
        {
            _logger.LogInformation("OnPostRemoveMultipleBuilds called. server={Server} count={Count}", server, selectedBuilds?.Count);

            if (string.IsNullOrWhiteSpace(server))
                return BadRequest(new { success = false, message = "server required" });

            if (selectedBuilds == null || selectedBuilds.Count == 0)
                return BadRequest(new { success = false, message = "No builds selected" });

            try
            {
                var root = _config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured");
                var results = new List<object>();
                int successCount = 0, errorCount = 0;

                foreach (var composite in selectedBuilds)
                {
                    if (string.IsNullOrWhiteSpace(composite)) continue;

                    var parts = composite.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2)
                    {
                        results.Add(new { id = composite, success = false, message = "Invalid identifier format" });
                        errorCount++;
                        continue;
                    }

                    var app = parts[0];
                    var build = parts[1];

                    if (HasIllegalName(app) || HasIllegalName(build))
                    {
                        results.Add(new { app, build, success = false, message = "Invalid characters" });
                        errorCount++;
                        continue;
                    }

                    var buildPath = $@"\\{server}\C$\{root}\{app}\{build}";

                    try
                    {
                        if (!Directory.Exists(buildPath))
                        {
                            results.Add(new { app, build, success = false, message = "Build folder not found" });
                            errorCount++;
                            continue;
                        }

                        SafeDeleteDirectory(buildPath);
                        var delCount = TryDeleteShortcuts(server, app, build);
                        _logger.LogInformation("Removed build {Build} of app {App} and {Count} shortcut(s) on {Server}", build, app, delCount, server);

                        results.Add(new { app, build, success = true, message = "Removed" });
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove build {Build} of app {App} on {Server}", build, app, server);
                        results.Add(new { app, build, success = false, message = ex.Message });
                        errorCount++;
                    }
                }

                var overallSuccess = errorCount == 0;
                return new JsonResult(new
                {
                    success = overallSuccess,
                    message = $"Build removal: {successCount} succeeded, {errorCount} failed",
                    results,
                    successCount,
                    errorCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed removing multiple builds on {Server}", server);
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // POST: /Remove?handler=Remove (single app or single build)
        public IActionResult OnPostRemove([FromForm] string server, [FromForm] string app, [FromForm] string? build, [FromForm] bool removeApp)
        {
            _logger.LogInformation("OnPostRemove called. server={Server} app={App} build={Build} removeApp={RemoveApp}", server, app, build, removeApp);

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(app))
                return BadRequest(new { success = false, message = "server & app required" });

            try
            {
                var root = _config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured");
                var basePath = $@"\\{server}\C$\{root}\{app}";

                if (HasIllegalName(app) || (build != null && HasIllegalName(build)))
                    return BadRequest(new { success = false, message = "Invalid app/build names" });

                if (removeApp)
                {
                    if (!Directory.Exists(basePath))
                    {
                        _logger.LogWarning("Remove requested for app but path not found: {Path}", basePath);
                        return new JsonResult(new { success = false, message = "App folder not found on target server." });
                    }

                    SafeDeleteDirectory(basePath);
                    var delCount = TryDeleteShortcuts(server, app, null);
                    _logger.LogInformation("Removed app folder {Path} and {Count} shortcut(s) on {Server}", basePath, delCount, server);

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
                    var delCount = TryDeleteShortcuts(server, app, build);
                    _logger.LogInformation("Removed build folder {Path} and {Count} shortcut(s) on {Server}", buildPath, delCount, server);

                    return new JsonResult(new { success = true, message = $"Build removed: {buildPath}" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove deployment on {Server} app={App}", server, app);
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        private bool HasIllegalName(string name) =>
            name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0;

        private long GetDirectorySize(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
                return 0;

            long size = 0;
            try
            {
                var files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        size += fileInfo.Length;
                    }
                    catch { }
                }
            }
            catch { }
            return size;
        }

        private void SafeDeleteDirectory(string path)
        {
            var full = Path.GetFullPath(path);

            void ClearAttributesRecursive(string dir)
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    try { System.IO.File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    try { new DirectoryInfo(sub).Attributes = FileAttributes.Normal; } catch { }
                    ClearAttributesRecursive(sub);
                }
            }

            try { ClearAttributesRecursive(full); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed clearing attributes under {Path}", full);
            }

            Directory.Delete(full, true);
        }

        // Delete shortcuts matching an app or a specific app+build on the remote machine.
        private int TryDeleteShortcuts(string server, string app, string? build)
        {
            try
            {
                var shortcutDir = $@"\\{server}\C$\CSTApps";
                if (!Directory.Exists(shortcutDir))
                {
                    _logger.LogWarning("Shortcut directory not found: {Dir}", shortcutDir);
                    return 0;
                }

                var pattern = build == null ? $"{app} *.lnk" : $"{app} {build}*.lnk";
                var files = Directory.GetFiles(shortcutDir, pattern);
                var deleted = 0;
                foreach (var f in files)
                {
                    try { System.IO.File.Delete(f); deleted++; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete shortcut {File}", f); }
                }

                if (deleted > 0)
                {
                    _logger.LogInformation("Deleted {Count} shortcut(s) in {Dir} with pattern {Pattern}", deleted, shortcutDir, pattern);
                }
                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting shortcuts for app={App} build={Build} on {Server}", app, build ?? "(none)", server);
                return 0;
            }
        }
    }
}