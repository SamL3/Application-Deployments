using ApplicationDeployment.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ApplicationDeployment.Pages
{
    public class ConfigModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<ConfigModel> _logger;

        public ConfigModel(IConfiguration config, IWebHostEnvironment env, ILogger<ConfigModel> logger)
        {
            _config = config;
            _env = env;
            _logger = logger;
        }

        public string StagingPath { get; set; } = string.Empty;
        public List<ServerInfo> ServerList { get; set; } = new();
        public List<AppExeConfig> AppExecutables { get; set; } = new();
        public bool ShowAppsOnDashboard { get; set; }
        public List<ApiTestConfig> ApiTests { get; set; } = new();
        public string EnvironmentsCsv { get; set; } = string.Empty;
        public string CSTAppsRootPath { get; set; } = string.Empty;

        private string ApiTestsFile => Path.Combine(_env.WebRootPath, "apiTests.json");
        private string AppExesFile => Path.Combine(_env.WebRootPath, "appExes.json");

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
            public List<ServerData> Servers { get; set; } = new();
        }
        public class ServerData
        {
            public string Hostname { get; set; } = string.Empty;
            public string Userid { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
        }
        #endregion

        public void OnGet()
        {
            LoadCurrentSettings();
            ApiTests = LoadApiTests();
        }

        #region Handlers
        public IActionResult OnGetTestServer(string hostname)
        {
            if (string.IsNullOrWhiteSpace(hostname))
                return new JsonResult(new { success = false, message = "Hostname required" });
            try
            {
                var accessible = Directory.Exists($@"\\{hostname}\C$");
                return new JsonResult(new
                {
                    success = accessible,
                    message = accessible ? "Server accessible" : "Server not accessible or no permissions"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Test server failed: {Host}", hostname);
                return new JsonResult(new { success = false, message = $"Connection error: {ex.Message}" });
            }
        }

        public async Task<IActionResult> OnPostSaveStagingPathAsync([FromForm] string stagingPath)
        {
            if (string.IsNullOrWhiteSpace(stagingPath))
                return new JsonResult(new { success = false, message = "Staging path cannot be empty" });
            try
            {
                var root = await LoadAppSettingsNodeAsync();
                root["StagingPath"] = stagingPath;
                await PersistAppSettingsAsync(root);
                _logger.LogInformation("Updated StagingPath={Path}", stagingPath);
                return new JsonResult(new { success = true, message = "Staging path saved" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save staging path failed");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostSaveServersAsync([FromBody] ServerListRequest request)
        {
            if (request?.Servers == null)
                return new JsonResult(new { success = false, message = "Invalid server data" });
            try
            {
                var csvPath = Path.Combine(_env.WebRootPath, _config["CsvFilePath"] ?? "servers.csv");
                var lines = request.Servers
                    .Where(s => !string.IsNullOrWhiteSpace(s.Hostname))
                    .Select(s => $"{s.Hostname},{s.Userid},{s.Description}")
                    .ToList();
                await System.IO.File.WriteAllLinesAsync(csvPath, lines);
                _logger.LogInformation("Saved {Count} servers", lines.Count);
                return new JsonResult(new { success = true, message = $"Saved {lines.Count} servers" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save servers failed");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostSaveAppsAsync([FromBody] AppExesRequest request)
        {
            if (request?.Apps == null)
                return new JsonResult(new { success = false, message = "Invalid apps data" });
            try
            {
                // basic validation
                foreach (var a in request.Apps.ToList())
                {
                    if (string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.Exe))
                        request.Apps.Remove(a);
                }
                var json = JsonSerializer.Serialize(request.Apps, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(AppExesFile, json);
                _logger.LogInformation("Saved {Count} app exe mappings", request.Apps.Count);
                return new JsonResult(new { success = true, message = $"Saved {request.Apps.Count} apps" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save apps failed");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostSaveEnvironmentsAsync([FromForm] string environmentsCsv)
        {
            try
            {
                var root = await LoadAppSettingsNodeAsync();
                root["Environments"] = environmentsCsv ?? "";
                await PersistAppSettingsAsync(root);
                _logger.LogInformation("Updated Environments={Env}", environmentsCsv);
                return new JsonResult(new { success = true, message = "Environments saved" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save environments failed");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostSaveDashboardOptionsAsync([FromForm] bool showApps)
        {
            try
            {
                var root = await LoadAppSettingsNodeAsync();
                var dashboardNode = root["Dashboard"] as JsonObject ?? new JsonObject();
                dashboardNode["ShowApps"] = showApps;
                root["Dashboard"] = dashboardNode;
                await PersistAppSettingsAsync(root);
                _logger.LogInformation("Dashboard:ShowApps set to {Val}", showApps);
                return new JsonResult(new { success = true, message = "Dashboard options saved", value = showApps });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save dashboard options failed");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostSaveApiTestsAsync([FromBody] ApiTestsRequest request)
        {
            try
            {
                if (request?.Tests == null)
                    return new JsonResult(new { success = false, message = "Invalid test data" });
                foreach (var t in request.Tests)
                {
                    t.ScriptPath = t.ScriptPath?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(t.ScriptPath))
                        return new JsonResult(new { success = false, message = "Script path required for all tests" });
                    if (t.ScriptPath.IndexOfAny(new[] { '|', ';', '&' }) >= 0)
                        return new JsonResult(new { success = false, message = $"Illegal character in script path: {t.ScriptPath}" });
                    t.Description = t.Description?.Trim() ?? "";
                    t.Parameters = t.Parameters?
                        .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                        .Select(p => new ApiParam { Name = p.Name.Trim(), Value = p.Value?.Trim() ?? "" })
                        .ToList() ?? new();
                    if (string.IsNullOrWhiteSpace(t.Id))
                        t.Id = Guid.NewGuid().ToString("N");
                }
                var json = JsonSerializer.Serialize(request.Tests, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(ApiTestsFile, json);
                _logger.LogInformation("Saved {Count} API tests", request.Tests.Count);
                return new JsonResult(new { success = true, message = $"Saved {request.Tests.Count} tests" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save API tests failed");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public IActionResult OnGetApiTests()
        {
            try
            {
                var list = LoadApiTests();
                return new JsonResult(list);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load API tests");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostSaveCstAppsRootPathAsync([FromForm] string cstAppsRootPath)
        {
            if (string.IsNullOrWhiteSpace(cstAppsRootPath))
                return new JsonResult(new { success = false, message = "Path cannot be empty" });
            try
            {
                // Normalize (trim spaces)
                cstAppsRootPath = cstAppsRootPath.Trim();
                var root = await LoadAppSettingsNodeAsync();
                root["CSTAppsRootPath"] = cstAppsRootPath;
                await PersistAppSettingsAsync(root);
                _logger.LogInformation("Updated CSTAppsRootPath={Path}", cstAppsRootPath);
                return new JsonResult(new { success = true, message = "CSTApps root path saved" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save CSTApps root path failed");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }
        #endregion

        #region Helpers
        private List<ApiTestConfig> LoadApiTests()
        {
            try
            {
                if (!System.IO.File.Exists(ApiTestsFile))
                    return new List<ApiTestConfig>();
                var json = System.IO.File.ReadAllText(ApiTestsFile);
                return JsonSerializer.Deserialize<List<ApiTestConfig>>(json) ?? new List<ApiTestConfig>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load API tests failed");
                return new List<ApiTestConfig>();
            }
        }

        private async Task<JsonObject> LoadAppSettingsNodeAsync()
        {
            var appSettingsPath = Path.Combine(_env.ContentRootPath, "appsettings.json");
            if (!System.IO.File.Exists(appSettingsPath))
                return new JsonObject();
            using var fs = System.IO.File.OpenRead(appSettingsPath);
            var node = await JsonNode.ParseAsync(fs) as JsonObject;
            return node ?? new JsonObject();
        }

        private async Task PersistAppSettingsAsync(JsonObject root)
        {
            var appSettingsPath = Path.Combine(_env.ContentRootPath, "appsettings.json");
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(appSettingsPath, json);
        }

        private void LoadCurrentSettings()
        {
            StagingPath = _config["StagingPath"] ?? string.Empty;
            ShowAppsOnDashboard = _config.GetValue<bool>("Dashboard:ShowApps", false);
            EnvironmentsCsv = _config["Environments"] ?? "";
            CSTAppsRootPath = _config["CSTAppsRootPath"] ?? @"C:\CST Apps";

            try
            {
                var csvPath = Path.Combine(_env.WebRootPath, _config["CsvFilePath"] ?? "servers.csv");
                if (System.IO.File.Exists(csvPath))
                {
                    ServerList = System.IO.File.ReadAllLines(csvPath)
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
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load servers failed");
                ServerList = new();
            }

            try
            {
                if (System.IO.File.Exists(AppExesFile))
                {
                    var json = System.IO.File.ReadAllText(AppExesFile);
                    AppExecutables = JsonSerializer.Deserialize<List<AppExeConfig>>(json) ?? new();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load appExes failed");
                AppExecutables = new();
            }
        }
        #endregion
    }
}