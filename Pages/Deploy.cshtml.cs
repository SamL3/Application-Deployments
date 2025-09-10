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
using System.Diagnostics;
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

            _logger.LogInformation("Deploy ctor: appExes mappings loaded: {Count}", _appExes.Count);
            Debug.WriteLine($"[Deploy] ctor: appExes mappings loaded: {_appExes.Count}. Keys: {string.Join(", ", _appExes.Keys.Take(10))}{(_appExes.Count>10?"...":"")}");
        }

        private sealed class AppExeEntry
        {
            public string Name { get; set; } = string.Empty;
            public string Exe { get; set; } = string.Empty;
            public bool EnvInShortcut { get; set; }
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
            _logger.LogInformation("OnGet: Loaded {Count} servers", ServerList.Count);
            Debug.WriteLine($"[Deploy] OnGet: Loaded {ServerList.Count} servers");
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

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure during deployment batch.");
                await _hubContext.Clients.Client(HubConnectionId)
                    .SendAsync("ReceiveError", $"Deployment batch failed: {ex.Message}");
                return new JsonResult(new { success = false, message = "One or more deployments failed unexpectedly." });
            }

            return new JsonResult(new { success = true, totalCopies = tasks.Count });
        }

        private void BuildInventory()
        {
            var sw = Stopwatch.StartNew();
            AppBuildGroups = new List<AppBuildGroup>();

            var stagingPath = _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured");
            _logger.LogInformation("BuildInventory: stagingPath={Path}", stagingPath);
            Debug.WriteLine($"[Deploy] BuildInventory: stagingPath={stagingPath}");

            if (!Directory.Exists(stagingPath))
            {
                _logger.LogWarning("Staging path missing: {Path}", stagingPath);
                Debug.WriteLine($"[Deploy] BuildInventory: staging path missing: {stagingPath}");
                return;
            }

            var appDirs = Directory.GetDirectories(stagingPath);
            _logger.LogInformation("BuildInventory: Found {Count} app directories", appDirs.Length);
            Debug.WriteLine($"[Deploy] BuildInventory: Found {appDirs.Length} app directories");

            var envSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var appDir in appDirs)
            {
                var appName = Path.GetFileName(appDir);
                if (string.IsNullOrWhiteSpace(appName)) continue;

                _appExes.TryGetValue(appName, out var exeName);
                exeName ??= string.Empty;

                _logger.LogInformation("BuildInventory: App={App} exeName={Exe}", appName, string.IsNullOrEmpty(exeName) ? "(not configured)" : exeName);
                Debug.WriteLine($"[Deploy] App={appName} exeName={(string.IsNullOrEmpty(exeName)?"(not configured)":exeName)}");

                var variants = new List<AppBuildVariant>();
                var buildDirs = Directory.GetDirectories(appDir);
                Debug.WriteLine($"[Deploy]  App={appName}: build dirs={buildDirs.Length}");

                foreach (var buildDir in buildDirs)
                {
                    var buildName = Path.GetFileName(buildDir);
                    if (string.IsNullOrWhiteSpace(buildName)) continue;

                    var neutralExePath = Path.Combine(buildDir, exeName);
                    var neutralExe = !string.IsNullOrEmpty(exeName) && System.IO.File.Exists(neutralExePath);
                    Debug.WriteLine($"[Deploy]   Build={buildName}: neutralExePath={neutralExePath} exists={neutralExe}");

                    if (neutralExe)
                    {
                        variants.Add(new AppBuildVariant { Build = buildName, Environment = null });
                    }
                    else
                    {
                        var envDirs = Directory.GetDirectories(buildDir);
                        Debug.WriteLine($"[Deploy]   Build={buildName}: env dirs={envDirs.Length}");
                        foreach (var envDir in envDirs)
                        {
                            var envName = Path.GetFileName(envDir);
                            if (string.IsNullOrWhiteSpace(envName)) continue;

                            var envExePath = Path.Combine(envDir, exeName);
                            var hasExe = !string.IsNullOrEmpty(exeName) && System.IO.File.Exists(envExePath);
                            Debug.WriteLine($"[Deploy]    Env={envName}: exePath={envExePath} exists={hasExe}");

                            if (hasExe)
                            {
                                variants.Add(new AppBuildVariant { Build = buildName, Environment = envName });
                                envSet.Add(envName);
                            }
                        }
                    }
                }

                if (variants.Count > 0)
                {
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

                    _logger.LogInformation("BuildInventory: App={App} variants={Count}", appName, variants.Count);
                    Debug.WriteLine($"[Deploy] App={appName}: variants={variants.Count} -> [{string.Join(", ", variants.Take(5).Select(v => v.Display))}{(variants.Count>5?", ...":"")}]");
                }
                else
                {
                    _logger.LogWarning("BuildInventory: App={App} had 0 variants (exe missing in builds?)", appName);
                    Debug.WriteLine($"[Deploy] App={appName}: 0 variants (exe missing?)");
                }
            }

            AppBuildGroups = AppBuildGroups.OrderBy(g => g.AppName).ToList();
            Environments = envSet.OrderBy(e => e).Select(e => new SelectListItem { Value = e, Text = e }).ToList();

            sw.Stop();
            _logger.LogInformation("BuildInventory completed: appsListed={Apps} envs={Envs} elapsed={Ms}ms",
                AppBuildGroups.Count, Environments.Count, sw.ElapsedMilliseconds);
            Debug.WriteLine($"[Deploy] BuildInventory done: appsListed={AppBuildGroups.Count}, envs={Environments.Count}, elapsed={sw.ElapsedMilliseconds}ms");
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
                    await _hubContext.Clients.Client(hubConnectionId)
                        .SendAsync("ReceiveError", $"Source not found: {sourcePath}");
                    return;
                }

                var cstAppsRoot = _config["CSTApps"] ?? "CSTApps";
                var destPath = environment == null
                    ? $@"\\{selectedServer}\C$\{cstAppsRoot}\{selectedApp}\{selectedBuild}"
                    : $@"\\{selectedServer}\C$\{cstAppsRoot}\{selectedApp}\{selectedBuild}\{environment}";

                try
                {
                    Directory.CreateDirectory(destPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create destination folder {Dest}", destPath);
                    await _hubContext.Clients.Client(hubConnectionId)
                        .SendAsync("ReceiveError", $"Cannot create destination: {destPath} ({ex.Message})");
                    return;
                }

                var files = Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories);
                int total = files.Length;
                int copied = 0;
                int failed = 0;

                foreach (var file in files)
                {
                    var rel = Path.GetRelativePath(sourcePath, file);
                    var destFile = Path.Combine(destPath, rel);

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "Failed creating directory for {DestFile}", destFile);
                        await _hubContext.Clients.Client(hubConnectionId)
                            .SendAsync("ReceiveError", $"Failed to create folder for: {destFile} ({ex.Message})");
                        int progress1 = total == 0 ? 100 : (int)((copied + failed) * 100.0 / total);
                        await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveProgress", progress1);
                        continue;
                    }

                    try
                    {
                        System.IO.File.Copy(file, destFile, true);
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "Failed copying {Source} to {Dest}", file, destFile);
                        await _hubContext.Clients.Client(hubConnectionId)
                            .SendAsync("ReceiveError", $"Copy failed: {rel} ({ex.Message})");
                    }

                    int progress = total == 0 ? 100 : (int)((copied + failed) * 100.0 / total);
                    await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveProgress", progress);
                }

                await _hubContext.Clients.Client(hubConnectionId)
                    .SendAsync("ReceiveMessage", $"Copy summary for {selectedServer} — ok: {copied}, failed: {failed}");

                if (failed == 0)
                {
                    CreateShortcut(selectedServer, selectedApp, selectedBuild, environment);
                    await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveMessage", "Shortcut created.");
                }
                else
                {
                    await _hubContext.Clients.Client(hubConnectionId)
                        .SendAsync("ReceiveError", "Skipped shortcut creation due to copy errors.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Copy failed for {Server}", selectedServer);
                await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveError", $"Unexpected error: {ex.Message}");
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
            if (!System.IO.File.Exists(appExesPath))
            {
                _logger.LogWarning("LoadAppExes: appExes.json not found at {Path}", appExesPath);
                Debug.WriteLine($"[Deploy] LoadAppExes: file not found: {appExesPath}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var jsonString = System.IO.File.ReadAllText(appExesPath);
                Debug.WriteLine($"[Deploy] LoadAppExes: Read {jsonString.Length} chars from appExes.json");

                using var doc = JsonDocument.Parse(jsonString);
                var kind = doc.RootElement.ValueKind;

                // 1) Try the dictionary format: { "AppA": "AppA.exe", ... }
                if (kind == JsonValueKind.Object)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);
                    if (dict != null && dict.Count > 0)
                    {
                        _logger.LogInformation("LoadAppExes: Parsed dict format with {Count} entries", dict.Count);
                        Debug.WriteLine($"[Deploy] LoadAppExes: dict format entries={dict.Count}");
                        return new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
                    }
                }
                // 2) Try the list format saved by Config: [ { "name": "...", "exe": "...", "envInShortcut": true }, ... ]
                else if (kind == JsonValueKind.Array)
                {
                    var list = JsonSerializer.Deserialize<List<AppExeEntry>>(jsonString);
                    if (list != null && list.Count > 0)
                    {
                        var mapped = list
                            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.Exe))
                            .ToDictionary(e => e.Name, e => e.Exe, StringComparer.OrdinalIgnoreCase);

                        _logger.LogInformation("LoadAppExes: Parsed list format with {Count} valid entries", mapped.Count);
                        Debug.WriteLine($"[Deploy] LoadAppExes: list format entries={mapped.Count}");
                        return mapped;
                    }
                }
                else
                {
                    _logger.LogWarning("LoadAppExes: Unexpected JSON root type: {Kind}", kind);
                }

                _logger.LogWarning("LoadAppExes: appExes.json parsed but contained no mappings.");
                Debug.WriteLine("[Deploy] LoadAppExes: parsed but no mappings");
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "appExes.json is not valid JSON.");
                Debug.WriteLine($"[Deploy] LoadAppExes: JsonException: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading appExes.json.");
                Debug.WriteLine($"[Deploy] LoadAppExes: Exception: {ex.Message}");
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<(string App, string Build, string? Environment)> ParseSelections(IEnumerable<string> selections)
        {
            foreach (var s in selections)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                var parts = s.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) continue;

                // App|Build or App|Build|Env
                if (parts.Length == 2)
                {
                    yield return (parts[0], parts[1], null);
                }
                else if (parts.Length == 3)
                {
                    yield return (parts[0], parts[1], parts[2]);
                }
            }
        }
    }
}
