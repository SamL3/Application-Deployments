using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ApplicationDeployment.Pages
{
    [ValidateAntiForgeryToken]
    public class TestModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<TestModel> _logger;

        public TestModel(IConfiguration config, IWebHostEnvironment env, ILogger<TestModel> logger)
        {
            _config = config;
            _env = env;
            _logger = logger;
        }

        public List<ApiTestConfig> ApiTests { get; set; } = new();

        private string AppConfigPath => Path.Combine(_env.ContentRootPath, "appconfig.json");
        
        private string UserOverrideFile
        {
            get
            {
                var user = User?.Identity?.Name ?? "anonymous";
                var safe = new string(user.Where(char.IsLetterOrDigit).ToArray());
                if (string.IsNullOrEmpty(safe)) safe = "anonymous";
                var dir = Path.Combine(Path.GetTempPath(), "AppDeploymentUserOverrides");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, safe + ".json");
            }
        }

        public void OnGet()
        {
            ApiTests = LoadApiTests();
            var overrides = LoadUserOverrides();
            foreach (var test in ApiTests)
            {
                if (overrides.TryGetValue(test.Id, out var paramOverrides))
                {
                    foreach (var ov in paramOverrides)
                    {
                        var target = test.Parameters.FirstOrDefault(p => p.Name.Equals(ov.Name, StringComparison.OrdinalIgnoreCase));
                        if (target != null)
                            target.Value = ov.Value;
                        else
                            test.Parameters.Add(new ApiParam { Name = ov.Name, Value = ov.Value });
                    }
                }
            }
        }

        public class ApiTestConfig
        {
            public string Id { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string ScriptPath { get; set; } = string.Empty;
            public List<ApiParam> Parameters { get; set; } = new();
        }

        public class ApiParam
        {
            public string Name { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }

        public class RunTestRequest
        {
            public string? Id { get; set; }
            public List<ApiParam>? Parameters { get; set; }
        }

        public class SaveUserConfigRequest
        {
            public string? Id { get; set; }
            public List<ApiParam>? Parameters { get; set; }
        }

        public class RunResult
        {
            public bool Success { get; set; }
            public int ExitCode { get; set; }
            public string Command { get; set; } = string.Empty;
            public string Output { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public double DurationMs { get; set; }
        }

        public async Task<IActionResult> OnPostRunAsync([FromBody] RunTestRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "Invalid request" });

            var tests = LoadApiTests();
            var overrides = LoadUserOverrides();
            ApiTestConfig? test = null;

            if (!string.IsNullOrWhiteSpace(request.Id))
                test = tests.FirstOrDefault(t => t.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));

            if (test == null)
                return BadRequest(new { success = false, message = "Test not found" });

            var parameters = test.Parameters.Select(p => new ApiParam { Name = p.Name, Value = p.Value }).ToList();

            if (overrides.TryGetValue(test.Id, out var userParamOverrides))
            {
                foreach (var ov in userParamOverrides)
                {
                    var existing = parameters.FirstOrDefault(x => x.Name.Equals(ov.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) existing.Value = ov.Value;
                }
            }

            if (request.Parameters != null)
            {
                foreach (var p in request.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                {
                    var existing = parameters.FirstOrDefault(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) existing.Value = p.Value ?? "";
                    else parameters.Add(new ApiParam { Name = p.Name, Value = p.Value ?? "" });
                }
            }

            string scriptPath = test.ScriptPath.Trim();
            _logger.LogInformation("Processing script: '{Script}' (Length: {Length})", scriptPath, scriptPath.Length);

            string exe;
            var argList = new List<string>();
            
            _logger.LogInformation("ScriptPath: '{ScriptPath}'", scriptPath);
            _logger.LogInformation("Starts with docker: {StartsWithDocker}", scriptPath.StartsWith("docker ", StringComparison.OrdinalIgnoreCase));

            if (scriptPath.StartsWith("docker ", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Detected Docker command");
                exe = "docker";
                
                var dockerArgs = scriptPath.Substring(7).Trim();
                _logger.LogInformation("Docker args: '{Args}'", dockerArgs);
                
                var parts = ParseDockerCommand(dockerArgs);
                argList.AddRange(parts);
                
                foreach (var p in parameters)
                {
                    if (!string.IsNullOrEmpty(p.Name))
                    {
                        argList.Add($"-{p.Name}");
                        if (!string.IsNullOrEmpty(p.Value))
                            argList.Add(p.Value);
                    }
                }
            }
            else
            {
                _logger.LogInformation("Taking PowerShell branch");
                exe = FindPowerShellExecutable();
                bool isPsScript = scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
                
                if (isPsScript)
                {
                    argList.Add("-NoProfile");
                    argList.Add("-ExecutionPolicy Bypass");
                    argList.Add("-File");
                    argList.Add(Quote(scriptPath));
                }
                else
                {
                    if (System.IO.File.Exists(scriptPath) && scriptPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exe = scriptPath;
                    }
                    else
                    {
                        argList.Add("-NoProfile");
                        argList.Add("-ExecutionPolicy Bypass");
                        argList.Add("-File");
                        argList.Add(Quote(scriptPath));
                    }
                }

                foreach (var p in parameters)
                {
                    argList.Add("-p");
                    argList.Add(EscapeArg(p.Name));
                    if (!string.IsNullOrEmpty(p.Value))
                        argList.Add(EscapeArg(p.Value));
                }
            }
            
            var finalArgs = string.Join(" ", argList);

            var sw = Stopwatch.StartNew();
            string stdout = "";
            string stderr = "";
            int exitCode = -1;

            _logger.LogInformation("About to execute: FileName='{FileName}', Arguments='{Arguments}'", exe, finalArgs);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = finalArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _logger.LogInformation("ProcessStartInfo created successfully");

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    _logger.LogError("Process.Start returned null");
                    stderr = "Failed to start process";
                }
                else
                {
                    _logger.LogInformation("Process started successfully with ID: {ProcessId}", proc.Id);
                    stdout = await proc.StandardOutput.ReadToEndAsync();
                    stderr = await proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit();
                    exitCode = proc.ExitCode;
                    _logger.LogInformation("Process completed with exit code: {ExitCode}", exitCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during process execution");
                stderr += (stderr.Length > 0 ? Environment.NewLine : "") + ex.Message;
            }

            _logger.LogInformation("Execution completed: ExitCode={ExitCode}, StdOut length={StdOutLength}, StdErr length={StdErrLength}", 
                exitCode, stdout?.Length ?? 0, stderr?.Length ?? 0);

            sw.Stop();

            var result = new RunResult
            {
                Success = exitCode == 0,
                ExitCode = exitCode,
                Command = exe + " " + finalArgs,
                Output = stdout ?? string.Empty,
                Error = stderr ?? string.Empty,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };

            return new JsonResult(result);
        }

        public IActionResult OnPostSaveUserConfig([FromBody] SaveUserConfigRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Id))
                return BadRequest(new { success = false, message = "Invalid request" });

            var baseTests = LoadApiTests();
            if (!baseTests.Any(t => t.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase)))
                return BadRequest(new { success = false, message = "Unknown test id" });

            var overrides = LoadUserOverrides();
            var cleaned = (request.Parameters ?? new List<ApiParam>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new ApiParam { Name = p.Name.Trim(), Value = p.Value ?? "" })
                .ToList();

            overrides[request.Id] = cleaned;
            SaveUserOverrides(overrides);

            return new JsonResult(new { success = true });
        }

        private List<ApiTestConfig> LoadApiTests()
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
                _logger.LogError(ex, "Failed to load API tests from appconfig.json");
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

        private Dictionary<string, List<ApiParam>> LoadUserOverrides()
        {
            try
            {
                if (!System.IO.File.Exists(UserOverrideFile))
                    return new Dictionary<string, List<ApiParam>>(StringComparer.OrdinalIgnoreCase);
                var json = System.IO.File.ReadAllText(UserOverrideFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, List<ApiParam>>>(json);
                return data ?? new Dictionary<string, List<ApiParam>>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load user overrides");
                return new Dictionary<string, List<ApiParam>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveUserOverrides(Dictionary<string, List<ApiParam>> overrides)
        {
            try
            {
                var json = JsonSerializer.Serialize(overrides, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(UserOverrideFile, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save user overrides");
            }
        }

        private string FindPowerShellExecutable()
        {
            var pwsh = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7\pwsh.exe");
            if (System.IO.File.Exists(pwsh)) return pwsh;
            return "powershell.exe";
        }

        private static string Quote(string s) =>
            s.Contains(' ') ? $"\"{s}\"" : s;

        private static string EscapeArg(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.Any(c => char.IsWhiteSpace(c) || c=='\"'))
                return "\"" + s.Replace("\"", "\\\"") + "\"";
            return s;
        }

        private List<string> ParseDockerCommand(string dockerArgs)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            bool escaped = false;
            
            for (int i = 0; i < dockerArgs.Length; i++)
            {
                char c = dockerArgs[i];
                
                if (escaped)
                {
                    current.Append(c);
                    escaped = false;
                    continue;
                }
                
                if (c == '\\')
                {
                    escaped = true;
                    current.Append(c);
                    continue;
                }
                
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    current.Append(c);
                    continue;
                }
                
                if (c == ' ' && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            
            if (current.Length > 0)
            {
                result.Add(current.ToString());
            }
            
            return result;
        }
    }
}
