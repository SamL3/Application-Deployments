using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
    public class TestModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<TestModel> _logger;

        public TestModel(IWebHostEnvironment env, ILogger<TestModel> logger)
        {
            _env = env;
            _logger = logger;
        }

        public List<ApiTestConfig> ApiTests { get; set; } = new();

        private string ApiTestsFile => Path.Combine(_env.WebRootPath, "apiTests.json");
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
            // Merge overrides (only values)
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

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostRunAsync([FromBody] RunTestRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "Invalid request" });

            var tests = LoadApiTests();
            var overrides = LoadUserOverrides(); // ensure run uses user's overrides where not replaced
            ApiTestConfig? test = null;

            if (!string.IsNullOrWhiteSpace(request.Id))
                test = tests.FirstOrDefault(t => t.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));

            if (test == null)
                return BadRequest(new { success = false, message = "Test not found" });

            // Compose parameter list (base -> user overrides -> request overrides)
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

            string scriptPath = test.ScriptPath;
            if (string.IsNullOrWhiteSpace(scriptPath))
                return BadRequest(new { success = false, message = "Script path not provided" });

            string exe = FindPowerShellExecutable();
            bool isPsScript = scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
            var argList = new List<string>();

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

            var finalArgs = string.Join(" ", argList);

            var sw = Stopwatch.StartNew();
            string stdout = "";
            string stderr = "";
            int exitCode = -1;
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

                using var proc = Process.Start(psi)!;
                stdout = await proc.StandardOutput.ReadToEndAsync();
                stderr = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            }
            catch (Exception ex)
            {
                stderr += (stderr.Length > 0 ? Environment.NewLine : "") + ex.Message;
            }
            sw.Stop();

            var result = new RunResult
            {
                Success = exitCode == 0,
                ExitCode = exitCode,
                Command = exe + " " + finalArgs,
                Output = stdout,
                Error = stderr,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };

            return new JsonResult(result);
        }

        [ValidateAntiForgeryToken]
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
                if (!System.IO.File.Exists(ApiTestsFile))
                    return new List<ApiTestConfig>();
                var json = System.IO.File.ReadAllText(ApiTestsFile);
                return JsonSerializer.Deserialize<List<ApiTestConfig>>(json) ?? new List<ApiTestConfig>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load API tests");
                return new List<ApiTestConfig>();
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
    }
}