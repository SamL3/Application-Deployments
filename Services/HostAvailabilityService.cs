using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApplicationDeployment.Services
{
    public class HostStatus
    {
        public string Host { get; set; } = string.Empty;
        public bool Accessible { get; set; }          // Now means: host reachable (ping ok)
        public bool? RootExists { get; set; }          // New: whether the CSTApps root folder exists
        public long? LatencyMs { get; set; }
        public DateTime UtcChecked { get; set; }
        public string? Message { get; set; }
    }

    public class HostAvailabilityService : IHostedService
    {
        private readonly ILogger<HostAvailabilityService> _logger;
        private readonly IConfiguration _config;
        private readonly object _scanLock = new();
        private CancellationTokenSource? _cts;

        private readonly ConcurrentDictionary<string, HostStatus> _statuses =
            new(StringComparer.OrdinalIgnoreCase);

        public bool ScanInProgress { get; private set; }
        public int Total { get; private set; }

        private int _completed;
        public int Completed
        {
            get => _completed;
            private set => _completed = value;
        }

        public HostAvailabilityService(ILogger<HostAvailabilityService> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("HostAvailabilityService starting initial scan.");
            _ = TriggerScanAsync(); // fire & forget
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts?.Cancel();
            return Task.CompletedTask;
        }

        public IReadOnlyDictionary<string, HostStatus> GetStatuses() => _statuses;

        public async Task TriggerScanAsync()
        {
            lock (_scanLock)
            {
                if (ScanInProgress) return;
                ScanInProgress = true;
                Completed = 0;
                Total = 0;
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
            }

            try
            {
                var serverObjects = _config.GetSection("Servers").Get<List<object>>() ?? new();
                // Accept both array of strings or objects with HostName
                var serverList = _config.GetSection("Servers").GetChildren()
                    .Select(c =>
                    {
                        var host = c.GetValue<string>("HostName");
                        if (string.IsNullOrWhiteSpace(host))
                            host = c.Get<string>(); // string entry fallback
                        return host;
                    })
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Total = serverList.Count;
                if (Total == 0)
                {
                    _logger.LogWarning("No servers configured for availability scan.");
                    return;
                }

                var root = _config["CSTApps"];
                var semaphore = new SemaphoreSlim(6); // limit concurrency
                var tasks = new List<Task>();

                foreach (var host in serverList)
                {
                    await semaphore.WaitAsync(_cts!.Token);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var status = await ProbeHostAsync(host, root, _cts.Token);
                            _statuses[host] = status;
                        }
                        catch (Exception ex)
                        {
                            _statuses[host] = new HostStatus
                            {
                                Host = host,
                                Accessible = false,
                                Message = ex.Message,
                                UtcChecked = DateTime.UtcNow
                            };
                        }
                        finally
                        {
                            Interlocked.Increment(ref _completed);
                            semaphore.Release();
                        }
                    }, _cts.Token));
                }

                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Host availability scan canceled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Host availability scan failed.");
            }
            finally
            {
                ScanInProgress = false;
            }
        }

        private async Task<HostStatus> ProbeHostAsync(string host, string? root, CancellationToken ct)
        {
            var result = new HostStatus { Host = host, UtcChecked = DateTime.UtcNow };
            try
            {
                using var ping = new Ping();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(host, 800);
                sw.Stop();
                if (reply.Status != IPStatus.Success)
                {
                    result.Accessible = false;
                    result.Message = $"Ping {reply.Status}";
                    return result;
                }

                result.Accessible = true;                 // Host is reachable
                result.LatencyMs = sw.ElapsedMilliseconds;

                // If no root configured, we stop here
                if (string.IsNullOrWhiteSpace(root))
                    return result;

                var adminShare = $@"\\{host}\C$";
                bool adminShareExists = false;
                try { adminShareExists = Directory.Exists(adminShare); }
                catch (Exception ex)
                {
                    result.Message = "Admin share error: " + ex.Message;
                    return result; // keep Accessible = true (ping ok) but share not reachable
                }

                if (!adminShareExists)
                {
                    result.Message = "Admin share not accessible";
                    return result;
                }

                var uncRoot = $@"\\{host}\C$\{root.TrimStart('\\').TrimEnd('\\')}";
                bool appRootExists = false;
                try { appRootExists = Directory.Exists(uncRoot); }
                catch (Exception ex)
                {
                    result.Message = "UNC error: " + ex.Message;
                    return result;
                }

                result.RootExists = appRootExists;
                if (!appRootExists)
                {
                    // Do NOT flip Accessible to false (host is still online)
                    result.Message ??= "App root missing";
                }
            }
            catch (Exception ex)
            {
                result.Accessible = false;
                result.Message = ex.Message;
            }
            return result;
        }
    }
}