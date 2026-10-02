using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Sessions;

namespace NeuralBridge.Infrastructure.Hosting;

/// <summary>Runs <see cref="ISessionCleanupService.SweepAsync"/> on a fixed interval. A failed sweep is logged and retried on the next tick.</summary>
public sealed class SessionCleanupHostedService : BackgroundService
{
    private readonly ISessionCleanupService _cleanup;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly ILogger<SessionCleanupHostedService> _logger;

    public SessionCleanupHostedService(
        ISessionCleanupService cleanup,
        TimeProvider time,
        IOptions<SessionOptions> options,
        ILogger<SessionCleanupHostedService> logger)
    {
        _cleanup = cleanup;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.CleanupIntervalSeconds), _time);
        do
        {
            try
            {
                await _cleanup.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session cleanup sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Initializes the database before the app starts serving requests.</summary>
public sealed class DatabaseInitializationHostedService : IHostedService
{
    private readonly Persistence.IDatabaseInitializer _initializer;
    private readonly IOptions<Persistence.PersistenceOptions> _options;

    public DatabaseInitializationHostedService(Persistence.IDatabaseInitializer initializer, IOptions<Persistence.PersistenceOptions> options)
    {
        _initializer = initializer;
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _options.Value.InitializeDatabaseOnStartup ? _initializer.InitializeAsync(cancellationToken) : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
