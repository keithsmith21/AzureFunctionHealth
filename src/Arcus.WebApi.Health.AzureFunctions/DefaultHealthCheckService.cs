﻿//Copyright(c) .NET Foundation and Contributors
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FunctionHealthCheck
{
    internal class DefaultHealthCheckService : HealthCheckService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptions<HealthCheckServiceOptions> _options;
        private readonly ILogger<DefaultHealthCheckService> _logger;

        public DefaultHealthCheckService(
            IServiceScopeFactory scopeFactory,
            IOptions<HealthCheckServiceOptions> options,
            ILogger<DefaultHealthCheckService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // We're specifically going out of our way to do this at startup time. We want to make sure you
            // get any kind of health-check related error as early as possible. Waiting until someone
            // actually tries to **run** health checks would be real baaaaad.
            ValidateRegistrations(_options.Value.Registrations);
        }
        public override async Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool> predicate,
            CancellationToken cancellationToken = default)
        {
            var registrations = _options.Value.Registrations;

            using (var scope = _scopeFactory.CreateScope())
            {
                var context = new HealthCheckContext();
                var entries = new Dictionary<string, HealthReportEntry>(StringComparer.OrdinalIgnoreCase);

                var totalTime = ValueStopwatch.StartNew();
                Log.HealthCheckProcessingBegin(_logger);

                foreach (var registration in registrations)
                {
                    if (predicate != null && !predicate(registration))
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    var healthCheck = registration.Factory(scope.ServiceProvider);

                    var stopwatch = ValueStopwatch.StartNew();
                    context.Registration = registration;

                    Log.HealthCheckBegin(_logger, registration);

                    HealthReportEntry entry;
                    try
                    {
                        var result = await healthCheck.CheckHealthAsync(context, cancellationToken);
                        var duration = stopwatch.Elapsed;

                        entry = new HealthReportEntry(
                            status: result.Status,
                            description: result.Description,
                            duration: duration,
                            exception: result.Exception,
                            data: result.Data);

                        Log.HealthCheckEnd(_logger, registration, entry, duration);
                        Log.HealthCheckData(_logger, registration, entry);
                    }

                    // Allow cancellation to propagate.
                    catch (Exception ex) when (ex as OperationCanceledException == null)
                    {
                        var duration = stopwatch.Elapsed;
                        entry = new HealthReportEntry(
                            status: HealthStatus.Unhealthy,
                            description: ex.Message,
                            duration: duration,
                            exception: ex,
                            data: null);

                        Log.HealthCheckError(_logger, registration, ex, duration);
                    }

                    entries[registration.Name] = entry;
                }

                var totalElapsedTime = totalTime.Elapsed;
                var report = new HealthReport(entries, totalElapsedTime);
                Log.HealthCheckProcessingEnd(_logger, report.Status, totalElapsedTime);
                return report;
            }
        }

        private static void ValidateRegistrations(IEnumerable<HealthCheckRegistration> registrations)
        {
            // Scan the list for duplicate names to provide a better error if there are duplicates.
            var duplicateNames = registrations
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateNames.Count > 0)
            {
                throw new ArgumentException($"Duplicate health checks were registered with the name(s): {string.Join(", ", duplicateNames)}", nameof(registrations));
            }
        }

        internal static class EventIds
        {
            public static readonly EventId HealthCheckProcessingBegin = new EventId(100, "HealthCheckProcessingBegin");
            public static readonly EventId HealthCheckProcessingEnd = new EventId(101, "HealthCheckProcessingEnd");

            public static readonly EventId HealthCheckBegin = new EventId(102, "HealthCheckBegin");
            public static readonly EventId HealthCheckEnd = new EventId(103, "HealthCheckEnd");
            public static readonly EventId HealthCheckError = new EventId(104, "HealthCheckError");
            public static readonly EventId HealthCheckData = new EventId(105, "HealthCheckData");
        }

        private static class Log
        {
            private static readonly Action<ILogger, Exception> _healthCheckProcessingBegin = LoggerMessage.Define(
                LogLevel.Debug,
                EventIds.HealthCheckProcessingBegin,
                "Running health checks");

            private static readonly Action<ILogger, double, HealthStatus, Exception> _healthCheckProcessingEnd = LoggerMessage.Define<double, HealthStatus>(
                LogLevel.Debug,
                EventIds.HealthCheckProcessingEnd,
                "Health check processing completed after {ElapsedMilliseconds}ms with combined status {HealthStatus}");

            private static readonly Action<ILogger, string, Exception> _healthCheckBegin = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.HealthCheckBegin,
                "Running health check {HealthCheckName}");

            // These are separate so they can have different log levels
            private static readonly string HealthCheckEndText = "Health check {HealthCheckName} completed after {ElapsedMilliseconds}ms with status {HealthStatus} and '{HealthCheckDescription}'";

            private static readonly Action<ILogger, string, double, HealthStatus, string, Exception> _healthCheckEndHealthy = LoggerMessage.Define<string, double, HealthStatus, string>(
                LogLevel.Debug,
                EventIds.HealthCheckEnd,
                HealthCheckEndText);

            private static readonly Action<ILogger, string, double, HealthStatus, string, Exception> _healthCheckEndDegraded = LoggerMessage.Define<string, double, HealthStatus, string>(
                LogLevel.Warning,
                EventIds.HealthCheckEnd,
                HealthCheckEndText);

            private static readonly Action<ILogger, string, double, HealthStatus, string, Exception> _healthCheckEndUnhealthy = LoggerMessage.Define<string, double, HealthStatus, string>(
                LogLevel.Error,
                EventIds.HealthCheckEnd,
                HealthCheckEndText);

            private static readonly Action<ILogger, string, double, HealthStatus, string, Exception> _healthCheckEndFailed = LoggerMessage.Define<string, double, HealthStatus, string>(
                LogLevel.Error,
                EventIds.HealthCheckEnd,
                HealthCheckEndText);

            private static readonly Action<ILogger, string, double, Exception> _healthCheckError = LoggerMessage.Define<string, double>(
                LogLevel.Error,
                EventIds.HealthCheckError,
                "Health check {HealthCheckName} threw an unhandled exception after {ElapsedMilliseconds}ms");

            public static void HealthCheckProcessingBegin(ILogger logger)
            {
                _healthCheckProcessingBegin(logger, null);
            }

            public static void HealthCheckProcessingEnd(ILogger logger, HealthStatus status, TimeSpan duration)
            {
                _healthCheckProcessingEnd(logger, duration.TotalMilliseconds, status, null);
            }

            public static void HealthCheckBegin(ILogger logger, HealthCheckRegistration registration)
            {
                _healthCheckBegin(logger, registration.Name, null);
            }

            public static void HealthCheckEnd(ILogger logger, HealthCheckRegistration registration, HealthReportEntry entry, TimeSpan duration)
            {
                switch (entry.Status)
                {
                    case HealthStatus.Healthy:
                        _healthCheckEndHealthy(logger, registration.Name, duration.TotalMilliseconds, entry.Status, entry.Description, null);
                        break;

                    case HealthStatus.Degraded:
                        _healthCheckEndDegraded(logger, registration.Name, duration.TotalMilliseconds, entry.Status, entry.Description, null);
                        break;

                    case HealthStatus.Unhealthy:
                        _healthCheckEndUnhealthy(logger, registration.Name, duration.TotalMilliseconds, entry.Status, entry.Description, null);
                        break;
                }
            }

            public static void HealthCheckError(ILogger logger, HealthCheckRegistration registration, Exception exception, TimeSpan duration)
            {
                _healthCheckError(logger, registration.Name, duration.TotalMilliseconds, exception);
            }

            public static void HealthCheckData(ILogger logger, HealthCheckRegistration registration, HealthReportEntry entry)
            {
                if (entry.Data.Count > 0 && logger.IsEnabled(LogLevel.Debug))
                {
                    logger.Log(
                        LogLevel.Debug,
                        EventIds.HealthCheckData,
                        new HealthCheckDataLogValue(registration.Name, entry.Data),
                        null,
                        (state, ex) => state.ToString());
                }
            }
        }

        internal class HealthCheckDataLogValue : IReadOnlyList<KeyValuePair<string, object>>
        {
            private readonly string _name;
            private readonly List<KeyValuePair<string, object>> _values;

            private string _formatted;

            public HealthCheckDataLogValue(string name, IReadOnlyDictionary<string, object> values)
            {
                _name = name;
                _values = values.ToList();

                // We add the name as a kvp so that you can filter by health check name in the logs.
                // This is the same parameter name used in the other logs.
                _values.Add(new KeyValuePair<string, object>("HealthCheckName", name));
            }

            public KeyValuePair<string, object> this[int index]
            {
                get
                {
                    if (index < 0 || index >= Count)
                    {
                        throw new IndexOutOfRangeException(nameof(index));
                    }

                    return _values[index];
                }
            }

            public int Count => _values.Count;

            public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
            {
                return _values.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return _values.GetEnumerator();
            }

            public override string ToString()
            {
                if (_formatted == null)
                {
                    var builder = new StringBuilder();
                    builder.AppendLine($"Health check data for {_name}:");

                    var values = _values;
                    for (var i = 0; i < values.Count; i++)
                    {
                        var kvp = values[i];
                        builder.Append("    ");
                        builder.Append(kvp.Key);
                        builder.Append(": ");

                        builder.AppendLine(kvp.Value?.ToString());
                    }

                    _formatted = builder.ToString();
                }

                return _formatted;
            }
        }
    }

    internal struct ValueStopwatch
    {
        private static readonly double TimestampToTicks = TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency;
        private long value;

        /// <summary>
        /// Starts a new instance.
        /// </summary>
        /// <returns>A new, running stopwatch.</returns>
        public static ValueStopwatch StartNew() => new ValueStopwatch(Stopwatch.GetTimestamp());

        private ValueStopwatch(long timestamp)
        {
            this.value = timestamp;
        }

        /// <summary>
        /// Returns true if this instance is running or false otherwise.
        /// </summary>
        public bool IsRunning => this.value > 0;

        /// <summary>
        /// Returns the elapsed time.
        /// </summary>
        public TimeSpan Elapsed => TimeSpan.FromTicks(this.ElapsedTicks);

        /// <summary>
        /// Returns the elapsed ticks.
        /// </summary>
        public long ElapsedTicks
        {
            get
            {
                // A positive timestamp value indicates the start time of a running stopwatch,
                // a negative value indicates the negative total duration of a stopped stopwatch.
                var timestamp = this.value;

                long delta;
                if (this.IsRunning)
                {
                    // The stopwatch is still running.
                    var start = timestamp;
                    var end = Stopwatch.GetTimestamp();
                    delta = end - start;
                }
                else
                {
                    // The stopwatch has been stopped.
                    delta = -timestamp;
                }

                return (long)(delta * TimestampToTicks);
            }
        }

        /// <summary>
        /// Gets the raw counter value for this instance.
        /// </summary>
        /// <remarks> 
        /// A positive timestamp value indicates the start time of a running stopwatch,
        /// a negative value indicates the negative total duration of a stopped stopwatch.
        /// </remarks>
        /// <returns>The raw counter value.</returns>
        public long GetRawTimestamp() => this.value;

        /// <summary>
        /// Starts the stopwatch.
        /// </summary>
        public void Start()
        {
            var timestamp = this.value;

            // If already started, do nothing.
            if (this.IsRunning) return;

            // Stopwatch is stopped, therefore value is zero or negative.
            // Add the negative value to the current timestamp to start the stopwatch again.
            var newValue = Stopwatch.GetTimestamp() + timestamp;
            if (newValue == 0) newValue = 1;
            this.value = newValue;
        }

        /// <summary>
        /// Restarts this stopwatch, beginning from zero time elapsed.
        /// </summary>
        public void Restart() => this.value = Stopwatch.GetTimestamp();

        /// <summary>
        /// Stops this stopwatch.
        /// </summary>
        public void Stop()
        {
            var timestamp = this.value;

            // If already stopped, do nothing.
            if (!this.IsRunning) return;

            var end = Stopwatch.GetTimestamp();
            var delta = end - timestamp;

            this.value = -delta;
        }
    }
}
