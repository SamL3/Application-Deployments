using DevApp.Hubs;
using DevApp.Models;
using DevApp.Services;
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
using System.Net.NetworkInformation; // add

namespace DevApp.Pages
{
    public class DeployModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<DeployModel> _logger;
        private readonly Dictionary<string, AppExeEntry> _appExes;
        private readonly IHubContext<CopyHub> _hubContext;
        private readonly HostAvailabilityService _hostSvc;

        public DeployModel(
            IConfiguration config,
            IWebHostEnvironment env,
            ILogger<DeployModel> logger,
            IHubContext<CopyHub> hubContext,
            HostAvailabilityService hostSvc)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _env = env ?? throw new ArgumentNullException(nameof(env));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _hostSvc = hostSvc ?? throw new ArgumentNullException(nameof(hostSvc));
            _appExes = LoadAppExesFromConfig();

            _logger.LogInformation("Deploy ctor: appExes mappings loaded: {Count}", _appExes.Count);
            Debug.WriteLine($"[Deploy] ctor: appExes mappings loaded: {_appExes.Count}. Keys: {string.Join(", ", _appExes.Keys.Take(10))}{(_appExes.Count>10?"...":"")}");
        }

        public string StagingPath { get; private set; } = string.Empty; // UNC source builds
        public string CstAppsRootPath { get; private set; } = string.Empty; // Destination root on targets

        private sealed class ProductConfig
        {
            public string Name { get; set; } = string.Empty;
            public string SubFolder { get; set; } = string.Empty;
            public string Type { get; set; } = "Standard"; // Standard or MSIX
            public bool RequiresEnvironment { get; set; }
            public List<AppExeEntry> Apps { get; set; } = new();
        }

        private sealed class AppExeEntry
        {
            public string Name { get; set; } = string.Empty;
            public string Exe { get; set; } = string.Empty;
            public bool EnvInShortcut { get; set; }
            public string SubFolder { get; set; } = string.Empty;
            public string? ProductName { get; set; } // Which product this app belongs to
            public string? ProductSubFolder { get; set; } // Product's subfolder
            public string? ProductType { get; set; } // Standard or MSIX
            public bool ProductRequiresEnvironment { get; set; } // Does product require environment in path
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
            public string? ProductType { get; set; } // Standard or MSIX
            public bool RequiresEnvironment { get; set; }
        }

        [BindProperty] public List<SelectListItem> Servers { get; set; } = new();
        [BindProperty] public Dictionary<string,bool> ServerAccessibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [BindProperty] public List<ServerInfo> ServerList { get; set; } = new();
        [BindProperty] public string[] SelectedServers { get; set; } = Array.Empty<string>();

        [BindProperty] public string[] SelectedEnvironments { get; set; } = new[] { "Dev", "QA", "UAT" };
        public List<SelectListItem> Environments { get; set; } = new();

        public List<AppBuildGroup> AppBuildGroups { get; set; } = new();
        public List<AppBuildGroup> MSIXBuildGroups { get; set; } = new(); // Separate list for MSIX packages

        public void OnGet()
        {
            // Load servers from appconfig.json
            var serversSection = _config.GetSection("Servers");
            var serversConfig = serversSection.Get<List<ServerInfo>>() ?? new();
            ServerList = serversConfig;

            var statuses = _hostSvc.GetStatuses();
            foreach (var s in ServerList.Where(s => !string.IsNullOrWhiteSpace(s.HostName)))
            {
                if (statuses.TryGetValue(s.HostName, out var st))
                    ServerAccessibility[s.HostName] = st.Accessible;
            }

            // Load environments from appconfig.json (JSON array of strings)
            var envs = _config.GetSection("Environments").Get<string[]>() ?? Array.Empty<string>();
            Environments = envs.Select(e => new SelectListItem { Value = e, Text = e }).ToList();

            // Load configured paths
            StagingPath = _config["StagingPath"] ?? string.Empty; // UNC source
            CstAppsRootPath = _config["CSTAppsRootPath"] ?? @"C:\\CSTApps"; // local root on targets

            // Build inventory, etc.
            BuildInventory();
        }

        private void LoadServers()
        {
            try
            {
                var csvPath = Path.Combine(_env.WebRootPath, _config["CsvFilePath"] ?? "servers.csv");
                if (System.IO.File.Exists(csvPath))
                {
                    var lines = System.IO.File.ReadAllLines(csvPath);
                    ServerList = lines
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l =>
                        {
                            var p = l.Split(',', StringSplitOptions.TrimEntries);
                            return new ServerInfo
                            {
                                HostName = p.ElementAtOrDefault(0) ?? "",
                                UserID = p.ElementAtOrDefault(1) ?? "",
                                Description = p.ElementAtOrDefault(2) ?? ""
                            };
                        })
                        .Where(s => !string.IsNullOrWhiteSpace(s.HostName))
                        .ToList();

                    Servers = ServerList
                        .Select(s => new SelectListItem { Value = s.HostName, Text = s.HostName })
                        .ToList();
                }
                else
                {
                    _logger.LogWarning("LoadServers: servers.csv not found at {Path}", csvPath);
                    ServerList = new();
                    Servers = new();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LoadServers failed");
                ServerList = new();
                Servers = new();
            }
        }

        public async Task<IActionResult> OnPostCopyAsync(
            [FromForm] string[] SelectedServers,
            [FromForm] string[] Selections,
            [FromForm] string HubConnectionId,
            [FromForm] string[] SelectedEnvironments)
        {
            try
            {
                _logger.LogInformation("Copy requested. Hub={Hub} Servers={Servers} Selections={Selections} Envs={Envs}",
                    HubConnectionId,
                    SelectedServers == null ? "null" : string.Join(',', SelectedServers),
                    Selections == null ? "null" : string.Join(',', Selections),
                    (SelectedEnvironments == null || SelectedEnvironments.Length == 0) ? "(none)" : string.Join(',', SelectedEnvironments));

                if (SelectedServers == null || SelectedServers.Length == 0)
                {
                    _logger.LogWarning("No servers selected in request");
                    return new JsonResult(new { success = false, message = "Please select at least one server." });
                }

                if (Selections == null || Selections.Length == 0)
                {
                    _logger.LogWarning("No build selections in request");
                    return new JsonResult(new { success = false, message = "Please select at least one build." });
                }

                var staging = _config["StagingPath"];
                if (string.IsNullOrWhiteSpace(staging))
                {
                    _logger.LogError("StagingPath not configured in appconfig.json");
                    return new JsonResult(new { success = false, message = "StagingPath not configured." });
                }

                _logger.LogInformation("Using staging path: {StagingPath}", staging);

                // Expand selections without an explicit environment into one per selected environment
                var triples = ParseSelections(Selections)
                    .SelectMany(t => t.Environment != null
                        ? new[] { (App: t.App, Build: t.Build, Environment: t.Environment) }
                        : ((SelectedEnvironments != null && SelectedEnvironments.Length > 0)
                            ? SelectedEnvironments.Select(env => (App: t.App, Build: t.Build, Environment: (string?)env))
                            : new[] { (App: t.App, Build: t.Build, Environment: (string?)null) }))
                    .Select(t => 
                    {
                        _appExes.TryGetValue(t.App, out var appEntry);
                        var envInShortcut = appEntry?.EnvInShortcut ?? false;
                        var subFolder = appEntry?.SubFolder ?? string.Empty;
                        var productSubFolder = appEntry?.ProductSubFolder ?? string.Empty;
                        var requiresEnv = appEntry?.ProductRequiresEnvironment ?? false;
                        var productType = appEntry?.ProductType ?? "Standard";
                        
                        // Build source path based on product structure
                        string path;
                        bool exists;
                        
                        if (requiresEnv && t.Environment != null && productType == "MSIX")
                        {
                            // For MSIX: t.Build is the filename (without extension)
                            // Path: staging\ProductSubFolder\Environment\{filename}.msix
                            path = Path.Combine(staging, productSubFolder, t.Environment, t.Build + ".msix");
                            exists = System.IO.File.Exists(path);
                        }
                        else if (requiresEnv && t.Environment != null)
                        {
                            // Path: staging\ProductSubFolder\Environment\Build\SubFolder
                            var basePath = Path.Combine(staging, productSubFolder, t.Environment, t.Build);
                            path = string.IsNullOrEmpty(subFolder) ? basePath : Path.Combine(basePath, subFolder);
                            exists = Directory.Exists(path);
                        }
                        else
                        {
                            // Path: staging\ProductSubFolder\Build\SubFolder[\Environment]
                            var basePath = string.IsNullOrEmpty(subFolder)
                                ? Path.Combine(staging, productSubFolder, t.Build)
                                : Path.Combine(staging, productSubFolder, t.Build, subFolder);
                            path = (!envInShortcut && t.Environment != null) ? Path.Combine(basePath, t.Environment) : basePath;
                            exists = Directory.Exists(path);
                        }
                        
                        _logger.LogInformation("Source check: App='{App}', Build='{Build}', Env='{Env}', Product='{Product}', Type='{Type}', Path='{Path}', Exists={Exists}", 
                            t.App, t.Build, t.Environment ?? "(none)", productSubFolder, productType, path, exists);
                        return (t.App, t.Build, t.Environment, exists);
                    })
                    .Where(t => t.exists)
                    .Select(t => (App: t.App, Build: t.Build, Environment: t.Environment))
                    .ToList();

                if (triples.Count == 0)
                {
                    _logger.LogWarning("No valid build sources found after filtering. Check source paths and existence.");
                    return new JsonResult(new { success = false, message = "No valid build sources found. Please verify staging path and build selections." });
                }

                _logger.LogInformation("Starting deployment tasks. Valid triples found: {Count}", triples.Count);

                var tasks = new List<Task>();
                foreach (var server in SelectedServers)
                    foreach (var t in triples)
                        tasks.Add(CopyFilesAsync(server, t.App, t.Build, t.Environment, HubConnectionId));

                await Task.WhenAll(tasks);
                
                _logger.LogInformation("All deployment tasks completed successfully. Total tasks: {Count}", tasks.Count);
                return new JsonResult(new { success = true, totalCopies = tasks.Count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure during deployment batch.");
                
                if (!string.IsNullOrWhiteSpace(HubConnectionId))
                {
                    try
                    {
                        await _hubContext.Clients.Client(HubConnectionId)
                            .SendAsync("ReceiveError", $"Deployment batch failed: {ex.Message}");
                    }
                    catch (Exception hubEx)
                    {
                        _logger.LogError(hubEx, "Failed to send error message to SignalR client");
                    }
                }
                
                return new JsonResult(new { success = false, message = $"Deployment failed: {ex.Message}" });
            }
        }

        private void BuildInventory()
        {
            var sw = Stopwatch.StartNew();
            AppBuildGroups = new List<AppBuildGroup>();
            MSIXBuildGroups = new List<AppBuildGroup>();

            var inventoryRoot = _config["StagingPath"] ?? throw new InvalidOperationException("StagingPath not configured");
            _logger.LogInformation("BuildInventory: inventoryRoot={Path}", inventoryRoot);
            Debug.WriteLine($"[Deploy] BuildInventory: inventoryRoot={inventoryRoot}");

            if (!Directory.Exists(inventoryRoot))
            {
                _logger.LogWarning("Inventory root path missing: {Path}", inventoryRoot);
                Debug.WriteLine($"[Deploy] BuildInventory: inventory root path missing: {inventoryRoot}");
                return;
            }

            // Get all build version directories or product directories
            string[] buildDirs;
            try
            {
                buildDirs = Directory.GetDirectories(inventoryRoot);
                _logger.LogInformation("BuildInventory: Found {Count} directories in {Path}", buildDirs.Length, inventoryRoot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BuildInventory: Failed to read directories from {Path}. Check permissions.", inventoryRoot);
                return;
            }

            if (_appExes == null || _appExes.Count == 0)
            {
                _logger.LogError("BuildInventory: No AppExes configured in appconfig.json. AppExes count: {Count}. Please add Products section.", _appExes?.Count ?? 0);
                return;
            }

            // Process each configured app
            foreach (var kvp in _appExes)
            {
                var appName = kvp.Key;
                var exeEntry = kvp.Value;
                var exeName = exeEntry.Exe;
                var subFolder = exeEntry.SubFolder;
                var productSubFolder = exeEntry.ProductSubFolder ?? "";
                var requiresEnv = exeEntry.ProductRequiresEnvironment;
                var productType = exeEntry.ProductType ?? "Standard";
                
                _logger.LogInformation("BuildInventory: Processing app '{App}' Product='{Product}', Type='{Type}', RequiresEnv={RequiresEnv}", 
                    appName, exeEntry.ProductName ?? "None", productType, requiresEnv);

                var variants = new List<AppBuildVariant>();

                // For products that require environment (like WinMobile), look in environment folders first
                if (requiresEnv)
                {
                    // Path: inventoryRoot\ProductSubFolder\Environment\{MSIX files directly here}
                    var productPath = Path.Combine(inventoryRoot, productSubFolder);
                    
                    if (!Directory.Exists(productPath))
                    {
                        _logger.LogWarning("BuildInventory: Product path does not exist: '{Path}'", productPath);
                        continue;
                    }

                    var envDirs = Directory.GetDirectories(productPath);
                    _logger.LogInformation("BuildInventory: Found {Count} environment directories in {Path}", envDirs.Length, productPath);

                    foreach (var envDir in envDirs)
                    {
                        var envName = Path.GetFileName(envDir);
                        if (string.IsNullOrWhiteSpace(envName)) continue;

                        // For MSIX, files are directly in the environment folder
                        var msixFiles = Directory.GetFiles(envDir, exeName, SearchOption.TopDirectoryOnly);
                        
                        _logger.LogInformation("BuildInventory: Found {Count} MSIX files in {Env}", msixFiles.Length, envName);
                        
                        foreach (var msixFile in msixFiles)
                        {
                            var fileName = Path.GetFileName(msixFile);
                            // Use filename (without extension) as the "build" identifier
                            var buildName = Path.GetFileNameWithoutExtension(fileName);
                            
                            variants.Add(new AppBuildVariant { Build = buildName, Environment = envName });
                            _logger.LogInformation("BuildInventory: Found {App} MSIX: {FileName} in {Env}", appName, fileName, envName);
                        }
                    }
                }
                else
                {
                    // Standard products: inventoryRoot\ProductSubFolder\Build\SubFolder
                    var productPath = Path.Combine(inventoryRoot, productSubFolder);
                    
                    if (!Directory.Exists(productPath))
                    {
                        _logger.LogWarning("BuildInventory: Product path does not exist: '{Path}'", productPath);
                        continue;
                    }

                    var productBuildDirs = Directory.GetDirectories(productPath);

                    foreach (var buildDir in productBuildDirs)
                    {
                        var buildName = Path.GetFileName(buildDir);
                        if (string.IsNullOrWhiteSpace(buildName)) continue;

                        // Construct full path including subfolder if specified
                        var appBuildPath = string.IsNullOrEmpty(subFolder)
                            ? buildDir
                            : Path.Combine(buildDir, subFolder);
                        
                        _logger.LogInformation("BuildInventory: Checking path for {App}/{Build}: '{Path}'", appName, buildName, appBuildPath);

                        if (!Directory.Exists(appBuildPath))
                        {
                            _logger.LogWarning("BuildInventory: Path does not exist: '{Path}'", appBuildPath);
                            continue;
                        }

                        // Check for exe directly in the build path (neutral/no environment)
                        var neutralExePath = Path.Combine(appBuildPath, exeName);
                        var neutralExe = !string.IsNullOrEmpty(exeName) && System.IO.File.Exists(neutralExePath);

                        if (neutralExe)
                        {
                            variants.Add(new AppBuildVariant { Build = buildName, Environment = null });
                        }
                        else
                        {
                            // Check for environment-specific subdirectories
                            var envDirs = Directory.GetDirectories(appBuildPath);
                            foreach (var envDir in envDirs)
                            {
                                var envName = Path.GetFileName(envDir);
                                if (string.IsNullOrWhiteSpace(envName)) continue;

                                var envExePath = Path.Combine(envDir, exeName);
                                var hasExe = !string.IsNullOrEmpty(exeName) && System.IO.File.Exists(envExePath);

                                if (hasExe)
                                {
                                    variants.Add(new AppBuildVariant { Build = buildName, Environment = envName });
                                }
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
                                string path;
                                
                                if (requiresEnv && v.Environment != null)
                                {
                                    // For MSIX: v.Build is the filename, get file creation time
                                    // Path: inventoryRoot\ProductSubFolder\Environment\{filename}.msix
                                    var msixFilePath = Path.Combine(inventoryRoot, productSubFolder, v.Environment, v.Build + ".msix");
                                    if (System.IO.File.Exists(msixFilePath))
                                    {
                                        return System.IO.File.GetCreationTime(msixFilePath);
                                    }
                                    return DateTime.MinValue;
                                }
                                else
                                {
                                    // Standard product: use directory creation time
                                    var basePath = string.IsNullOrEmpty(subFolder)
                                        ? Path.Combine(inventoryRoot, productSubFolder, v.Build)
                                        : Path.Combine(inventoryRoot, productSubFolder, v.Build, subFolder);
                                    path = v.Environment == null ? basePath : Path.Combine(basePath, v.Environment);
                                    return Directory.GetCreationTime(path);
                                }
                            }
                            catch { return DateTime.MinValue; }
                        })
                        .Take(10) // Limit to 10 most recent builds
                        .ToList();

                    var buildGroup = new AppBuildGroup
                    {
                        AppName = appName,
                        Variants = variants,
                        ProductType = productType,
                        RequiresEnvironment = requiresEnv
                    };

                    // Separate MSIX from Standard apps
                    if (productType == "MSIX")
                    {
                        MSIXBuildGroups.Add(buildGroup);
                    }
                    else
                    {
                        AppBuildGroups.Add(buildGroup);
                    }
                }
            }

            AppBuildGroups = AppBuildGroups.OrderBy(g => g.AppName).ToList();
            MSIXBuildGroups = MSIXBuildGroups.OrderBy(g => g.AppName).ToList();

            // Keep Model.Environments exactly as loaded from configuration; do not augment with disk findings.

            sw.Stop();
            _logger.LogInformation("BuildInventory completed: appsListed={Apps} envs={Envs} elapsed={Ms}ms",
                AppBuildGroups.Count, Environments.Count, sw.ElapsedMilliseconds);
            Debug.WriteLine($"[Deploy] BuildInventory done: appsListed={AppBuildGroups.Count}, envs={Environments.Count}, elapsed={sw.ElapsedMilliseconds}ms");
        }
            
        private static bool FilesAreSame(FileInfo src, FileInfo dst)
        {
            if (src.Length != dst.Length) return false;
            // SMB/FAT32 can have coarse time resolution; allow small skew
            var delta = (src.LastWriteTimeUtc - dst.LastWriteTimeUtc).Duration();
            return delta <= TimeSpan.FromSeconds(2);
        }

        private static string ToFileUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            if (path.StartsWith(@"\\"))
                return "file:" + path.Replace("\\", "/"); // -> file://server/C$/CSTApps
            return "file:///" + path.Replace("\\", "/");  // -> file:///C:/CSTApps
        }

        private async Task CopyFilesAsync(string selectedServer, string selectedApp, string selectedBuild, string? environment, string hubConnectionId)
        {
            try
            {
                var stagingRoot = _config["StagingPath"]!;
                _appExes.TryGetValue(selectedApp, out var entry);
                var envInShortcut = entry?.EnvInShortcut ?? false;
                var subFolder = entry?.SubFolder ?? string.Empty;
                var productSubFolder = entry?.ProductSubFolder ?? string.Empty;
                var requiresEnv = entry?.ProductRequiresEnvironment ?? false;
                var productType = entry?.ProductType ?? "Standard";

                // Build source path based on product structure
                string sourcePath;
                bool isMSIXFile = false;
                
                if (requiresEnv && environment != null && productType == "MSIX")
                {
                    // For MSIX: selectedBuild is the filename (without extension)
                    // Path: stagingRoot\ProductSubFolder\Environment\{filename}.msix
                    sourcePath = Path.Combine(stagingRoot, productSubFolder, environment, selectedBuild + ".msix");
                    isMSIXFile = true;
                }
                else if (requiresEnv && environment != null)
                {
                    // Path: stagingRoot\ProductSubFolder\Environment\Build\SubFolder
                    var basePath = Path.Combine(stagingRoot, productSubFolder, environment, selectedBuild);
                    sourcePath = string.IsNullOrEmpty(subFolder) ? basePath : Path.Combine(basePath, subFolder);
                }
                else
                {
                    // Path: stagingRoot\ProductSubFolder\Build\SubFolder[\Environment]
                    var basePath = string.IsNullOrEmpty(subFolder)
                        ? Path.Combine(stagingRoot, productSubFolder, selectedBuild)
                        : Path.Combine(stagingRoot, productSubFolder, selectedBuild, subFolder);
                    sourcePath = (!envInShortcut && environment != null) ? Path.Combine(basePath, environment) : basePath;
                }

                // Handle MSIX file copy differently
                if (isMSIXFile)
                {
                    if (!System.IO.File.Exists(sourcePath))
                    {
                        await _hubContext.Clients.Client(hubConnectionId)
                            .SendAsync("ReceiveError", $"MSIX file not found: {sourcePath}");
                        return;
                    }

                    var msixCstAppsRoot = _config["CSTApps"] ?? "CSTApps";
                    var destFolder = $@"\\{selectedServer}\C$\{msixCstAppsRoot}\MSIX";
                    var destFile = Path.Combine(destFolder, Path.GetFileName(sourcePath));

                    try { Directory.CreateDirectory(destFolder); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to create MSIX destination folder {Dest}", destFolder);
                        await _hubContext.Clients.Client(hubConnectionId)
                            .SendAsync("ReceiveError", $"Cannot create MSIX folder: {destFolder} ({ex.Message})");
                        return;
                    }

                    try
                    {
                        System.IO.File.Copy(sourcePath, destFile, true);
                        await _hubContext.Clients.Client(hubConnectionId)
                            .SendAsync("ReceiveMessage", $"MSIX copied: {Path.GetFileName(sourcePath)} to {selectedServer}");
                        await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveProgress", 100);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed copying MSIX {Source} to {Dest}", sourcePath, destFile);
                        await _hubContext.Clients.Client(hubConnectionId)
                            .SendAsync("ReceiveError", $"MSIX copy failed: {ex.Message}");
                    }
                    return;
                }

                if (!Directory.Exists(sourcePath))
                {
                    await _hubContext.Clients.Client(hubConnectionId)
                        .SendAsync("ReceiveError", $"Source not found: {sourcePath}");
                    return;
                }

                var cstAppsRoot = _config["CSTApps"] ?? "CSTApps";
                var baseDest = $@"\\{selectedServer}\C$\{cstAppsRoot}\{selectedApp}\{selectedBuild}";
                var destPath = (!envInShortcut && environment != null) ? Path.Combine(baseDest, environment) : baseDest;

                try { Directory.CreateDirectory(destPath); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create destination folder {Dest}", destPath);
                    await _hubContext.Clients.Client(hubConnectionId)
                        .SendAsync("ReceiveError", $"Cannot create destination: {destPath} ({ex.Message})");
                    return;
                }

                var files = Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories);
                int total = files.Length, copied = 0, failed = 0;
                foreach (var file in files)
                {
                    var rel = Path.GetRelativePath(sourcePath, file);
                    var destFile = Path.Combine(destPath, rel);

                    try { Directory.CreateDirectory(Path.GetDirectoryName(destFile)!); }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "Failed creating directory for {DestFile}", destFile);
                        await _hubContext.Clients.Client(hubConnectionId)
                            .SendAsync("ReceiveError", $"Failed to create folder for: {destFile} ({ex.Message})");
                        int p1 = total == 0 ? 100 : (int)((copied + failed) * 100.0 / total);
                        await _hubContext.Clients.Client(hubConnectionId).SendAsync("ReceiveProgress", p1);
                        continue;
                    }

                    try
                    {
                        // Skip copying if destination exists and file is effectively the same
                        if (System.IO.File.Exists(destFile))
                        {
                            var srcInfo = new FileInfo(file);
                            var dstInfo = new FileInfo(destFile);
                            if (!FilesAreSame(srcInfo, dstInfo))
                            {
                                System.IO.File.Copy(file, destFile, true);
                            }
                        }
                        else
                        {
                            System.IO.File.Copy(file, destFile, false);
                        }

                        // Count skipped files as processed so progress reaches 100%
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
                    .SendAsync("ReceiveMessage", $"Copy summary for {selectedServer} � ok: {copied}, failed: {failed}");

                if (failed == 0)
                {
                    var remoteShortcutPath = CreateShortcut(selectedServer, selectedApp, selectedBuild, environment);
                    if (!string.IsNullOrEmpty(remoteShortcutPath))
                    {
                        var folder = Path.GetDirectoryName(remoteShortcutPath)!;
                        var link = ToFileUrl(folder);
                        var name = Path.GetFileName(remoteShortcutPath);
                        await _hubContext.Clients.Client(hubConnectionId)
                            .SendAsync("ReceiveMessage", $"Shortcut created: {name} � {folder} ({link})");
                    }
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

        private string? CreateShortcut(string selectedServer, string selectedApp, string selectedBuild, string? environment)
        {
            var stagingAppsRoot = _config["CSTApps"] ?? throw new InvalidOperationException("CSTApps not configured");
            var exeName = _appExes.TryGetValue(selectedApp, out var entry)
                ? entry.Exe
                : throw new InvalidOperationException($"No EXE configured for app '{selectedApp}' (appconfig.json).");

            var envInShortcut = entry!.EnvInShortcut;

            var basePath = $@"C:\{stagingAppsRoot}\{selectedApp}\{selectedBuild}";
            var targetOnRemote = envInShortcut
                ? Path.Combine(basePath, exeName)
                : (environment == null ? Path.Combine(basePath, exeName) : Path.Combine(basePath, environment, exeName));

            var remoteShortcutDir = $@"\\{selectedServer}\C$\CSTApps";
            if (!Directory.Exists(remoteShortcutDir))
            {
                _logger.LogWarning("Remote shortcut dir missing: {Dir}", remoteShortcutDir);
                return null;
            }

            var shortcutName = environment == null
                ? $"{selectedApp} {selectedBuild}.lnk"
                : $"{selectedApp} {environment} {selectedBuild}.lnk";

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
                if (envInShortcut && !string.IsNullOrWhiteSpace(environment))
                {
                    shortcut.Arguments = $"-Mode {environment} -Configuration {environment}";
                }
                shortcut.Save();

                if (!System.IO.File.Exists(localShortcutPath))
                {
                    _logger.LogError("Local shortcut not created: {LocalPath}", localShortcutPath);
                    return null;
                }

                System.IO.File.Copy(localShortcutPath, remoteShortcutPath, true);
                _logger.LogInformation("Shortcut deployed: {Path}", remoteShortcutPath);
                return remoteShortcutPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed shortcut creation for Server={Server} App={App} Build={Build} Env={Env}",
                    selectedServer, selectedApp, selectedBuild, environment ?? "(none)");
                return null;
            }
            finally
            {
                try { if (System.IO.File.Exists(localShortcutPath)) System.IO.File.Delete(localShortcutPath); } catch { }
            }
        }

        private Dictionary<string, AppExeEntry> LoadAppExesFromConfig()
        {
            // Try new Products structure first
            var productsSection = _config.GetSection("Products");
            var products = productsSection.Get<List<ProductConfig>>();
            
            if (products != null && products.Count > 0)
            {
                _logger.LogInformation("LoadAppExesFromConfig: Loading from Products structure. Found {Count} products", products.Count);
                
                var appDict = new Dictionary<string, AppExeEntry>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var product in products)
                {
                    _logger.LogInformation("LoadAppExesFromConfig: Processing product '{Product}' with {AppCount} apps", 
                        product.Name, product.Apps?.Count ?? 0);
                    
                    if (product.Apps == null) continue;
                    
                    foreach (var app in product.Apps)
                    {
                        if (string.IsNullOrWhiteSpace(app.Name) || string.IsNullOrWhiteSpace(app.Exe))
                            continue;
                        
                        // Enrich app with product information
                        app.ProductName = product.Name;
                        app.ProductSubFolder = product.SubFolder;
                        app.ProductType = product.Type;
                        app.ProductRequiresEnvironment = product.RequiresEnvironment;
                        
                        appDict[app.Name] = app;
                    }
                }
                
                return appDict;
            }
            
            // Fallback to legacy AppExes structure
            var section = _config.GetSection("AppExes");
            var entries = section.Get<List<AppExeEntry>>();
            if (entries == null)
            {
                _logger.LogWarning("LoadAppExesFromConfig: Neither Products nor AppExes section found in appconfig.json");
                return new Dictionary<string, AppExeEntry>(StringComparer.OrdinalIgnoreCase);
            }
            
            _logger.LogInformation("LoadAppExesFromConfig: Using legacy AppExes structure. Found {Count} apps", entries.Count);
            return entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.Exe))
                .ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<(string App, string Build, string? Environment)> ParseSelections(IEnumerable<string> selections)
        {
            foreach (var s in selections)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                var parts = s.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

        public string GetSearchLocations()
        {
            // Show the configured staging root path used to find builds
            return _config["StagingPath"] ?? string.Empty;
        }

        public IActionResult OnGetDiagnostics()
        {
            var diagnostics = new List<string>();
            
            try
            {
                var stagingPath = _config["StagingPath"] ?? "(not configured)";
                diagnostics.Add($"StagingPath: {stagingPath}");
                
                diagnostics.Add($"Current User: {Environment.UserName}");
                diagnostics.Add($"Machine Name: {Environment.MachineName}");
                diagnostics.Add($"App Running: Yes");
                
                if (!string.IsNullOrWhiteSpace(stagingPath) && stagingPath != "(not configured)")
                {
                    try
                    {
                        var exists = Directory.Exists(stagingPath);
                        diagnostics.Add($"StagingPath Exists: {exists}");
                        
                        if (exists)
                        {
                            var dirs = Directory.GetDirectories(stagingPath);
                            diagnostics.Add($"Build Directories Found: {dirs.Length}");
                            if (dirs.Length > 0)
                            {
                                diagnostics.Add($"Sample Builds: {string.Join(", ", dirs.Select(Path.GetFileName).Take(5))}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"ERROR accessing StagingPath: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                
                diagnostics.Add($"AppExes Configured: {_appExes?.Count ?? 0}");
                if (_appExes != null && _appExes.Count > 0)
                {
                    foreach (var kvp in _appExes)
                    {
                        diagnostics.Add($"  - {kvp.Key}: Exe={kvp.Value.Exe}, SubFolder={kvp.Value.SubFolder}, EnvInShortcut={kvp.Value.EnvInShortcut}");
                    }
                }
                else
                {
                    diagnostics.Add("ERROR: AppExes section is missing or empty in appconfig.json!");
                    diagnostics.Add("Add this to appconfig.json:");
                    diagnostics.Add("  \"AppExes\": [");
                    diagnostics.Add("    { \"Name\": \"Admin\", \"Exe\": \"CST.Host.App.exe\", \"SubFolder\": \"Client\\\\CST.Host.App\", \"EnvInShortcut\": true },");
                    diagnostics.Add("    { \"Name\": \"TIM\", \"Exe\": \"CST.TIM.App.exe\", \"SubFolder\": \"Client\\\\CST.TIM.App\", \"EnvInShortcut\": true }");
                    diagnostics.Add("  ]");
                }
                
                diagnostics.Add($"Standard AppBuildGroups Loaded: {AppBuildGroups?.Count ?? 0}");
                diagnostics.Add($"MSIX BuildGroups Loaded: {MSIXBuildGroups?.Count ?? 0}");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"DIAGNOSTIC ERROR: {ex.GetType().Name}: {ex.Message}");
                diagnostics.Add($"Stack: {ex.StackTrace}");
            }
            
            return new JsonResult(new { diagnostics });
        }

        private bool IsServerAccessible(string host, string? root)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            if (!QuickPing(host)) return false;
            if (!string.IsNullOrWhiteSpace(root))
            {
                try
                {
                    var unc = $@"\\{host}\C$\{root}";
                    if (Directory.Exists(unc)) return true;
                }
                catch { return false; }
                return false;
            }
            return true;
        }

        private bool QuickPing(string host)
        {
            try
            {
                using var p = new Ping();
                var r = p.Send(host, 500);
                return r != null && r.Status == IPStatus.Success;
            }
            catch { return false; }
        }
    }
}
