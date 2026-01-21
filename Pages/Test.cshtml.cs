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

namespace DevApp.Pages
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
            ViewData["Title"] = "DevApp - API Tests";

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
            // Now expected to start with: docker ...
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

        // Replace the whole OnPostRunAsync with this version to always return a uniform RunResult
        public async Task<IActionResult> OnPostRunAsync([FromBody] RunTestRequest request)
        {
            RunResult Fail(string cmd, string err, int exit = -1) => new()
            {
                Success = false,
                ExitCode = exit,
                Command = cmd,
                Output = "",
                Error = err,
                DurationMs = 0
            };

            if (request == null)
                return new JsonResult(Fail("(none)", "Invalid request"));

            var tests = LoadApiTests();
            var overrides = LoadUserOverrides();
            var test = !string.IsNullOrWhiteSpace(request.Id)
                ? tests.FirstOrDefault(t => t.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase))
                : null;

            if (test == null)
                return new JsonResult(Fail(request.Id ?? "(none)", "Test not found"));

            // Merge parameters
            var parameters = test.Parameters
                .Select(p => new ApiParam { Name = p.Name, Value = p.Value })
                .ToList();

            if (overrides.TryGetValue(test.Id, out var userParamOverrides))
            {
                foreach (var ov in userParamOverrides)
                {
                    var match = parameters.FirstOrDefault(p => p.Name.Equals(ov.Name, StringComparison.OrdinalIgnoreCase));
                    if (match != null) match.Value = ov.Value;
                }
            }
            if (request.Parameters != null)
            {
                foreach (var p in request.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                {
                    var match = parameters.FirstOrDefault(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                    if (match != null) match.Value = p.Value ?? "";
                    else parameters.Add(new ApiParam { Name = p.Name, Value = p.Value ?? "" });
                }
            }

            string scriptPath = test.ScriptPath.Trim();
            string exe;
            List<string> argTokens = new();

            bool isDocker = scriptPath.StartsWith("docker ", StringComparison.OrdinalIgnoreCase);
            if (isDocker)
            {
                exe = "docker";
                var dockerArgs = scriptPath[7..].Trim();
                var parts = ParseDockerCommand(dockerArgs)
                    .Where(p => !string.Equals(p, "-i", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(p, "-t", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(p, "-it", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(p, "-ti", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(p, "--tty", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                argTokens.AddRange(parts);
                foreach (var p in parameters)
                {
                    if (!string.IsNullOrEmpty(p.Name))
                    {
                        argTokens.Add($"-{p.Name}");
                        if (!string.IsNullOrEmpty(p.Value))
                            argTokens.Add(p.Value);
                    }
                }
            }
            else
            {
                // Allow executing a direct executable path (no PowerShell)
                if (!System.IO.File.Exists(scriptPath))
                    return new JsonResult(Fail(scriptPath, "Script/executable not found"));

                exe = scriptPath;
                foreach (var p in parameters)
                {
                    if (!string.IsNullOrWhiteSpace(p.Name))
                    {
                        argTokens.Add($"-{p.Name}");
                        if (!string.IsNullOrEmpty(p.Value))
                            argTokens.Add(p.Value);
                    }
                }
            }

            string finalArgs = string.Join(" ", argTokens.Select(QuoteIfNeeded));
            var fullCommand = exe + (finalArgs.Length > 0 ? " " + finalArgs : "");

            var sw = Stopwatch.StartNew();
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            int exitCode = -1;
            bool timedOut = false;
            const int timeoutMs = 60000;

            _logger.LogInformation("Starting test execution: {Command}", fullCommand);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = finalArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    _logger.LogInformation("Process started, PID: {ProcessId}", proc.Id);

                    // Read output synchronously using tasks to avoid BeginOutputReadLine issues with Docker
                    var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                    var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

                    // Create cancellation token for timeout
                    using (var cts = new System.Threading.CancellationTokenSource(timeoutMs))
                    {
                        try
                        {
                            // Wait for process to exit
                            await proc.WaitForExitAsync(cts.Token);
                            _logger.LogInformation("Process exited, reading remaining output");
                            
                            // Wait for all output to be read (with a reasonable timeout)
                            var allOutput = Task.WhenAll(stdoutTask, stderrTask);
                            if (await Task.WhenAny(allOutput, Task.Delay(5000)) == allOutput)
                            {
                                stdoutBuilder.Append(await stdoutTask);
                                stderrBuilder.Append(await stderrTask);
                            }
                            else
                            {
                                _logger.LogWarning("Output reading timed out after process exit");
                                stderrBuilder.AppendLine("[WARNING] Output reading timed out, some output may be missing");
                            }
                            
                            exitCode = proc.ExitCode;
                            _logger.LogInformation("Process completed. ExitCode: {ExitCode}, STDOUT bytes: {StdoutLength}, STDERR bytes: {StderrLength}", 
                                exitCode, stdoutBuilder.Length, stderrBuilder.Length);
                        }
                        catch (System.OperationCanceledException)
                        {
                            timedOut = true;
                            stderrBuilder.AppendLine($"[TIMEOUT] Process exceeded {timeoutMs} ms and was terminated.");
                            
                            try
                            {
                                if (!proc.HasExited)
                                {
                                    proc.Kill(true);
                                    proc.WaitForExit(5000);
                                }
                            }
                            catch (Exception killEx)
                            {
                                _logger.LogError(killEx, "Error killing process on timeout");
                            }
                        }
                    }

                    // Final cleanup
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill(true);
                            proc.WaitForExit(2000);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in final process cleanup");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running process");
                stderrBuilder.AppendLine($"Process execution error: {ex.Message}");
                exitCode = -1;
            }

            sw.Stop();

            var result = new RunResult
            {
                Success = !timedOut && exitCode == 0,
                ExitCode = exitCode,
                Command = fullCommand,
                Output = RemoveAnsiCodes(stdoutBuilder.ToString()),
                Error = RemoveAnsiCodes(stderrBuilder.ToString()),
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

        private static string? ExtractDockerImageName(string scriptPath)
        {
            try
            {
                // scriptPath format: "docker run --rm cstcontainerregistry.azurecr.io/saml3/k6tests ./CreateUser.ps1"
                // We want to extract: "cstcontainerregistry.azurecr.io/saml3/k6tests"
                var parts = scriptPath.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                
                // Find the image name - it's typically after "run" and after any flags (starting with -)
                bool foundRun = false;
                foreach (var part in parts)
                {
                    if (part.Equals("run", StringComparison.OrdinalIgnoreCase))
                    {
                        foundRun = true;
                        continue;
                    }
                    
                    if (foundRun && !part.StartsWith("-") && !part.StartsWith("./"))
                    {
                        return part;
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }
            
            return null;
        }

        private static string TrimTrailingNewline(string s) =>
            s.EndsWith(Environment.NewLine) ? s[..^Environment.NewLine.Length] : s;

        private static string QuoteIfNeeded(string token)
        {
            if (string.IsNullOrEmpty(token)) return "\"\"";
            if (token.Any(char.IsWhiteSpace) && !(token.StartsWith('"') && token.EndsWith('"')))
                return "\"" + token.Replace("\"", "\\\"") + "\"";
            return token;
        }

        // Remove ANSI codes from output
        private static string RemoveAnsiCodes(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            
            var cleaned = input;
            
            // Remove actual ANSI escape sequences
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\x1b\[[0-9;]*m", "");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\x1b\[[0-9;]*[A-Za-z]", "");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\x1b].*?\x07", "");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\x1b\].*?(\x1b\\|[\x07\x08])", "");
            
            // Remove leftover ANSI fragments like [0m, [31m, etc.
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\[[\d;]+m", "");
            
            // Remove source=console suffix
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+source=console\s*$", "", System.Text.RegularExpressions.RegexOptions.Multiline);
            
            // Clean up any remaining escaped characters
            cleaned = cleaned.Replace("\\x1b", "");
            cleaned = cleaned.Replace("\\\"", "\"");
            cleaned = cleaned.Replace("\\/", "/");
            cleaned = cleaned.Replace("\\n", " ");
            cleaned = cleaned.Replace("\\r", "");
            cleaned = cleaned.Replace("\\t", " ");
            
            // Add blank line between log entries for readability
            var lines = cleaned.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var result = new StringBuilder();
            
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    result.AppendLine(line);
                    result.AppendLine(); // Add blank line after each non-empty line
                }
            }
            
            return result.ToString().TrimEnd();
        }
    }
}
