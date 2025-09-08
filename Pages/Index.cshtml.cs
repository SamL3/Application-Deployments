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
    public class IndexModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<IndexModel> _logger;
        private readonly IHubContext<CopyHub> _hubContext;

        private readonly Dictionary<string,string> _exeMap;
        private readonly Dictionary<string,bool> _envInShortcutMap;
        private readonly List<string> _environments;

        public IndexModel(IConfiguration config,
                          IWebHostEnvironment env,
                          ILogger<IndexModel> logger,
                          IHubContext<CopyHub> hubContext)
        {
            _config = config;
            _env = env;
            _logger = logger;
            _hubContext = hubContext;
            (_exeMap, _envInShortcutMap) = LoadAppExeMetadata();
            _environments = (_config["Environments"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ===== New models / properties for environment support =====

        public class AppBuildEntry
        {
            public string App { get; set; } = string.Empty;
            public string Build { get; set; } = string.Empty;
            public string? Environment { get; set; } // null if environment-neutral
            public bool IsEnvironmentSpecific => Environment != null;
        }

        public class AppBuildGroup
        {
            public string AppName { get; set; } = string.Empty;
            public List<string> Builds { get; set; } = new();
        }

        public List<AppBuildGroup> AppBuildGroups { get; set; } = new();

        // Add this property to the IndexModel class
        public List<ServerInfo> ServerList { get; set; } = new();

        // Add this property to the IndexModel class
        public List<SelectListItem> Servers { get; set; } = new();

        // Add this property to the IndexModel class
        public List<SelectListItem> Apps { get; set; } = new();

        // Add this property to the IndexModel class
        public string SelectedApp { get; set; } = string.Empty;

        // Add this property to the IndexModel class
        public List<SelectListItem> Builds { get; set; } = new();

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

            var pairs = ParseSelections(Selections)
                .Where(p => !string.IsNullOrWhiteSpace(p.App) && !string.IsNullOrWhiteSpace(p.Build))
                .Where(p => Directory.Exists(Path.Combine(staging, p.App, p.Build)))
                .ToList();
            if (pairs.Count == 0)
                return new JsonResult(new { success = false, message = "No valid build sources found." });

            var tasks = new List<Task>();
            foreach (var server in SelectedServers)
            {
                foreach (var pair in pairs)
                {
                    var envFlag = _envInShortcutMap.TryGetValue(pair.App, out var f) && f;
                    tasks.Add(CopyAndShortcutAsync(server, pair.App, pair.Build, envFlag, HubConnectionId));
                }
            }
            await Task.WhenAll(tasks);
            return new JsonResult(new { success = true, totalCopies = tasks.Count });
        }

        private async Task CopyAndShortcutAsync(string server, string app, string version, bool envInShortcut, string hubConnectionId)
        {
            try
            {
                var stagingRoot = _config["StagingPath"]!;
                var sourcePath = Path.Combine(stagingRoot, app, version);
                if (!Directory.Exists(sourcePath))
                {
                    await _hubContext.Clients.Client(hubConnectionId)
                        .SendAsync("ReceiveMessage", $"Source not found: {sourcePath}");
                    return;
                }

                var root = _config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured");
                var baseDest = $@"\\{server}\C$\{root}\{app}\{version}";

                if (envInShortcut)
                {
                    // Copy once to version folder
                    await CopyDirectoryAsync(sourcePath, baseDest, hubConnectionId);
                    await CreateShortcutsAsync(server, app, version, envInShortcut, hubConnectionId);
                }
                else
                {
                    // Copy per environment into version\Env
                    foreach (var env in _environments)
                    {
                        var envDest = Path.Combine(baseDest, env);
                        await CopyDirectoryAsync(sourcePath, envDest, hubConnectionId);
                    }
                    await CreateShortcutsAsync(server, app, version, envInShortcut, hubConnectionId);
                }

                await _hubContext.Clients.Client(hubConnectionId)
                    .SendAsync("ReceiveMessage", $"Copy + shortcuts completed for {server}:{app}:{version}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deployment failed for {Server} {App} {Version}", server, app, version);
                await _hubContext.Clients.Client(hubConnectionId)
                    .SendAsync("ReceiveMessage", $"Error for {server}: {ex.Message}");
            }
        }

        private async Task CopyDirectoryAsync(string source, string dest, string hubConnectionId)
        {
            Directory.CreateDirectory(dest);
            var files = Directory.GetFiles(source, "*.*", SearchOption.AllDirectories);
            int total = files.Length;
            int copied = 0;
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(source, file);
                var targetFile = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                System.IO.File.Copy(file, targetFile, true);
                copied++;
                int progress = total == 0 ? 100 : (int)(copied * 100.0 / total);
                await _hubContext.Clients.Client(hubConnectionId)
                    .SendAsync("ReceiveProgress", progress);
            }
        }

        private async Task CreateShortcutsAsync(string server, string app, string version, bool envInShortcut, string hubConnectionId)
        {
            foreach (var env in _environments)
            {
                try
                {
                    CreateShortcutVariant(server, app, version, env, envInShortcut);
                    await _hubContext.Clients.Client(hubConnectionId)
                        .SendAsync("ReceiveMessage", $"Shortcut created: {app} {version} {env}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Shortcut failed {App} {Version} {Env}", app, version, env);
                }
            }
        }

        private void CreateShortcutVariant(string server, string app, string version, string environment, bool envInShortcut)
        {
            if (!_exeMap.TryGetValue(app, out var exeName))
                throw new InvalidOperationException($"No EXE configured for {app}");

            var root = _config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured");

            string targetPath;
            string arguments = "";
            if (envInShortcut)
            {
                // C:\CSTApps\App\Version\Exe + args
                targetPath = $@"C:\{root}\{app}\{version}\{exeName}";
                arguments = $"-Configuration {environment} -Mode {environment}";
            }
            else
            {
                // C:\CSTApps\App\Version\Env\Exe
                targetPath = $@"C:\{root}\{app}\{version}\{environment}\{exeName}";
            }

            var shortcutDir = $@"\\{server}\C$\CSTApps";
            if (!Directory.Exists(shortcutDir))
            {
                _logger.LogWarning("Shortcut directory missing: {Dir}", shortcutDir);
                return;
            }

            var shortcutName = $"{app} {version} {environment}.lnk";
            var remoteShortcutPath = Path.Combine(shortcutDir, shortcutName);

            var tempDir = Path.Combine(Path.GetTempPath(), "ShortcutBuild");
            Directory.CreateDirectory(tempDir);
            var localShortcutPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".lnk");

            var shell = new WshShell();
            var shortcut = (IWshShortcut)shell.CreateShortcut(localShortcutPath);
            shortcut.TargetPath = targetPath;
            if (!string.IsNullOrEmpty(arguments))
                shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? "C:\\";
            shortcut.IconLocation = targetPath;
            shortcut.Description = $"{app} {version} {environment}";
            shortcut.Save();

            if (System.IO.File.Exists(localShortcutPath))
            {
                System.IO.File.Copy(localShortcutPath, remoteShortcutPath, true);
                System.IO.File.Delete(localShortcutPath);
            }
        }

        private (Dictionary<string,string> exeMap, Dictionary<string,bool> envFlag) LoadAppExeMetadata()
        {
            var path = Path.Combine(_env.WebRootPath, "appExes.json");
            var exeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flagMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (System.IO.File.Exists(path))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(path);
                    var list = JsonSerializer.Deserialize<List<AppExeRecord>>(json) ?? new();
                    foreach (var r in list.Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Exe)))
                    {
                        exeMap[r.Name] = r.Exe;
                        flagMap[r.Name] = r.EnvInShortcut;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse appExes.json");
                }
            }
            return (exeMap, flagMap);
        }

        private class AppExeRecord
        {
            public string Name { get; set; } = "";
            public string Exe { get; set; } = "";
            public bool EnvInShortcut { get; set; }
        }

        // Add this method inside the IndexModel class

        private List<AppBuildEntry> ParseSelections(string[] selections)
        {
            var result = new List<AppBuildEntry>();
            foreach (var sel in selections)
            {
                // Expected format: "App|Build" or "App|Build|Environment"
                var parts = sel.Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    result.Add(new AppBuildEntry { App = parts[0], Build = parts[1], Environment = null });
                }
                else if (parts.Length == 3)
                {
                    result.Add(new AppBuildEntry { App = parts[0], Build = parts[1], Environment = parts[2] });
                }
            }
            return result;
        }
    }
}