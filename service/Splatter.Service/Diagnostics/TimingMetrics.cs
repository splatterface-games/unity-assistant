// Performance Timing Instrumentation - M6.4 Implementation
// Tracks latency and timing for key operations

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Splatter.Service.Diagnostics;

/// <summary>
/// Timing operation types.
/// </summary>
public enum TimingOperation
{
    /// <summary>Time from request to first token from LLM.</summary>
    FirstTokenLatency,

    /// <summary>Full LLM response time.</summary>
    FullResponseLatency,

    /// <summary>Index scan operation (file search, etc.).</summary>
    IndexScan,

    /// <summary>Permission prompt shown to completion.</summary>
    PermissionPrompt,

    /// <summary>Code patch application.</summary>
    PatchApply,

    /// <summary>Event journal replay.</summary>
    EventReplay,

    /// <summary>Tool execution time.</summary>
    ToolExecution,

    /// <summary>Context packing operation.</summary>
    ContextPacking,

    /// <summary>WebSocket message round trip.</summary>
    WebSocketRoundTrip,

    /// <summary>Database query execution.</summary>
    DatabaseQuery,

    /// <summary>File system operation.</summary>
    FileSystemOperation,

    /// <summary>Session recovery scan.</summary>
    RecoveryScan,

    /// <summary>Diagnostics export.</summary>
    DiagnosticsExport,

    /// <summary>Checkpoint creation.</summary>
    CheckpointCreate,

    /// <summary>Checkpoint restore.</summary>
    CheckpointRestore,

    /// <summary>Generator job execution.</summary>
    GeneratorJob
}

/// <summary>
/// Single timing sample.
/// </summary>
public sealed record TimingSample(
    TimingOperation Operation,
    double DurationMs,
    DateTimeOffset Timestamp,
    string? CorrelationId,
    IReadOnlyDictionary<string, string>? Tags);

/// <summary>
/// Aggregated timing statistics.
/// </summary>
public sealed record TimingStats(
    TimingOperation Operation,
    int SampleCount,
    double MinMs,
    double MaxMs,
    double AvgMs,
    double MedianMs,
    double P95Ms,
    double P99Ms,
    double StdDevMs,
    DateTimeOffset FirstSample,
    DateTimeOffset LastSample);

/// <summary>
/// Performance budgets for operations.
/// </summary>
public sealed record PerformanceBudget(
    TimingOperation Operation,
    double TargetMs,
    double WarningMs,
    double CriticalMs);

/// <summary>
/// Budget violation event.
/// </summary>
public sealed record BudgetViolation(
    TimingOperation Operation,
    double ActualMs,
    double BudgetMs,
    BudgetViolationSeverity Severity,
    DateTimeOffset Timestamp,
    string? CorrelationId);

public enum BudgetViolationSeverity
{
    Warning,
    Critical
}

/// <summary>
/// Interface for timing metrics collection.
/// </summary>
public interface ITimingMetrics
{
    /// <summary>
    /// Starts a timing scope.
    /// </summary>
    ITimingScope StartScope(TimingOperation operation, string? correlationId = null, Dictionary<string, string>? tags = null);

    /// <summary>
    /// Records a timing sample.
    /// </summary>
    void Record(TimingOperation operation, double durationMs, string? correlationId = null, Dictionary<string, string>? tags = null);

    /// <summary>
    /// Gets statistics for an operation.
    /// </summary>
    TimingStats? GetStats(TimingOperation operation);

    /// <summary>
    /// Gets combined metrics summary.
    /// </summary>
    DiagnosticsTimingMetrics GetMetrics();

    /// <summary>
    /// Gets recent samples.
    /// </summary>
    IReadOnlyList<TimingSample> GetRecentSamples(TimingOperation? operation = null, int maxCount = 100);

    /// <summary>
    /// Checks if operation is within budget.
    /// </summary>
    BudgetViolation? CheckBudget(TimingOperation operation, double durationMs);

    /// <summary>
    /// Gets recent budget violations.
    /// </summary>
    IReadOnlyList<BudgetViolation> GetRecentViolations(int maxCount = 50);

    /// <summary>
    /// Clears all metrics.
    /// </summary>
    void Reset();

    /// <summary>
    /// Event fired on budget violation.
    /// </summary>
    event EventHandler<BudgetViolation>? BudgetViolated;
}

/// <summary>
/// Timing scope for automatic duration tracking.
/// </summary>
public interface ITimingScope : IDisposable
{
    /// <summary>
    /// Adds a tag to the scope.
    /// </summary>
    ITimingScope WithTag(string key, string value);

    /// <summary>
    /// Marks the scope as failed.
    /// </summary>
    ITimingScope MarkFailed();

    /// <summary>
    /// Gets current elapsed time.
    /// </summary>
    double ElapsedMs { get; }
}

public sealed class TimingMetrics : ITimingMetrics
{
    private readonly ILogger<TimingMetrics> _logger;
    private readonly ConcurrentDictionary<TimingOperation, ConcurrentQueue<TimingSample>> _samples = new();
    private readonly ConcurrentQueue<BudgetViolation> _violations = new();
    private readonly int _maxSamplesPerOperation;
    private readonly int _maxViolations;

