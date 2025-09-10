using ApplicationDeployment.Models;
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

        // Add this method to fix CS0103
        private List<ApiTestConfig> LoadApiTests()
        {
            try
            {
                if (System.IO.File.Exists(ApiTestsFile))
                {
                    var json = System.IO.File.ReadAllText(ApiTestsFile);
                    var tests = JsonSerializer.Deserialize<List<ApiTestConfig>>(json);
                    return tests ?? new List<ApiTestConfig>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LoadApiTests failed");
                Debug.WriteLine($"[Config] LoadApiTests failed: {ex.Message}");
            }
            return new List<ApiTestConfig>();
        }

        #region Helpers
        private void LoadCurrentSettings()
        {
            StagingPath = _config["StagingPath"] ?? string.Empty;
            ShowAppsOnDashboard = _config.GetValue<bool>("Dashboard:ShowApps", false);
            EnvironmentsCsv = _config["Environments"] ?? "";
            CSTAppsRootPath = _config["CSTAppsRootPath"] ?? @"C:\CST Apps";

            _logger.LogInformation("Config.LoadCurrentSettings: StagingPath={StagingPath}", StagingPath);
            Debug.WriteLine($"[Config] LoadCurrentSettings: StagingPath={StagingPath}");

            try
            {
                var csvPath = Path.Combine(_env.WebRootPath, _config["CsvFilePath"] ?? "servers.csv");
                if (System.IO.File.Exists(csvPath))
                {
                    var lines = System.IO.File.ReadAllLines(csvPath);
                    _logger.LogInformation("Config.LoadCurrentSettings: servers.csv found at {Path} with {Count} lines", csvPath, lines.Length);
                    Debug.WriteLine($"[Config] servers.csv: path={csvPath}, lines={lines.Length}");

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

                    _logger.LogInformation("Config.LoadCurrentSettings: Loaded {Count} servers", ServerList.Count);
                    Debug.WriteLine($"[Config] Loaded servers={ServerList.Count}");
                }
                else
                {
                    _logger.LogWarning("Config.LoadCurrentSettings: servers.csv not found at {Path}", csvPath);
                    Debug.WriteLine($"[Config] servers.csv not found: {csvPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load servers failed");
                Debug.WriteLine($"[Config] Load servers failed: {ex.Message}");
                ServerList = new();
            }

            try
            {
                if (System.IO.File.Exists(AppExesFile))
                {
                    var json = System.IO.File.ReadAllText(AppExesFile);
                    _logger.LogInformation("Config.LoadCurrentSettings: appExes.json found at {Path}, size={Size} bytes", AppExesFile, json.Length);
                    Debug.WriteLine($"[Config] appExes.json: path={AppExesFile}, size={json.Length} bytes");

                    AppExecutables = JsonSerializer.Deserialize<List<AppExeConfig>>(json) ?? new();

                    _logger.LogInformation("Config.LoadCurrentSettings: AppExecutables loaded: {Count}", AppExecutables.Count);
                    Debug.WriteLine($"[Config] AppExecutables count={AppExecutables.Count} Names=[{string.Join(", ", AppExecutables.Select(a=>a.Name).Take(10))}{(AppExecutables.Count>10?", ...":"")}]");
                }
                else
                {
                    _logger.LogWarning("Config.LoadCurrentSettings: appExes.json not found at {Path}", AppExesFile);
                    Debug.WriteLine($"[Config] appExes.json not found: {AppExesFile}");
                    AppExecutables = new();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load appExes failed");
                Debug.WriteLine($"[Config] Load appExes failed: {ex.Message}");
                AppExecutables = new();
            }
        }
        #endregion
    }
}