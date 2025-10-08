using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using ApplicationDeployment.Models;
using ApplicationDeployment.Services;

namespace ApplicationDeployment.Pages
{
    public class DashboardModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<DashboardModel> _logger;
        private readonly HostAvailabilityService _hostSvc;

        public DashboardModel(IConfiguration config, IWebHostEnvironment env,
            ILogger<DashboardModel> logger, HostAvailabilityService hostSvc)
        {
            _config = config;
            _env = env;
            _logger = logger;
            _hostSvc = hostSvc;
        }

        private bool ShowApps => _config.GetValue<bool>("Dashboard:ShowApps", false);
        private string RootApps => _config["CSTApps"] ?? string.Empty;

        public IReadOnlyDictionary<string, HostStatus> HostStatuses => _hostSvc.GetStatuses();

        public void OnGet() { }

        public IActionResult OnGetHostStatuses()
        {
            var statuses = _hostSvc.GetStatuses().Values
                .OrderBy(s => s.Host, StringComparer.OrdinalIgnoreCase);
            return new JsonResult(new
            {
                scanInProgress = _hostSvc.ScanInProgress,
                completed = _hostSvc.Completed,
                total = _hostSvc.Total,
                statuses
            });
        }

        public async Task<IActionResult> OnPostRefreshHostsAsync()
        {
            await _hostSvc.TriggerScanAsync();
            return new JsonResult(new { started = true });
        }

