using LogRag.Api.Telemetry;
using Microsoft.Extensions.Hosting;

namespace LogRag.Api.Telemetry;

/// <summary>
/// Logs a metrics snapshot to console every 30 seconds.
/// </summary>
public sealed class MetricsConsoleReporter : BackgroundService
{
    private readonly ILogger<MetricsConsoleReporter> _logger;

    public MetricsConsoleReporter(ILogger<MetricsConsoleReporter> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("{Metrics}", MetricsSnapshot.GetSnapshot());
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