    public event EventHandler<BudgetViolation>? BudgetViolated;

    // Performance budgets for key operations
    private static readonly Dictionary<TimingOperation, PerformanceBudget> Budgets = new()
    {
        [TimingOperation.FirstTokenLatency] = new(TimingOperation.FirstTokenLatency, 500, 1500, 5000),
        [TimingOperation.IndexScan] = new(TimingOperation.IndexScan, 100, 500, 2000),
        [TimingOperation.PermissionPrompt] = new(TimingOperation.PermissionPrompt, 200, 500, 1000),
        [TimingOperation.PatchApply] = new(TimingOperation.PatchApply, 50, 200, 1000),
        [TimingOperation.EventReplay] = new(TimingOperation.EventReplay, 200, 1000, 5000),
        [TimingOperation.ToolExecution] = new(TimingOperation.ToolExecution, 500, 2000, 30000),
        [TimingOperation.ContextPacking] = new(TimingOperation.ContextPacking, 100, 500, 2000),
        [TimingOperation.WebSocketRoundTrip] = new(TimingOperation.WebSocketRoundTrip, 10, 50, 200),
        [TimingOperation.DatabaseQuery] = new(TimingOperation.DatabaseQuery, 20, 100, 500),
        [TimingOperation.FileSystemOperation] = new(TimingOperation.FileSystemOperation, 50, 200, 1000),
        [TimingOperation.RecoveryScan] = new(TimingOperation.RecoveryScan, 500, 2000, 10000),
        [TimingOperation.DiagnosticsExport] = new(TimingOperation.DiagnosticsExport, 1000, 5000, 30000),
        [TimingOperation.CheckpointCreate] = new(TimingOperation.CheckpointCreate, 200, 1000, 5000),
        [TimingOperation.CheckpointRestore] = new(TimingOperation.CheckpointRestore, 500, 2000, 10000),
        [TimingOperation.GeneratorJob] = new(TimingOperation.GeneratorJob, 5000, 30000, 120000)
    };

    public TimingMetrics(ILogger<TimingMetrics> logger, int maxSamplesPerOperation = 1000, int maxViolations = 500)
    {
        _logger = logger;
        _maxSamplesPerOperation = maxSamplesPerOperation;
        _maxViolations = maxViolations;
    }

    public ITimingScope StartScope(TimingOperation operation, string? correlationId = null, Dictionary<string, string>? tags = null)
    {
        return new TimingScope(this, operation, correlationId, tags);
    }

    public void Record(TimingOperation operation, double durationMs, string? correlationId = null, Dictionary<string, string>? tags = null)
    {
        var sample = new TimingSample(operation, durationMs, DateTimeOffset.UtcNow, correlationId, tags);

        var queue = _samples.GetOrAdd(operation, _ => new ConcurrentQueue<TimingSample>());
        queue.Enqueue(sample);

        // Trim to max size
        while (queue.Count > _maxSamplesPerOperation)
        {
            queue.TryDequeue(out _);
        }

        // Check budget
        var violation = CheckBudget(operation, durationMs);
        if (violation != null)
        {
            RecordViolation(violation);
        }

        _logger.LogTrace("Timing: {Operation} = {Duration:F2}ms", operation, durationMs);
    }

    public TimingStats? GetStats(TimingOperation operation)
    {
        if (!_samples.TryGetValue(operation, out var queue))
            return null;

        var samples = queue.ToArray();
        if (samples.Length == 0)
            return null;

        var durations = samples.Select(s => s.DurationMs).OrderBy(d => d).ToArray();
        var count = durations.Length;

        return new TimingStats(
            operation,
            count,
            durations[0],
            durations[^1],
            durations.Average(),
            durations[count / 2],
            durations[(int)(count * 0.95)],
            durations[(int)(count * 0.99)],
            CalculateStdDev(durations),
            samples.Min(s => s.Timestamp),
            samples.Max(s => s.Timestamp));
    }

    public DiagnosticsTimingMetrics GetMetrics()
    {
        var firstToken = GetStats(TimingOperation.FirstTokenLatency);
        var toolExec = GetStats(TimingOperation.ToolExecution);
        var permPrompt = GetStats(TimingOperation.PermissionPrompt);
        var patchApply = GetStats(TimingOperation.PatchApply);
        var indexScan = GetStats(TimingOperation.IndexScan);
        var eventReplay = GetStats(TimingOperation.EventReplay);

        var totalSamples = _samples.Values.Sum(q => q.Count);

        return new DiagnosticsTimingMetrics(
            firstToken?.AvgMs ?? 0,
            toolExec?.AvgMs ?? 0,
            permPrompt?.AvgMs ?? 0,
            patchApply?.AvgMs ?? 0,
            indexScan?.AvgMs ?? 0,
            eventReplay?.AvgMs ?? 0,
            totalSamples);
    }

