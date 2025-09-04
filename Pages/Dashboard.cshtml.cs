using ApplicationDeployment.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// System.Management is required for remote WMI calls (disk info)
using System.Management;
using Microsoft.Win32;

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

        public List<SelectListItem> Servers { get; set; } = new();

        public void OnGet()
        {
            var csvPath = Path.Combine(_env.WebRootPath, _config["CsvFilePath"] ?? throw new InvalidOperationException("CsvFilePath not configured"));
            var serverLines = System.IO.File.ReadAllLines(csvPath);
            Servers = serverLines.Select(s => new SelectListItem { Value = s, Text = s }).ToList();
        }

        // Returns JSON with disk info and installed apps for a server.
        public IActionResult OnGetServerInfo(string server)
        {
            if (string.IsNullOrWhiteSpace(server))
                return BadRequest("server required");

            try
            {
                var disks = GetRemoteLogicalDisks(server);
                var apps = GetRemoteInstalledApps(server);

                return new JsonResult(new { disks, apps });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get server info for {Server}", server);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Concrete disk info type for JSON serialization and strong typing
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

            // Use WMI (System.Management)
            var scopeString = $@"\\{server}\root\cimv2";
            var options = new ConnectionOptions { Impersonation = ImpersonationLevel.Impersonate, Timeout = TimeSpan.FromSeconds(5) };
            var scope = new ManagementScope(scopeString, options);

            try
            {
                scope.Connect();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WMI connect failed for {Server}", server);
                return results;
            }

            var query = new ObjectQuery("SELECT DeviceID, Size, FreeSpace, VolumeName FROM Win32_LogicalDisk WHERE DriveType = 3");
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    string deviceId = mo["DeviceID"]?.ToString() ?? "";
                    long size = mo["Size"] != null ? Convert.ToInt64(mo["Size"]) : 0;
                    long free = mo["FreeSpace"] != null ? Convert.ToInt64(mo["FreeSpace"]) : 0;
                    string vol = mo["VolumeName"]?.ToString() ?? "";
                    results.Add(new DiskInfo { Name = deviceId, VolumeName = vol, Size = size, Free = free });
                }
                catch (Exception inner)
                {
                    _logger.LogDebug(inner, "Skipping malformed disk entry on {Server}", server);
                }
            }

            return results;
        }

        // Concrete installed app type for JSON serialization and grouping
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
                // Query both 64-bit and 32-bit uninstall keys on HKLM
                var hives = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (var sub in hives)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, server);
                        using var key = baseKey.OpenSubKey(sub);
                        if (key == null) continue;

                        foreach (var subName in key.GetSubKeyNames())
                        {
                            try
                            {
                                using var appKey = key.OpenSubKey(subName);
                                var displayName = appKey?.GetValue("DisplayName")?.ToString();
                                if (string.IsNullOrWhiteSpace(displayName)) continue;
                                var displayVersion = appKey?.GetValue("DisplayVersion")?.ToString();
                                var publisher = appKey?.GetValue("Publisher")?.ToString();

                                list.Add(new InstalledApp
                                {
                                    DisplayName = displayName!,
                                    DisplayVersion = displayVersion,
                                    Publisher = publisher
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Skipping registry entry {Sub} on {Server}", subName, server);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Registry read failed for hive {Sub} on {Server}", sub, server);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate installed apps on {Server}", server);
            }

            // Deduplicate by DisplayName (case-insensitive)
            var dedup = list
                .GroupBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            return dedup;
        }
    }
}