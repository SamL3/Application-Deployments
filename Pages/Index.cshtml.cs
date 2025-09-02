using ApplicationDeployment.Hubs;
using DnsClient.Internal;
using IWshRuntimeLibrary;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace ApplicationDeployment.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<IndexModel> _logger;
        private readonly Dictionary<string, string> _appExes;
        private readonly IHubContext<CopyHub> _hubContext; // Added

        public IndexModel(
            IConfiguration config,
            IWebHostEnvironment env,
            ILogger<IndexModel> logger,
            IHubContext<CopyHub> hubContext) // Added
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _env = env ?? throw new ArgumentNullException(nameof(env));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext)); // Added
            _appExes = LoadAppExes();
        }

        [BindProperty]
        public List<SelectListItem> Servers { get; set; } = new();
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

        public void OnGet()
        {
            var csvPath = Path.Combine(_env.WebRootPath, _config["CsvFilePath"] ?? throw new InvalidOperationException("CsvFilePath not configured"));
            var serverLines = System.IO.File.ReadAllLines(csvPath);
            Servers = serverLines.Select(s => new SelectListItem { Value = s, Text = s }).ToList();

            var stagingPath = _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured");
            if (Directory.Exists(stagingPath))
            {
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
            }
        }

        public IActionResult OnGetBuildsForApp(string selectedApp)
        {
            var stagingPath = _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured");
            var appPath = Path.Combine(stagingPath, selectedApp);
            var buildsPath = Path.Combine(appPath, ""); // Builds
            _logger.LogInformation("Fetching builds for app {SelectedApp}, Path: {BuildsPath}, Exists: {Exists}", selectedApp, buildsPath, Directory.Exists(buildsPath));
            var builds = new List<SelectListItem>();
            if (Directory.Exists(buildsPath))
            {
                builds = Directory.GetDirectories(buildsPath)
                    .Select(d => new SelectListItem { Value = Path.GetFileName(d), Text = Path.GetFileName(d) })
                    .OrderByDescending(d => Directory.GetCreationTime(Path.Combine(buildsPath, d.Value)))
                    .ToList();
            }
            return new JsonResult(builds);
        }

        public async Task<IActionResult> OnPostCopyAsync([FromForm] string[] SelectedServers, [FromForm] string SelectedApp, [FromForm] string SelectedBuild)
        {
            // Log the received values for debugging
            _logger.LogInformation("Request.Form: {Form}", string.Join(", ", Request.Form.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            _logger.LogInformation("SelectedServers: {Servers}", SelectedServers == null ? "null" : string.Join(",", SelectedServers));
            _logger.LogInformation("SelectedApp: {App}", SelectedApp);
            _logger.LogInformation("SelectedBuild: {Build}", SelectedBuild);

            if (SelectedServers == null || !SelectedServers.Any() || string.IsNullOrEmpty(SelectedApp) || string.IsNullOrEmpty(SelectedBuild))
            {
                _logger.LogError("Please select at least one server, an app, and a build.");
                return new JsonResult(new { success = false, message = "Please select at least one server, an app, and a build." });
            }

            var userId = User.Identity?.Name ?? Request.HttpContext.Connection.Id;
            var copyTasks = SelectedServers.Select(server => CopyFilesAsync(server, SelectedApp, SelectedBuild, userId));
            await Task.WhenAll(copyTasks);
            return new JsonResult(new { success = true });
        }

        private async Task CopyFilesAsync(string selectedServer, string selectedApp, string selectedBuild, string userId)
        {
            try
            {
                // Corrected source path (removed extra "Builds")
                var sourcePath = Path.Combine(
                    _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured"),
                    selectedApp,
                    selectedBuild);

                _logger.LogInformation("Resolved sourcePath: {Source} Exists:{Exists}", sourcePath, Directory.Exists(sourcePath));

                if (!Directory.Exists(sourcePath))
                {
                    await _hubContext.Clients.User(userId)
                        .SendAsync("ReceiveMessage", $"Source not found: {sourcePath}");
                    throw new Exception("Source not found.");
                }

                var destPath = $"\\\\{selectedServer ?? throw new ArgumentNullException(nameof(selectedServer))}\\C$\\{_config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured")}\\{selectedApp}\\{selectedBuild}";
                _logger.LogInformation("Resolved destPath: {Dest}", destPath);

                var files = Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories);
                int total = files.Length;

                Directory.CreateDirectory(destPath);

                int copied = 0;
                foreach (var file in files)
                {
                    var destFile = Path.Combine(destPath, Path.GetRelativePath(sourcePath, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    System.IO.File.Copy(file, destFile, true);
                    copied++;
                    int progress = total == 0 ? 100 : (int)(copied * 100.0 / total);
                    await _hubContext.Clients.User(userId).SendAsync("ReceiveProgress", progress);
                }

                await _hubContext.Clients.User(userId).SendAsync("ReceiveMessage", $"Copy completed for {selectedServer}.");
                CreateShortcut(selectedServer, selectedApp, selectedBuild);
                await _hubContext.Clients.User(userId).SendAsync("ReceiveMessage", $"Shortcut created for {selectedServer}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Copy failed for {Server}.", selectedServer);
                await _hubContext.Clients.User(userId).SendAsync("ReceiveMessage", $"Error for {selectedServer}: {ex.Message}");
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
            //var remoteDesktopDir = $@"\\{selectedServer}\C$\Users\Public\Desktop";
            
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
    }
}