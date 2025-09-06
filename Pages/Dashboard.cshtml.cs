using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace ApplicationDeployment.Pages
{
    public class DashboardModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<DashboardModel> _logger;

        public DashboardModel(IConfiguration config, IWebHostEnvironment env, ILogger<DashboardModel> logger)
        {
            _config = config;
            _env = env;
            _logger = logger;
        }

        public void OnGet() { /* Initial page shell only; data via AJAX */ }

        // GET: /Dashboard?handler=Servers
        public IActionResult OnGetServers()
        {
            try
            {
                var csvPath = Path.Combine(_env.WebRootPath, _config["CsvFilePath"] ?? throw new InvalidOperationException("CsvFilePath not configured"));
                if (!System.IO.File.Exists(csvPath)) return new JsonResult(Array.Empty<string>());
                var servers = System.IO.File.ReadAllLines(csvPath)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Split(',', StringSplitOptions.TrimEntries)[0])
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new JsonResult(servers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list servers");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: /Dashboard?handler=ServerDetails&server=HOST
        public IActionResult OnGetServerDetails(string server)
        {
            if (string.IsNullOrWhiteSpace(server))
                return BadRequest("server required");

            try
            {
                var disks = GetRemoteLogicalDisks(server);
                var apps = GetRemoteInstalledApps(server);
                var deployments = GetDeployments(server, _config["CSTApps"] ?? string.Empty);

                var totalBuildSizeMB = deployments.Sum(d => d.TotalSizeMB);
                var totalBuildCount = deployments.Sum(d => d.BuildCount);

                return new JsonResult(new
                {
                    server,
                    disks = disks.Select(d => new { name = d.Name, volumeName = d.VolumeName, size = d.Size, free = d.Free }),
                    apps = apps.Select(a => new { displayName = a.DisplayName, displayVersion = a.DisplayVersion, publisher = a.Publisher }),
                    deployments = deployments.Select(d => new
                    {
                        app = d.App,
                        buildCount = d.BuildCount,
                        totalSizeMB = d.TotalSizeMB,
                        builds = d.Builds.Select(b => new { name = b.Name, sizeMB = b.SizeMB })
                    }),
                    totalBuildSizeMB,
                    totalBuildCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Server details failed for {Server}", server);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        #region Disk / Apps

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
                    catch { /* skip malformed */ }
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

        #endregion

        #region Deployments

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

                    try
                    {
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
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed enumerating builds for {App} on {Server}", appName, server);
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