    public IReadOnlyList<TimingSample> GetRecentSamples(TimingOperation? operation = null, int maxCount = 100)
    {
        if (operation.HasValue)
        {
            if (!_samples.TryGetValue(operation.Value, out var queue))
                return Array.Empty<TimingSample>();

            return queue.ToArray()
                .OrderByDescending(s => s.Timestamp)
                .Take(maxCount)
                .ToList();
        }

        return _samples.Values
            .SelectMany(q => q)
            .OrderByDescending(s => s.Timestamp)
            .Take(maxCount)
            .ToList();
    }

    public BudgetViolation? CheckBudget(TimingOperation operation, double durationMs)
    {
        if (!Budgets.TryGetValue(operation, out var budget))
            return null;

        BudgetViolationSeverity? severity = null;
        double budgetMs = 0;

        if (durationMs >= budget.CriticalMs)
        {
            severity = BudgetViolationSeverity.Critical;
            budgetMs = budget.CriticalMs;
        }
        else if (durationMs >= budget.WarningMs)
        {
            severity = BudgetViolationSeverity.Warning;
            budgetMs = budget.WarningMs;
        }

        if (severity == null)
            return null;

        return new BudgetViolation(operation, durationMs, budgetMs, severity.Value, DateTimeOffset.UtcNow, null);
    }

    public IReadOnlyList<BudgetViolation> GetRecentViolations(int maxCount = 50)
    {
        return _violations.ToArray()
            .OrderByDescending(v => v.Timestamp)
            .Take(maxCount)
            .ToList();
    }

    public void Reset()
    {
        _samples.Clear();
        _violations.Clear();
        _logger.LogInformation("Timing metrics reset");
    }

    private void RecordViolation(BudgetViolation violation)
    {
        _violations.Enqueue(violation);

        while (_violations.Count > _maxViolations)
        {
            _violations.TryDequeue(out _);
        }

        var logLevel = violation.Severity == BudgetViolationSeverity.Critical ? LogLevel.Warning : LogLevel.Debug;
        _logger.Log(logLevel, "Budget violation: {Operation} took {Actual:F2}ms (budget: {Budget:F2}ms, severity: {Severity})",
            violation.Operation, violation.ActualMs, violation.BudgetMs, violation.Severity);

        BudgetViolated?.Invoke(this, violation);
    }

    private static double CalculateStdDev(double[] values)
    {
        if (values.Length < 2)
            return 0;

        var avg = values.Average();
        var sumSquares = values.Sum(v => (v - avg) * (v - avg));
        return Math.Sqrt(sumSquares / (values.Length - 1));
    }

    private sealed class TimingScope : ITimingScope
    {
        private readonly TimingMetrics _metrics;
        private readonly TimingOperation _operation;
        private readonly string? _correlationId;
        private readonly Dictionary<string, string> _tags;
        private readonly Stopwatch _stopwatch;
        private bool _failed;
        private bool _disposed;

        public TimingScope(TimingMetrics metrics, TimingOperation operation, string? correlationId, Dictionary<string, string>? tags)
        {
            _metrics = metrics;
            _operation = operation;
            _correlationId = correlationId;
            _tags = tags ?? new Dictionary<string, string>();
            _stopwatch = Stopwatch.StartNew();
        }

        public double ElapsedMs => _stopwatch.Elapsed.TotalMilliseconds;

        public ITimingScope WithTag(string key, string value)
        {
            _tags[key] = value;
            return this;
        }

        public ITimingScope MarkFailed()
        {
            _failed = true;
            return this;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _stopwatch.Stop();

            if (_failed)
            {
                _tags["failed"] = "true";
            }

            _metrics.Record(_operation, _stopwatch.Elapsed.TotalMilliseconds, _correlationId, _tags);
        }
    }
}

/// <summary>
/// Extension methods for timing.
/// </summary>
public static class TimingExtensions
{
    /// <summary>
    /// Executes an action with timing.
    /// </summary>
    public static T Timed<T>(this ITimingMetrics metrics, TimingOperation operation, Func<T> action, string? correlationId = null)
    {
        using var scope = metrics.StartScope(operation, correlationId);
        try
        {
            return action();
        }
        catch
        {
            scope.MarkFailed();
            throw;
        }
    }

    /// <summary>
    /// Executes an async action with timing.
    /// </summary>
    public static async Task<T> TimedAsync<T>(this ITimingMetrics metrics, TimingOperation operation, Func<Task<T>> action, string? correlationId = null)
    {
        using var scope = metrics.StartScope(operation, correlationId);
        try
        {
            return await action();
        }
        catch
        {
            scope.MarkFailed();
            throw;
        }
    }

    /// <summary>
    /// Executes an async action with timing (no return value).
    /// </summary>
    public static async Task TimedAsync(this ITimingMetrics metrics, TimingOperation operation, Func<Task> action, string? correlationId = null)
    {
        using var scope = metrics.StartScope(operation, correlationId);
        try
        {
            await action();
        }
        catch
        {
            scope.MarkFailed();
            throw;
        }
    }
}
