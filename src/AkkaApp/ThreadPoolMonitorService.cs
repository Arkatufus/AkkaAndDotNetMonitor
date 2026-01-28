using System.Diagnostics;

namespace AkkaApp;

/// <summary>
/// A dedicated monitoring service that runs on its own thread (NOT the ThreadPool).
/// This allows it to continue monitoring and logging even when the ThreadPool is starved.
///
/// Key insight: During ThreadPool starvation, ASP.NET endpoints stop responding,
/// metrics exporters stop working, and anything using the ThreadPool fails.
/// This service uses a dedicated Thread so it can observe and report on starvation.
/// </summary>
public sealed class ThreadPoolMonitorService : BackgroundService
{
    private readonly ILogger<ThreadPoolMonitorService> _logger;
    private readonly TimeSpan _monitorInterval = TimeSpan.FromSeconds(2);
    private readonly Stopwatch _stopwatch = new();
    private int _consecutiveSlowChecks = 0;
    private const int SlowThresholdMs = 100; // If checking takes >100ms, ThreadPool is struggling

    public ThreadPoolMonitorService(ILogger<ThreadPoolMonitorService> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // CRITICAL: Run on a dedicated thread, NOT the ThreadPool
        // This ensures monitoring continues even during ThreadPool starvation
        var monitorThread = new Thread(() => MonitorLoop(stoppingToken))
        {
            Name = "ThreadPoolMonitor",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal // Give it priority during starvation
        };
        monitorThread.Start();

        return Task.CompletedTask;
    }

    private void MonitorLoop(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ThreadPoolMonitor] Started on dedicated thread {ThreadId}", Environment.CurrentManagedThreadId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _stopwatch.Restart();

                // Get ThreadPool stats
                ThreadPool.GetAvailableThreads(out var workerAvailable, out var ioAvailable);
                ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);
                ThreadPool.GetMinThreads(out var workerMin, out var ioMin);

                var workerInUse = workerMax - workerAvailable;
                var ioInUse = ioMax - ioAvailable;

                // Measure how long a ThreadPool work item takes to execute
                var queuedTime = MeasureThreadPoolDelay();

                _stopwatch.Stop();
                var checkDuration = _stopwatch.ElapsedMilliseconds;

                // Detect starvation indicators
                var isStarved = workerAvailable == 0 || queuedTime > SlowThresholdMs;
                var isNearStarvation = workerAvailable <= 2 || queuedTime > 50;

                if (isStarved)
                {
                    _consecutiveSlowChecks++;
                    _logger.LogWarning(
                        "[ThreadPoolMonitor] STARVATION DETECTED! " +
                        "Workers: {InUse}/{Max} (available: {Available}), " +
                        "IO: {IoInUse}/{IoMax}, " +
                        "QueueDelay: {QueueDelay}ms, " +
                        "ConsecutiveSlowChecks: {SlowChecks}",
                        workerInUse, workerMax, workerAvailable,
                        ioInUse, ioMax,
                        queuedTime,
                        _consecutiveSlowChecks);
                }
                else if (isNearStarvation)
                {
                    _logger.LogWarning(
                        "[ThreadPoolMonitor] Near starvation - " +
                        "Workers: {InUse}/{Max} (available: {Available}), " +
                        "QueueDelay: {QueueDelay}ms",
                        workerInUse, workerMax, workerAvailable, queuedTime);
                    _consecutiveSlowChecks = 0;
                }
                else
                {
                    _logger.LogDebug(
                        "[ThreadPoolMonitor] Healthy - " +
                        "Workers: {InUse}/{Max}, IO: {IoInUse}/{IoMax}, QueueDelay: {QueueDelay}ms",
                        workerInUse, workerMax, ioInUse, ioMax, queuedTime);
                    _consecutiveSlowChecks = 0;
                }

                // Sleep on this dedicated thread (not using Task.Delay which uses ThreadPool timers)
                Thread.Sleep(_monitorInterval);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[ThreadPoolMonitor] Error during monitoring");
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }
        }

        _logger.LogInformation("[ThreadPoolMonitor] Stopped");
    }

    /// <summary>
    /// Measures how long it takes for a ThreadPool work item to start executing.
    /// High values indicate ThreadPool starvation - work is queued but can't run.
    /// </summary>
    private long MeasureThreadPoolDelay()
    {
        var sw = Stopwatch.StartNew();
        using var mre = new ManualResetEventSlim(false);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            mre.Set();
        });

        // Wait up to 5 seconds for the work item to execute
        if (!mre.Wait(TimeSpan.FromSeconds(5)))
        {
            return 5000; // Severe starvation - work item didn't execute in 5s
        }

        sw.Stop();
        return sw.ElapsedMilliseconds;
    }
}
