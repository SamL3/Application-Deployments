using ApplicationDeployment.Hubs;
using ApplicationDeployment.Models;
using DnsClient.Internal;
using IWshRuntimeLibrary;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ApplicationDeployment.Pages
{
    public class DeployModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<DeployModel> _logger;
        private readonly Dictionary<string, string> _appExes;
        private readonly IHubContext<CopyHub> _hubContext;

        public DeployModel(
            IConfiguration config,
            IWebHostEnvironment env,
            ILogger<DeployModel> logger,
            IHubContext<CopyHub> hubContext)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _env = env ?? throw new ArgumentNullException(nameof(env));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _appExes = LoadAppExes();
        }

        [BindProperty]
        public List<SelectListItem> Servers { get; set; } = new();
        [BindProperty]
        public List<ServerInfo> ServerList { get; set; } = new();
        [BindProperty]
        public List<SelectListItem> Apps { get; set; } = new();
        [BindProperty]
        public List<SelectListItem> Builds { get; set; } = new();
        [BindProperty]
        public string[] SelectedServers { get; set; } = Array.Empty<string>();
        [BindProperty]
        public string SelectedApp { get; set; } = string.Empty;
        [BindProperty]
        public string SelectedBuild { get; set; } = string.Empty;
        public string BuildVersion { get; set; } = string.Empty;

        // Grouped structure for center-column cards
        public class AppBuildGroup
        {
            public string AppName { get; set; } = string.Empty;
            public List<string> Builds { get; set; } = new();
        }

        public List<AppBuildGroup> AppBuildGroups { get; set; } = new();

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

            var stagingPath = _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured");
            if (Directory.Exists(stagingPath))
            {
                // legacy dropdown lists
                Apps = Directory.GetDirectories(stagingPath)
                    .Select(d => new SelectListItem { Value = Path.GetFileName(d), Text = Path.GetFileName(d) })
                    .ToList();
                SelectedApp = Apps.FirstOrDefault()?.Value ?? string.Empty;
                var appPath = Path.Combine(stagingPath, SelectedApp);
                var buildsPath = Path.Combine(appPath, "");
                _logger.LogInformation("Initial builds path: {BuildsPath}, Exists: {Exists}", buildsPath, Directory.Exists(buildsPath));
                if (Directory.Exists(buildsPath))
                {
                    Builds = Directory.GetDirectories(buildsPath)
                        .Select(d => new SelectListItem { Value = Path.GetFileName(d), Text = Path.GetFileName(d) })
                        .OrderByDescending(d => Directory.GetCreationTime(Path.Combine(buildsPath, d.Value)))
                        .ToList();
                }

                // build grouped cards for center column
                AppBuildGroups = Directory.GetDirectories(stagingPath)
                    .Select(appDir =>
                    {
                        var appName = Path.GetFileName(appDir);
                        var builds = Directory.Exists(appDir)
                            ? Directory.GetDirectories(appDir)
                                .Select(d => Path.GetFileName(d))
                                .OrderByDescending(name => Directory.GetCreationTime(Path.Combine(appDir, name)))
                                .ToList()
                            : new List<string>();
                        return new AppBuildGroup { AppName = appName, Builds = builds };
                    })
                    .OrderBy(g => g.AppName)
                    .ToList();
            }
        }

        public IActionResult OnGetBuildsForApp(string selectedApp)
        {
            if (string.IsNullOrWhiteSpace(selectedApp))
                return new JsonResult(Array.Empty<object>());

            var stagingPath = _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured");
            var buildsPath = Path.Combine(stagingPath, selectedApp);
            if (!Directory.Exists(buildsPath))
            {
                _logger.LogWarning("Builds path missing for {App}: {Path}", selectedApp, buildsPath);
                return new JsonResult(Array.Empty<object>());
            }

            var list = Directory.GetDirectories(buildsPath)
                .Select(d => Path.GetFileName(d))
                .Select(name => new { value = name, text = name })
                .OrderByDescending(x => Directory.GetCreationTime(Path.Combine(buildsPath, x.value)))
                .ToList();

            return new JsonResult(list);
        }

        public async Task<IActionResult> OnPostCopyAsync(
            [FromForm] string[] SelectedServers,
            [FromForm] string[] Selections,
            [FromForm] string HubConnectionId)
        {
            _logger.LogInformation("Copy requested. HubConnectionId: {HubId} SelectedServers: {Servers} Selections: {Selections}",
                HubConnectionId,
                SelectedServers == null ? "null" : string.Join(",", SelectedServers),
                Selections == null ? "null" : string.Join(",", Selections));

            if (SelectedServers == null || SelectedServers.Length == 0)
                return new JsonResult(new { success = false, message = "Please select at least one server." });

            if (Selections == null || Selections.Length == 0)
                return new JsonResult(new { success = false, message = "Please select at least one build." });

            var staging = _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured");

            // Parse selections in the form "AppName|BuildName"
            var pairs = ParseSelections(Selections)
                .Where(p => !string.IsNullOrWhiteSpace(p.App) && !string.IsNullOrWhiteSpace(p.Build))
                .Where(p => Directory.Exists(Path.Combine(staging, p.App, p.Build)))
                .ToList();

            if (pairs.Count == 0)
            {
                _logger.LogWarning("No valid source folders found for provided selections.");
                return new JsonResult(new { success = false, message = "No valid build sources found for the chosen selections." });
            }

            // Run copy tasks: for each server x each selected app/build pair
            var tasks = new List<Task>();
            foreach (var server in SelectedServers)
            {
                foreach (var pair in pairs)
                {
                    tasks.Add(CopyFilesAsync(server, pair.App, pair.Build, HubConnectionId));
                }
            }

            await Task.WhenAll(tasks);

            return new JsonResult(new { success = true, totalCopies = tasks.Count });
        }

        private async Task CopyFilesAsync(string selectedServer, string selectedApp, string selectedBuild, string hubConnectionId)
        {
            try
            {
                var sourcePath = Path.Combine(_config["StagingPath"]!, selectedApp, selectedBuild);
                if (!Directory.Exists(sourcePath))
                {
                    await _hubContext.Clients.Client(hubConnectionId)
                        .SendAsync("ReceiveMessage", $"Source not found: {sourcePath}");
                    return;
                }

                var destPath = $@"\\{selectedServer}\C$\{_config["CSTApps"]}\{selectedApp}\{selectedBuild}";
                Directory.CreateDirectory(destPath);

                var files = Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories);
                int total = files.Length;
                int copied = 0;

                foreach (var file in files)
                {
                    var rel = Path.GetRelativePath(sourcePath, file);
                    var destFile = Path.Combine(destPath, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    System.IO.File.Copy(file, destFile, true);
                    copied++;
                    int progress = total == 0 ? 100 : (int)(copied * 100.0 / total);
                    await _hubContext.Clients.Client(hubConnectionId)
                        .SendAsync("ReceiveProgress", progress);
                }

                await _hubContext.Clients.Client(hubConnectionId)
                    .SendAsync("ReceiveMessage", $"Copy completed for {selectedServer}.");
                CreateShortcut(selectedServer, selectedApp, selectedBuild);
                await _hubContext.Clients.Client(hubConnectionId)
                    .SendAsync("ReceiveMessage", $"Shortcut created for {selectedServer}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Copy failed for {Server}", selectedServer);
                await _hubContext.Clients.Client(hubConnectionId)
                    .SendAsync("ReceiveMessage", $"Error for {selectedServer}: {ex.Message}");
            }
        }

        private void CreateShortcut(string selectedServer, string selectedApp, string selectedBuild)
        {
            var stagingAppsRoot = _config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured");
            var exeName = _appExes.TryGetValue(selectedApp, out var exe)
                ? exe
                : throw new InvalidOperationException($"No EXE configured for app '{selectedApp}' (check appExes.json).");

            var targetOnRemote = $@"C:\{stagingAppsRoot}\{selectedApp}\{selectedBuild}\{exeName}";
            var remoteDesktopDir = $@"\\{selectedServer}\C$\CSTApps";
            
            var shortcutName = $"{selectedApp} {selectedBuild}.lnk";
            _logger.LogInformation("shortcutName:{shortcutName}", shortcutName);

            var remoteShortcutPath = Path.Combine(remoteDesktopDir, shortcutName);
            _logger.LogInformation("remoteShortcutPath:{remoteShortcutPath}", remoteShortcutPath);

            _logger.LogInformation("Creating shortcut. RemoteDesktopDir={RemoteDesktopDir} Target={Target} Exists(Target)={TargetExists}",
                remoteDesktopDir, targetOnRemote, System.IO.File.Exists(targetOnRemote));

            // Validate remote desktop folder accessible
            if (!Directory.Exists(remoteDesktopDir))
            {
                _logger.LogWarning("Remote desktop directory not found or inaccessible: {Dir}", remoteDesktopDir);
                return;
            }

            // Create shortcut LOCALLY first (temp), then copy – avoids some UNC COM quirks
            var tempDir = Path.Combine(Path.GetTempPath(), "ShortcutBuild");
            Directory.CreateDirectory(tempDir);
            var localShortcutPath = Path.Combine(tempDir, shortcutName);

            try
            {
                var shell = new WshShell();
                var shortcut = (IWshShortcut)shell.CreateShortcut(localShortcutPath);
                shortcut.TargetPath = targetOnRemote;  // Target path (on the remote machine)
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetOnRemote) ?? "C:\\";
                shortcut.IconLocation = targetOnRemote;
                shortcut.Description = $"{selectedApp} {selectedBuild}";
                shortcut.Save();

                if (!System.IO.File.Exists(localShortcutPath))
                {
                    _logger.LogError("Local shortcut was not created: {LocalPath}", localShortcutPath);
                    return;
                }

                // Copy to remote desktop (overwrite)
                System.IO.File.Copy(localShortcutPath, remoteShortcutPath, true);

                _logger.LogInformation("Shortcut deployed: {RemoteShortcutPath}", remoteShortcutPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create shortcut for {Server} App={App} Build={Build}", selectedServer, selectedApp, selectedBuild);
            }
            finally
            {
                try
                {
                    if (System.IO.File.Exists(localShortcutPath))
                        System.IO.File.Delete(localShortcutPath);
                }
                catch { /* ignore temp cleanup failures */ }
            }
        }

        private Dictionary<string, string> LoadAppExes()
        {
            var appExesPath = Path.Combine(_env.WebRootPath, "appExes.json");
            if (System.IO.File.Exists(appExesPath))
            {
                var json = System.IO.File.ReadAllText(appExesPath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            }
            return new Dictionary<string, string>();
        }

        private IEnumerable<(string App, string Build)> ParseSelections(IEnumerable<string> selections)
        {
            foreach (var s in selections)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                var parts = s.Split('|', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                    yield return (parts[0], parts[1]);
            }
        }
    }
}
