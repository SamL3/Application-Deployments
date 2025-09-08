using ApplicationDeployment.Hubs;
using ApplicationDeployment.Models;
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

        public class AppBuildVariant
        {
            public string Build { get; set; } = string.Empty;
            public string? Environment { get; set; }
            public string Display => Environment == null ? Build : $"{Build}\\{Environment}";
            public string SelectionValue(string app) =>
                Environment == null ? $"{app}|{Build}" : $"{app}|{Build}|{Environment}";
        }

        public class AppBuildGroup
        {
            public string AppName { get; set; } = string.Empty;
            public List<AppBuildVariant> Variants { get; set; } = new();
        }

        [BindProperty] public List<SelectListItem> Servers { get; set; } = new();
        [BindProperty] public List<ServerInfo> ServerList { get; set; } = new();
        [BindProperty] public string[] SelectedServers { get; set; } = Array.Empty<string>();

        [BindProperty] public string SelectedEnvironment { get; set; } = string.Empty; // (client-side filter)
        public List<SelectListItem> Environments { get; set; } = new();

        public List<AppBuildGroup> AppBuildGroups { get; set; } = new();

        public void OnGet()
        {
            LoadServers();
            BuildInventory();
        }

        public async Task<IActionResult> OnPostCopyAsync(
            [FromForm] string[] SelectedServers,
            [FromForm] string[] Selections,
            [FromForm] string HubConnectionId)
        {
            _logger.LogInformation("Copy requested. Hub={Hub} Servers={Servers} Selections={Selections}",
                HubConnectionId,
                SelectedServers == null ? "null" : string.Join(',', SelectedServers),
                Selections == null ? "null" : string.Join(',', Selections));

            if (SelectedServers == null || SelectedServers.Length == 0)
                return new JsonResult(new { success = false, message = "Please select at least one server." });

            if (Selections == null || Selections.Length == 0)
                return new JsonResult(new { success = false, message = "Please select at least one build." });

            var staging = _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured");

            var triples = ParseSelections(Selections)
                .Where(t =>
                {
                    var path = t.Environment == null
                        ? Path.Combine(staging, t.App, t.Build)
                        : Path.Combine(staging, t.App, t.Build, t.Environment);
                    return Directory.Exists(path);
                })
                .ToList();

            if (triples.Count == 0)
                return new JsonResult(new { success = false, message = "No valid build sources found." });

            var tasks = new List<Task>();
            foreach (var server in SelectedServers)
            {
                foreach (var t in triples)
                {
                    tasks.Add(CopyFilesAsync(server, t.App, t.Build, t.Environment, HubConnectionId));
                }
            }

            await Task.WhenAll(tasks);
            return new JsonResult(new { success = true, totalCopies = tasks.Count });
        }

        private void BuildInventory()
        {
            AppBuildGroups = new List<AppBuildGroup>();
            var stagingPath = _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured");
            if (!Directory.Exists(stagingPath))
            {
                _logger.LogWarning("Staging path missing: {Path}", stagingPath);
                return;
            }

            var envSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var appDir in Directory.GetDirectories(stagingPath))
            {
                var appName = Path.GetFileName(appDir);
                if (string.IsNullOrWhiteSpace(appName)) continue;

                var variants = new List<AppBuildVariant>();
                _appExes.TryGetValue(appName, out var exeName);
                exeName ??= string.Empty;

                foreach (var buildDir in Directory.GetDirectories(appDir))
                {
                    var buildName = Path.GetFileName(buildDir);
                    if (string.IsNullOrWhiteSpace(buildName)) continue;

                    var neutralExe = !string.IsNullOrEmpty(exeName) && System.IO.File.Exists(Path.Combine(buildDir, exeName));
                    if (neutralExe)
                    {
                        variants.Add(new AppBuildVariant { Build = buildName, Environment = null });
                    }
                    else
                    {
                        // environment-specific subfolders
                        foreach (var envDir in Directory.GetDirectories(buildDir))
                        {
                            var envName = Path.GetFileName(envDir);
                            if (string.IsNullOrWhiteSpace(envName)) continue;
                            if (!string.IsNullOrEmpty(exeName) && System.IO.File.Exists(Path.Combine(envDir, exeName)))
                            {
                                variants.Add(new AppBuildVariant { Build = buildName, Environment = envName });
                                envSet.Add(envName);
                            }
                        }
                    }
                }

                if (variants.Count > 0)
                {
                    // Order: newest build first (by directory creation time)
                    variants = variants
                        .OrderByDescending(v =>
                        {
                            try
                            {
                                var path = v.Environment == null
                                    ? Path.Combine(appDir, v.Build)
                                    : Path.Combine(appDir, v.Build, v.Environment);
                                return Directory.GetCreationTime(path);
                            }
                            catch { return DateTime.MinValue; }
                        })
                        .ToList();

                    AppBuildGroups.Add(new AppBuildGroup
                    {
                        AppName = appName,
                        Variants = variants
                    });
                }
            }

            AppBuildGroups = AppBuildGroups.OrderBy(g => g.AppName).ToList();
            Environments = envSet.OrderBy(e => e).Select(e => new SelectListItem { Value = e, Text = e }).ToList();
        }

        private async Task CopyFilesAsync(string selectedServer, string selectedApp, string selectedBuild, string? environment, string hubConnectionId)
        {
            try
            {
                var stagingRoot = _config["StagingPath"]!;
                var sourcePath = environment == null
                    ? Path.Combine(stagingRoot, selectedApp, selectedBuild)
                    : Path.Combine(stagingRoot, selectedApp, selectedBuild, environment);

                if (!Directory.Exists(sourcePath))
                {
                    await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveMessage", $"Source not found: {sourcePath}");
                    return;
                }

                var destPath = environment == null
                    ? $@"\\{selectedServer}\C$\{_config["CSTApps"]}\{selectedApp}\{selectedBuild}"
                    : $@"\\{selectedServer}\C$\{_config["CSTApps"]}\{selectedApp}\{selectedBuild}\{environment}";

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
                    await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveProgress", progress);
                }

                await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveMessage", $"Copy completed for {selectedServer}.");
                CreateShortcut(selectedServer, selectedApp, selectedBuild, environment);
                await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveMessage", "Shortcut created.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Copy failed for {Server}", selectedServer);
                await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveMessage", $"Error: {ex.Message}");
            }
        }

        private void CreateShortcut(string selectedServer, string selectedApp, string selectedBuild, string? environment)
        {
            var stagingAppsRoot = _config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured");
            var exeName = _appExes.TryGetValue(selectedApp, out var exe)
                ? exe
                : throw new InvalidOperationException($"No EXE configured for app '{selectedApp}' (appExes.json).");

            var targetOnRemote = environment == null
                ? $@"C:\{stagingAppsRoot}\{selectedApp}\{selectedBuild}\{exeName}"
                : $@"C:\{stagingAppsRoot}\{selectedApp}\{selectedBuild}\{environment}\{exeName}";

            var remoteShortcutDir = $@"\\{selectedServer}\C$\CSTApps";
            if (!Directory.Exists(remoteShortcutDir))
            {
                _logger.LogWarning("Remote shortcut dir missing: {Dir}", remoteShortcutDir);
                return;
            }

            var shortcutName = environment == null
                ? $"{selectedApp} {selectedBuild}.lnk"
                : $"{selectedApp} {selectedBuild} {environment}.lnk";

            var remoteShortcutPath = Path.Combine(remoteShortcutDir, shortcutName);

            var tempDir = Path.Combine(Path.GetTempPath(), "ShortcutBuild");
            Directory.CreateDirectory(tempDir);
            var localShortcutPath = Path.Combine(tempDir, shortcutName);

            try
            {
                var shell = new WshShell();
                var shortcut = (IWshShortcut)shell.CreateShortcut(localShortcutPath);
                shortcut.TargetPath = targetOnRemote;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetOnRemote) ?? "C:\\";
                shortcut.IconLocation = targetOnRemote;
                shortcut.Description = environment == null
                    ? $"{selectedApp} {selectedBuild}"
                    : $"{selectedApp} {selectedBuild} ({environment})";
                if (environment != null)
                {
                    shortcut.Arguments = $"-Mod {environment} -SubMod {environment}";
                }
                shortcut.Save();

                if (!System.IO.File.Exists(localShortcutPath))
                {
                    _logger.LogError("Local shortcut not created: {LocalPath}", localShortcutPath);
                    return;
                }

                System.IO.File.Copy(localShortcutPath, remoteShortcutPath, true);
                _logger.LogInformation("Shortcut deployed: {Path}", remoteShortcutPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed shortcut creation for {Server} App={App} Build={Build} Env={Env}",
                    selectedServer, selectedApp, selectedBuild, environment ?? "(none)");
            }
            finally
            {
                try { if (System.IO.File.Exists(localShortcutPath)) System.IO.File.Delete(localShortcutPath); } catch { }
            }
        }

        private void LoadServers()
        {
            var csvPath = Path.Combine(_env.WebRootPath, _config["CsvFilePath"] ?? throw new InvalidOperationException("CsvFilePath not configured"));
            var serverLines = System.IO.File.ReadAllLines(csvPath);
            ServerList = serverLines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line =>
                {
                    var p = line.Split(',', StringSplitOptions.TrimEntries);
                    return new ServerInfo
                    {
                        HostName = p.Length > 0 ? p[0] : string.Empty,
                        UserID = p.Length > 1 ? p[1] : string.Empty,
                        Description = p.Length > 2 ? p[2] : string.Empty
                    };
                })
                .Where(s => !string.IsNullOrWhiteSpace(s.HostName))
                .ToList();
            Servers = ServerList.Select(s => new SelectListItem { Value = s.HostName, Text = s.HostName }).ToList();
        }

        private Dictionary<string, string> LoadAppExes()
        {
            var appExesPath = Path.Combine(_env.WebRootPath, "appExes.json");
            if (System.IO.File.Exists(appExesPath))
            {
                var jsonString = System.IO.File.ReadAllText(appExesPath);
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);
                }
                catch (JsonException ex)
                {
                    // Log or handle the error
                }
            }
            return new Dictionary<string, string>();
        }

        private IEnumerable<(string App, string Build, string? Environment)> ParseSelections(IEnumerable<string> selections)
        {
            foreach (var s in selections)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                var parts = s.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2) yield return (parts[0], parts[1], null);
                else if (parts.Length == 3) yield return (parts[0], parts[1], parts[2]);
            }
        }
    }
}
