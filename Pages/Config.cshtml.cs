using DevApp.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace DevApp.Pages
{
    public class ConfigModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<ConfigModel> _logger;

        //    public ConfigModel(IConfiguration config, IWebHostEnvironment env, ILogger<ConfigModel> logger)
        //    {
        //        _config = config;
        //        _env = env;
        //        _logger = logger;
        //    }

        //    public string ConfirmationMessage { get; set; } = string.Empty;

        //    public string StagingPath { get; set; } = string.Empty;
        //    public List<ServerInfo> ServerList { get; set; } = new();
        //    public List<AppExeConfig> AppExecutables { get; set; } = new();
        //    public bool ShowAppsOnDashboard { get; set; }
        //    public List<ApiTestConfig> ApiTests { get; set; } = new();
        //    public string EnvironmentsCsv { get; set; } = string.Empty;
        //    public string CSTAppsRootPath { get; set; } = string.Empty;

        //    private string AppConfigPath => Path.Combine(_env.ContentRootPath, "appconfig.json");

        //    #region Models
        //    public class AppExeConfig
        //    {
        //        public string Name { get; set; } = string.Empty;
        //        public string Exe { get; set; } = string.Empty;
        //        public bool EnvInShortcut { get; set; }
        //    }

        //    public class AppExesRequest
        //    {
        //        public List<AppExeConfig> Apps { get; set; } = new();
        //    }

        //    public class ApiTestConfig
        //    {
        //        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        //        public string Description { get; set; } = string.Empty;
        //        public string ScriptPath { get; set; } = string.Empty;
        //        public List<ApiParam> Parameters { get; set; } = new();
        //    }
        //    public class ApiParam
        //    {
        //        public string Name { get; set; } = string.Empty;
        //        public string Value { get; set; } = string.Empty;
        //    }
        //    public class ApiTestsRequest
        //    {
        //        public List<ApiTestConfig> Tests { get; set; } = new();
        //    }
        //    public class ServerListRequest
        //    {
        //        public List<ServerInfo> Servers { get; set; } = new();
        //    }
        //    #endregion

        //    public void OnGet()
        //    {
        //        LoadCurrentSettings();
        //        ApiTests = LoadApiTestsFromAppConfig();
        //    }

        //    #region Helpers
        //    private void LoadCurrentSettings()
        //    {
        //        // Read current values (do not depend on IConfiguration reload)
        //        var doc = LoadAppConfig();
        //        StagingPath = doc?["StagingPath"]?.GetValue<string>() ?? string.Empty;
        //        ShowAppsOnDashboard = doc?["Dashboard"]?["ShowApps"]?.GetValue<bool>() ?? false;
        //        CSTAppsRootPath = doc?["CSTAppsRootPath"]?.GetValue<string>() ?? @"C:\CSTApps";
        //        EnvironmentsCsv = doc?["Environments"]?.GetValue<string>() ?? string.Empty;

        //        // Servers
        //        try
        //        {
        //            var serversNode = doc?["Servers"] as JsonArray;
        //            if (serversNode != null)
        //            {
        //                ServerList = serversNode
        //                    .OfType<JsonObject>()
        //                    .Select(o => new ServerInfo
        //                    {
        //                        HostName = o["HostName"]?.GetValue<string>() ?? "",
        //                        UserID = o["UserID"]?.GetValue<string>() ?? "",
        //                        Description = o["Description"]?.GetValue<string>() ?? ""
        //                    })
        //                    .Where(s => !string.IsNullOrWhiteSpace(s.HostName))
        //                    .ToList();
        //            }
        //            else
        //            {
        //                ServerList = new();
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "Load servers failed");
        //            ServerList = new();
        //        }

        //        // App executables
        //        try
        //        {
        //            var appsNode = doc?["AppExes"] as JsonArray;
        //            if (appsNode != null)
        //            {
        //                AppExecutables = appsNode
        //                    .OfType<JsonObject>()
        //                    .Select(o => new AppExeConfig
        //                    {
        //                        Name = o["Name"]?.GetValue<string>() ?? "",
        //                        Exe = o["Exe"]?.GetValue<string>() ?? "",
        //                        EnvInShortcut = o["EnvInShortcut"]?.GetValue<bool>() ?? false
        //                    })
        //                    .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Exe))
        //                    .ToList();
        //            }
        //            else
        //            {
        //                AppExecutables = new();
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "Load app executables failed");
        //            AppExecutables = new();
        //        }
        //    }

        //    private List<ApiTestConfig> LoadApiTestsFromAppConfig()
        //    {
        //        try
        //        {
        //            var doc = LoadAppConfig();
        //            var testsNode = doc?["ApiTests"] as JsonArray;
        //            if (testsNode == null) return new List<ApiTestConfig>();
        //            return testsNode
        //                .OfType<JsonObject>()
        //                .Select(o => new ApiTestConfig
        //                {
        //                    Id = o["Id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
        //                    Description = o["Description"]?.GetValue<string>() ?? "",
        //                    ScriptPath = o["ScriptPath"]?.GetValue<string>() ?? "",
        //                    Parameters = (o["Parameters"] as JsonArray)?
        //                        .OfType<JsonObject>()
        //                        .Select(p => new ApiParam
        //                        {
        //                            Name = p["Name"]?.GetValue<string>() ?? "",
        //                            Value = p["Value"]?.GetValue<string>() ?? ""
        //                        }).ToList() ?? new List<ApiParam>()
        //                })
        //                .ToList();
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "Load API tests failed");
        //            return new List<ApiTestConfig>();
        //        }
        //    }

        //    private JsonObject LoadAppConfig()
        //    {
        //        try
        //        {
        //            var json = System.IO.File.ReadAllText(AppConfigPath);
        //            var node = JsonNode.Parse(json) as JsonObject;
        //            return node ?? new JsonObject();
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "Failed reading appconfig.json");
        //            return new JsonObject();
        //        }
        //    }

        //    private (bool ok, string message) SaveAppConfig(JsonObject doc)
        //    {
        //        try
        //        {
        //            var options = new JsonSerializerOptions { WriteIndented = true };
        //            System.IO.File.WriteAllText(AppConfigPath, doc.ToJsonString(options));
        //            return (true, "Saved.");
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "Failed writing appconfig.json");
        //            return (false, $"Save failed: {ex.Message}");
        //        }
        //    }

        //    private JsonObject EnsureObj(JsonObject parent, string name)
        //    {
        //        if (parent[name] is JsonObject o) return o;
        //        var n = new JsonObject();
        //        parent[name] = n;
        //        return n;
        //    }
        //    #endregion

        //    #region Save Handlers (JSON responses with success/message)
        //    [ValidateAntiForgeryToken]
        //    public IActionResult OnPostSaveStagingPath([FromForm] string stagingPath)
        //    {
        //        if (string.IsNullOrWhiteSpace(stagingPath))
        //            return new JsonResult(new { success = false, message = "StagingPath is required." });

        //        var doc = LoadAppConfig();
        //        doc["StagingPath"] = stagingPath.Trim();
        //        var (ok, msg) = SaveAppConfig(doc);
        //        ConfirmationMessage = ok ? "StagingPath saved successfully." : "";
        //        return new JsonResult(new { success = ok, message = ok ? "StagingPath saved." : msg });
        //    }

        //    [ValidateAntiForgeryToken]
        //    public IActionResult OnPostSaveCstAppsRootPath([FromForm] string cstAppsRootPath)
        //    {
        //        if (string.IsNullOrWhiteSpace(cstAppsRootPath))
        //            return new JsonResult(new { success = false, message = "CSTAppsRootPath is required." });

        //        var doc = LoadAppConfig();
        //        doc["CSTAppsRootPath"] = cstAppsRootPath.Trim();
        //        var (ok, msg) = SaveAppConfig(doc);
        //        ConfirmationMessage = ok ? "CSTAppsRootPath saved successfully." : "";
        //        return new JsonResult(new { success = ok, message = ok ? "CSTAppsRootPath saved." : msg });
        //    }

        //    [ValidateAntiForgeryToken]
        //    public IActionResult OnPostSaveDashboardOptions([FromForm] bool showApps)
        //    {
        //        var doc = LoadAppConfig();
        //        var dashboard = EnsureObj(doc, "Dashboard");
        //        dashboard["ShowApps"] = showApps;
        //        var (ok, msg) = SaveAppConfig(doc);
        //        ConfirmationMessage = ok ? "Dashboard options saved successfully." : "";
        //        return new JsonResult(new { success = ok, message = ok ? "Dashboard options saved." : msg });
        //    }

        //    [ValidateAntiForgeryToken]
        //    public IActionResult OnPostSaveEnvironments([FromForm] string environmentsCsv)
        //    {
        //        var csv = (environmentsCsv ?? "").Trim();
        //        if (string.IsNullOrWhiteSpace(csv))
        //            return new JsonResult(new { success = false, message = "At least one environment is required." });

        //        // Normalize: split/trim/join to keep as CSV in appconfig.json
        //        var list = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        //        if (list.Count == 0)
        //            return new JsonResult(new { success = false, message = "No valid environment names provided." });

        //        var normalizedCsv = string.Join(",", list);

        //        var doc = LoadAppConfig();
        //        doc["Environments"] = normalizedCsv;
        //        var (ok, msg) = SaveAppConfig(doc);
        //        ConfirmationMessage = ok ? "Environments saved successfully." : "";
        //        return new JsonResult(new { success = ok, message = ok ? "Environments saved." : msg });
        //    }

        //    [ValidateAntiForgeryToken]
        //    public IActionResult OnPostSaveApps([FromBody] AppExesRequest request)
        //    {
        //        if (request?.Apps == null)
        //            return new JsonResult(new { success = false, message = "Invalid payload." });

        //        var invalid = request.Apps.Where(a => string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.Exe)).ToList();
        //        if (invalid.Any())
        //            return new JsonResult(new { success = false, message = "All apps must have Name and Exe." });

        //        var doc = LoadAppConfig();
        //        var arr = new JsonArray();
        //        foreach (var a in request.Apps)
        //        {
        //            arr.Add(new JsonObject
        //            {
        //                ["Name"] = a.Name.Trim(),
        //                ["Exe"] = a.Exe.Trim(),
        //                ["EnvInShortcut"] = a.EnvInShortcut
        //            });
        //        }
        //        doc["AppExes"] = arr;

        //        var (ok, msg) = SaveAppConfig(doc);
        //        ConfirmationMessage = ok ? "Application executables saved successfully." : "";
        //        return new JsonResult(new { success = ok, message = ok ? "Applications saved." : msg });
        //    }

        //    [ValidateAntiForgeryToken]
        //    public IActionResult OnPostSaveServers([FromBody] ServerListRequest request)
        //    {
        //        if (request?.Servers == null)
        //            return new JsonResult(new { success = false, message = "Invalid payload." });

        //        var servers = request.Servers
        //            .Where(s => !string.IsNullOrWhiteSpace(s.HostName))
        //            .Select(s => new ServerInfo
        //            {
        //                HostName = s.HostName.Trim(),
        //                UserID = s.UserID?.Trim() ?? "",
        //                Description = s.Description?.Trim() ?? ""
        //            })
        //            .ToList();

        //        if (servers.Count == 0)
        //            return new JsonResult(new { success = false, message = "At least one server with HostName is required." });

        //        var doc = LoadAppConfig();
        //        var arr = new JsonArray();
        //        foreach (var s in servers)
        //        {
        //            arr.Add(new JsonObject
        //            {
        //                ["HostName"] = s.HostName,
        //                ["UserID"] = s.UserID,
        //                ["Description"] = s.Description
        //            });
        //        }
        //        doc["Servers"] = arr;

        //        var (ok, msg) = SaveAppConfig(doc);
        //        ConfirmationMessage = ok ? "Servers saved successfully." : "";
        //        return new JsonResult(new { success = ok, message = ok ? "Servers saved." : msg });
        //    }

        //    [ValidateAntiForgeryToken]
        //    public IActionResult OnPostSaveApiTests([FromBody] ApiTestsRequest request)
        //    {
        //        if (request?.Tests == null)
        //            return new JsonResult(new { success = false, message = "Invalid payload." });

        //        // Basic validation
        //        foreach (var t in request.Tests)
        //        {
        //            if (string.IsNullOrWhiteSpace(t.Description))
        //                return new JsonResult(new { success = false, message = "Each test must have a Description." });
        //            if (string.IsNullOrWhiteSpace(t.ScriptPath))
        //                return new JsonResult(new { success = false, message = "Each test must have a ScriptPath." });
        //            t.Id ??= Guid.NewGuid().ToString("N");
        //            t.Parameters ??= new List<ApiParam>();
        //        }

        //        var doc = LoadAppConfig();
        //        var arr = new JsonArray();
        //        foreach (var t in request.Tests)
        //        {
        //            var pArr = new JsonArray();
        //            foreach (var p in t.Parameters)
        //            {
        //                if (string.IsNullOrWhiteSpace(p.Name) && string.IsNullOrWhiteSpace(p.Value)) continue;
        //                pArr.Add(new JsonObject
        //                {
        //                    ["Name"] = p.Name ?? "",
        //                    ["Value"] = p.Value ?? ""
        //                });
        //            }

        //            arr.Add(new JsonObject
        //            {
        //                ["Id"] = t.Id,
        //                ["Description"] = t.Description,
        //                ["ScriptPath"] = t.ScriptPath,
        //                ["Parameters"] = pArr
        //            });
        //        }
        //        doc["ApiTests"] = arr;

        //        var (ok, msg) = SaveAppConfig(doc);
        //        ConfirmationMessage = ok ? "API tests saved successfully." : "";
        //        return new JsonResult(new { success = ok, message = ok ? "API tests saved." : msg });
        //    }
        //    #endregion
        //}
        public ConfigModel(IConfiguration config, IWebHostEnvironment env, ILogger<ConfigModel> logger)
        {
            _config = config;
            _env = env;
            _logger = logger;
        }

        public string ConfirmationMessage { get; set; } = string.Empty;

        public string StagingPath { get; set; } = string.Empty;
        public List<ServerInfo> ServerList { get; set; } = new();
        public List<AppExeConfig> AppExecutables { get; set; } = new();
        public bool ShowAppsOnDashboard { get; set; }
        public List<ApiTestConfig> ApiTests { get; set; } = new();

        // New: strongly-typed environments list (always JSON array in storage)
        public List<string> Environments { get; set; } = new();

        // Optional: keep CSV for UI compatibility (computed from Environments)
        public string EnvironmentsCsv { get; set; } = string.Empty;

        public string CSTAppsRootPath { get; set; } = string.Empty;

        private string AppConfigPath => Path.Combine(_env.ContentRootPath, "appconfig.json");

        #region Models
        public class AppExeConfig
        {
            public string Name { get; set; } = string.Empty;
            public string Exe { get; set; } = string.Empty;
            public bool EnvInShortcut { get; set; }
        }

        public class AppExesRequest
        {
            public List<AppExeConfig> Apps { get; set; } = new();
        }

        public class ApiTestConfig
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Description { get; set; } = string.Empty;
            public string ScriptPath { get; set; } = string.Empty;
            public List<ApiParam> Parameters { get; set; } = new();
        }
        public class ApiParam
        {
            public string Name { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }
        public class ApiTestsRequest
        {
            public List<ApiTestConfig> Tests { get; set; } = new();
        }
        public class ServerListRequest
        {
            public List<ServerInfo> Servers { get; set; } = new();
        }

        public class EnvironmentsRequest
        {
            public List<string> Environments { get; set; } = new();
        }
        #endregion

        public void OnGet()
        {
            LoadCurrentSettings();
            ApiTests = LoadApiTestsFromAppConfig();
        }

        #region Helpers
        private void LoadCurrentSettings()
        {
            // Read current values (do not depend on IConfiguration reload)
            var doc = LoadAppConfig();
            StagingPath = doc?["StagingPath"]?.GetValue<string>() ?? string.Empty;
            ShowAppsOnDashboard = doc?["Dashboard"]?["ShowApps"]?.GetValue<bool>() ?? false;
            CSTAppsRootPath = doc?["CSTAppsRootPath"]?.GetValue<string>() ?? @"C:\CSTApps";

            // Environments: always expose as list; read array/object/string (back-compat)
            Environments = ReadEnvironments(doc?["Environments"]);
            EnvironmentsCsv = string.Join(",", Environments);

            // Servers
            try
            {
                var serversNode = doc?["Servers"] as JsonArray;
                if (serversNode != null)
                {
                    ServerList = serversNode
                        .OfType<JsonObject>()
                        .Select(o => new ServerInfo
                        {
                            HostName = o["HostName"]?.GetValue<string>() ?? "",
                            UserID = o["UserID"]?.GetValue<string>() ?? "",
                            Description = o["Description"]?.GetValue<string>() ?? ""
                        })
                        .Where(s => !string.IsNullOrWhiteSpace(s.HostName))
                        .ToList();
                }
                else
                {
                    ServerList = new();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load servers failed");
                ServerList = new();
            }

            // App executables
            try
            {
                var appsNode = doc?["AppExes"] as JsonArray;
                if (appsNode != null)
                {
                    AppExecutables = appsNode
                        .OfType<JsonObject>()
                        .Select(o => new AppExeConfig
                        {
                            Name = o["Name"]?.GetValue<string>() ?? "",
                            Exe = o["Exe"]?.GetValue<string>() ?? "",
                            EnvInShortcut = o["EnvInShortcut"]?.GetValue<bool>() ?? false
                        })
                        .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Exe))
                        .ToList();
                }
                else
                {
                    AppExecutables = new();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load app executables failed");
                AppExecutables = new();
            }
        }

        private static List<string> ReadEnvironments(JsonNode? node)
        {
            var result = new List<string>();

            if (node is JsonArray arr)
            {
                foreach (var e in arr)
                {
                    if (e is JsonValue sv && sv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                        result.Add(s.Trim());
                    else if (e is JsonObject o && o["Name"] is JsonValue nv && nv.TryGetValue<string>(out var n) && !string.IsNullOrWhiteSpace(n))
                        result.Add(n.Trim());
                }
                return result;
            }

            if (node is JsonValue v && v.TryGetValue<string>(out var csv) && !string.IsNullOrWhiteSpace(csv))
            {
                result.AddRange(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                return result;
            }

            if (node is JsonObject obj)
            {
                // Fallback: property names as env names
                result.AddRange(obj.Select(kvp => kvp.Key)
                                   .Where(k => !string.IsNullOrWhiteSpace(k))
                                   .Select(k => k.Trim()));
            }

            return result;
        }

        private static List<string> ParseCsvToList(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new List<string>();
            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Where(s => !string.IsNullOrWhiteSpace(s))
                      .Select(s => s.Trim())
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToList();
        }

        private List<ApiTestConfig> LoadApiTestsFromAppConfig()
        {
            try
            {
                var doc = LoadAppConfig();
                var testsNode = doc?["ApiTests"] as JsonArray;
                if (testsNode == null) return new List<ApiTestConfig>();
                return testsNode
                    .OfType<JsonObject>()
                    .Select(o => new ApiTestConfig
                    {
                        Id = o["Id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                        Description = o["Description"]?.GetValue<string>() ?? "",
                        ScriptPath = o["ScriptPath"]?.GetValue<string>() ?? "",
                        Parameters = (o["Parameters"] as JsonArray)?
                            .OfType<JsonObject>()
                            .Select(p => new ApiParam
                            {
                                Name = p["Name"]?.GetValue<string>() ?? "",
                                Value = p["Value"]?.GetValue<string>() ?? ""
                            }).ToList() ?? new List<ApiParam>()
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load API tests failed");
                return new List<ApiTestConfig>();
            }
        }

        private JsonObject LoadAppConfig()
        {
            try
            {
                var json = System.IO.File.ReadAllText(AppConfigPath);
                var node = JsonNode.Parse(json) as JsonObject;
                return node ?? new JsonObject();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed reading appconfig.json");
                return new JsonObject();
            }
        }

        private (bool ok, string message) SaveAppConfig(JsonObject doc)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                System.IO.File.WriteAllText(AppConfigPath, doc.ToJsonString(options));
                return (true, "Saved.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed writing appconfig.json");
                return (false, $"Save failed: {ex.Message}");
            }
        }

        private JsonObject EnsureObj(JsonObject parent, string name)
        {
            if (parent[name] is JsonObject o) return o;
            var n = new JsonObject();
            parent[name] = n;
            return n;
        }
        #endregion

        #region Save Handlers (JSON responses with success/message)
        [ValidateAntiForgeryToken]
        public IActionResult OnPostSaveStagingPath([FromForm] string stagingPath)
        {
            if (string.IsNullOrWhiteSpace(stagingPath))
                return new JsonResult(new { success = false, message = "StagingPath is required." });

            var doc = LoadAppConfig();
            doc["StagingPath"] = stagingPath.Trim();
            var (ok, msg) = SaveAppConfig(doc);
            ConfirmationMessage = ok ? "StagingPath saved successfully." : "";
            return new JsonResult(new { success = ok, message = ok ? "StagingPath saved." : msg });
        }

        [ValidateAntiForgeryToken]
        public IActionResult OnPostSaveCstAppsRootPath([FromForm] string cstAppsRootPath)
        {
            if (string.IsNullOrWhiteSpace(cstAppsRootPath))
                return new JsonResult(new { success = false, message = "CSTAppsRootPath is required." });

            var doc = LoadAppConfig();
            doc["CSTAppsRootPath"] = cstAppsRootPath.Trim();
            var (ok, msg) = SaveAppConfig(doc);
            ConfirmationMessage = ok ? "CSTAppsRootPath saved successfully." : "";
            return new JsonResult(new { success = ok, message = ok ? "CSTAppsRootPath saved." : msg });
        }

        [ValidateAntiForgeryToken]
        public IActionResult OnPostSaveDashboardOptions([FromForm] bool showApps)
        {
            var doc = LoadAppConfig();
            var dashboard = EnsureObj(doc, "Dashboard");
            dashboard["ShowApps"] = showApps;
            var (ok, msg) = SaveAppConfig(doc);
            ConfirmationMessage = ok ? "Dashboard options saved successfully." : "";
            return new JsonResult(new { success = ok, message = ok ? "Dashboard options saved." : msg });
        }

        // Accept JSON array body (preferred) or CSV form for backward compatibility
        [ValidateAntiForgeryToken]
        public IActionResult OnPostSaveEnvironments([FromBody] EnvironmentsRequest? request, [FromForm] string? environmentsCsv)
        {
            var list = (request?.Environments ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

            if (list.Count == 0 && !string.IsNullOrWhiteSpace(environmentsCsv))
            {
                list = ParseCsvToList(environmentsCsv);
            }

            if (list.Count == 0)
                return new JsonResult(new { success = false, message = "At least one environment is required." });

            var doc = LoadAppConfig();
            var arr = new JsonArray();
            foreach (var e in list)
                arr.Add(e);

            doc["Environments"] = arr;

            var (ok, msg) = SaveAppConfig(doc);
            ConfirmationMessage = ok ? "Environments saved successfully." : "";
            return new JsonResult(new { success = ok, message = ok ? "Environments saved." : msg });
        }

        [ValidateAntiForgeryToken]
        public IActionResult OnPostSaveApps([FromBody] AppExesRequest request)
        {
            if (request?.Apps == null)
                return new JsonResult(new { success = false, message = "Invalid payload." });

            var invalid = request.Apps.Where(a => string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.Exe)).ToList();
            if (invalid.Any())
                return new JsonResult(new { success = false, message = "All apps must have Name and Exe." });

            var doc = LoadAppConfig();
            var arr = new JsonArray();
            foreach (var a in request.Apps)
            {
                arr.Add(new JsonObject
                {
                    ["Name"] = a.Name.Trim(),
                    ["Exe"] = a.Exe.Trim(),
                    ["EnvInShortcut"] = a.EnvInShortcut
                });
            }
            doc["AppExes"] = arr;

            var (ok, msg) = SaveAppConfig(doc);
            ConfirmationMessage = ok ? "Application executables saved successfully." : "";
            return new JsonResult(new { success = ok, message = ok ? "Applications saved." : msg });
        }

        [ValidateAntiForgeryToken]
        public IActionResult OnPostSaveServers([FromBody] ServerListRequest request)
        {
            if (request?.Servers == null)
                return new JsonResult(new { success = false, message = "Invalid payload." });

            var servers = request.Servers
                .Where(s => !string.IsNullOrWhiteSpace(s.HostName))
                .Select(s => new ServerInfo
                {
                    HostName = s.HostName.Trim(),
                    UserID = s.UserID?.Trim() ?? "",
                    Description = s.Description?.Trim() ?? ""
                })
                .ToList();

            if (servers.Count == 0)
                return new JsonResult(new { success = false, message = "At least one server with HostName is required." });

            var doc = LoadAppConfig();
            var arr = new JsonArray();
            foreach (var s in servers)
            {
                arr.Add(new JsonObject
                {
                    ["HostName"] = s.HostName,
                    ["UserID"] = s.UserID,
                    ["Description"] = s.Description
                });
            }
            doc["Servers"] = arr;

            var (ok, msg) = SaveAppConfig(doc);
            ConfirmationMessage = ok ? "Servers saved successfully." : "";
            return new JsonResult(new { success = ok, message = ok ? "Servers saved." : msg });
        }

        [ValidateAntiForgeryToken]
        public IActionResult OnPostSaveApiTests([FromBody] ApiTestsRequest request)
        {
            if (request?.Tests == null)
                return new JsonResult(new { success = false, message = "Invalid payload." });

            // Basic validation
            foreach (var t in request.Tests)
            {
                if (string.IsNullOrWhiteSpace(t.Description))
                    return new JsonResult(new { success = false, message = "Each test must have a Description." });
                if (string.IsNullOrWhiteSpace(t.ScriptPath))
                    return new JsonResult(new { success = false, message = "Each test must have a ScriptPath." });
                t.Id ??= Guid.NewGuid().ToString("N");
                t.Parameters ??= new List<ApiParam>();
            }

            var doc = LoadAppConfig();
            var arr = new JsonArray();
            foreach (var t in request.Tests)
            {
                var pArr = new JsonArray();
                foreach (var p in t.Parameters)
                {
                    if (string.IsNullOrWhiteSpace(p.Name) && string.IsNullOrWhiteSpace(p.Value)) continue;
                    pArr.Add(new JsonObject
                    {
                        ["Name"] = p.Name ?? "",
                        ["Value"] = p.Value ?? ""
                    });
                }

                arr.Add(new JsonObject
                {
                    ["Id"] = t.Id,
                    ["Description"] = t.Description,
                    ["ScriptPath"] = t.ScriptPath,
                    ["Parameters"] = pArr
                });
            }
            doc["ApiTests"] = arr;

            var (ok, msg) = SaveAppConfig(doc);
            ConfirmationMessage = ok ? "API tests saved successfully." : "";
            return new JsonResult(new { success = ok, message = ok ? "API tests saved." : msg });
        }
        #endregion
    }

}