        public IActionResult OnGetServers()
        {
            try
            {
                // Build lookup of descriptions from configuration (supports objects or plain strings)
                var descriptions = _config.GetSection("Servers").GetChildren()
                    .Select(c => new
                    {
                        Host = c.GetValue<string>("HostName") ?? c.Get<string>(),
                        Description = c.GetValue<string>("Description")
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Host))
                    .GroupBy(x => x.Host!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Description, StringComparer.OrdinalIgnoreCase);

                var servers = _hostSvc.GetStatuses().Values
                    .OrderBy(s => s.Host, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new
                    {
                        host = s.Host,
                        description = descriptions.TryGetValue(s.Host, out var d) ? d : null,
                        accessible = s.Accessible,
                        latencyMs = s.LatencyMs,
                        message = s.Message
                    })
                    .ToList();

                return new JsonResult(servers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list servers");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        public IActionResult OnGetServerDetails(string server)
        {
            if (string.IsNullOrWhiteSpace(server))
                return BadRequest("server required");

            try
            {
                var disks = GetRemoteLogicalDisks(server)
                    .Where(d => d.Name.Equals("C:", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var deployments = GetDeployments(server, RootApps);
                var installedApps = ShowApps ? GetRemoteInstalledApps(server) : new List<InstalledApp>();

                var appPayload = ShowApps
                    ? installedApps.Select(a => new AppInfo
                    {
                        DisplayName = a.DisplayName,
                        DisplayVersion = a.DisplayVersion,
                        Publisher = a.Publisher
                    }).ToList()
                    : new List<AppInfo>();

                var totalBuildSizeMB = deployments.Sum(d => d.TotalSizeMB);
                var totalBuildCount = deployments.Sum(d => d.BuildCount);

                return new JsonResult(new
                {
                    server,
                    disks = disks.Select(d => new { name = d.Name, volumeName = d.VolumeName, size = d.Size, free = d.Free }),
                    deployments = deployments.Select(d => new
                    {
                        app = d.App,
                        buildCount = d.BuildCount,
                        totalSizeMB = d.TotalSizeMB,
                        builds = d.Builds.Select(b => new { name = b.Name, sizeMB = b.SizeMB })
                    }),
                    apps = appPayload,
                    totalBuildSizeMB,
                    totalBuildCount,
                    showApps = ShowApps
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Server details failed for {Server}", server);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        #region Disk / Apps / Deployments

        private class DiskInfo
        {
            public string Name { get; set; } = string.Empty;
            public string VolumeName { get; set; } = string.Empty;
            public long Size { get; set; }
            public long Free { get; set; }
        }

        private List<DiskInfo> GetRemoteLogicalDisks(string server)
        {
            var results = new List<DiskInfo>();
            try
            {
                var scopeString = $@"\\{server}\root\cimv2";
                var options = new ConnectionOptions
                {
                    Impersonation = ImpersonationLevel.Impersonate,
                    Timeout = TimeSpan.FromSeconds(5)
                };
                var scope = new ManagementScope(scopeString, options);
                scope.Connect();

                var query = new ObjectQuery("SELECT DeviceID, Size, FreeSpace, VolumeName FROM Win32_LogicalDisk WHERE DriveType = 3");
                using var searcher = new ManagementObjectSearcher(scope, query);
                foreach (ManagementObject mo in searcher.Get())
                {
                    try
                    {
                        results.Add(new DiskInfo
                        {
                            Name = mo["DeviceID"]?.ToString() ?? "",
                            VolumeName = mo["VolumeName"]?.ToString() ?? "",
                            Size = mo["Size"] != null ? Convert.ToInt64(mo["Size"]) : 0,
                            Free = mo["FreeSpace"] != null ? Convert.ToInt64(mo["FreeSpace"]) : 0
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disk enumeration failed for {Server}", server);
            }
            return results;
        }

        private class InstalledApp
        {
            public string DisplayName { get; set; } = string.Empty;
            public string? DisplayVersion { get; set; }
            public string? Publisher { get; set; }
        }

        private class AppInfo
        {
            public string DisplayName { get; set; } = string.Empty;
            public string? DisplayVersion { get; set; }
            public string? Publisher { get; set; }
        }

        private List<InstalledApp> GetRemoteInstalledApps(string server)
        {
            var list = new List<InstalledApp>();
            try
            {
                var hives = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (var path in hives)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, server);
                        using var key = baseKey.OpenSubKey(path);
                        if (key == null) continue;

                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using var appKey = key.OpenSubKey(sub);
                                var dn = appKey?.GetValue("DisplayName")?.ToString();
                                if (string.IsNullOrWhiteSpace(dn)) continue;
                                list.Add(new InstalledApp
                                {
                                    DisplayName = dn,
                                    DisplayVersion = appKey?.GetValue("DisplayVersion")?.ToString(),
                                    Publisher = appKey?.GetValue("Publisher")?.ToString()
                                });
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "App enumeration failed for {Server}", server);
            }

            return list
                .GroupBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private record BuildItem(string Name, double SizeMB);
        private record AppDeployment(string App, List<BuildItem> Builds)
        {
            public int BuildCount => Builds.Count;
            public double TotalSizeMB => Builds.Sum(b => b.SizeMB);
        }

        private List<AppDeployment> GetDeployments(string server, string rootFolder)
        {
            var deployments = new List<AppDeployment>();
            if (string.IsNullOrWhiteSpace(rootFolder)) return deployments;

            var baseUnc = $@"\\{server}\C$\{rootFolder}";
            if (!Directory.Exists(baseUnc)) return deployments;

            try
            {
                foreach (var appDir in Directory.GetDirectories(baseUnc))
                {
                    var appName = Path.GetFileName(appDir);
                    var buildItems = new List<BuildItem>();

                    foreach (var buildDir in Directory.GetDirectories(appDir))
                    {
                        var buildName = Path.GetFileName(buildDir);
                        double sizeMB = 0;
                        try
                        {
                            sizeMB = Math.Round(GetDirectorySize(buildDir) / (1024.0 * 1024.0), 2);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Size failed for {BuildDir}", buildDir);
                        }
                        buildItems.Add(new BuildItem(buildName, sizeMB));
                    }

                    buildItems = buildItems
                        .OrderByDescending(b => b.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    deployments.Add(new AppDeployment(appName, buildItems));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Deployment enumeration failed for {Server}", server);
            }

            return deployments
                .OrderBy(a => a.App, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        #endregion
    }
}