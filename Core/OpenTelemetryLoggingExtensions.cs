using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace Coflnet.Core;

/// <summary>
/// Shared OpenTelemetry logging configuration for all Coflnet services.
/// Bridges <see cref="ILogger"/> to the OTLP exporter so that application logs land in
/// Loki (OTLP-native) and can be correlated with traces exported to Jaeger.
/// </summary>
public static class OpenTelemetryLoggingExtensions
{
    /// <summary>
    /// Configures the logging pipeline to export logs via OTLP (HttpProtobuf) with
    /// resource attributes for service identification and Kubernetes context.
    ///
    /// In local development (<c>DEV_LOGGING=true</c>, or when no logs endpoint is configured)
    /// a plain console logger is used instead so nothing is silently dropped.
    /// </summary>
    /// <param name="builder">The <see cref="ILoggingBuilder"/> from the host builder.</param>
    /// <param name="configuration">Application configuration for reading OTLP endpoints and flags.</param>
    /// <param name="applicationName">The service name (used as the <c>service.name</c> resource attribute).</param>
    public static void AddOpenTelemetryLogging(this ILoggingBuilder builder, IConfiguration configuration, string applicationName)
    {
        const string devLoggingKey = "DEV_LOGGING";
        const string logLevelPath = "Logging:LogLevel:Default";

        var consoleLogging = configuration.GetValue<bool?>(devLoggingKey) ?? false;

        // Parse configured minimum log level, default to Debug.
        var configLogLevel = configuration.GetValue<string>(logLevelPath);
        if (!Enum.TryParse<LogLevel>(configLogLevel, true, out var minLogLevel))
            minLogLevel = LogLevel.Debug;

        // Clear default providers so every log flows through a single pipeline.
        builder.ClearProviders();

        builder
            .AddFilter(null, minLogLevel)
            .AddFilter("Microsoft", LogLevel.Warning);

        // Resolve the logs endpoint. The OTLP HttpProtobuf exporter requires the FULL URL
        // including the signal path when the endpoint is set programmatically, e.g.
        // http://loki-scalable-write.loki:3100/otlp/v1/logs (Loki 3.x native OTLP receiver).
        // Logs go to Loki; the base OTLP endpoint is only used as a fallback. The traces
        // endpoint is intentionally NOT used here so logs never get sent to Jaeger.
        var logsEndpoint = configuration["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"]
                        ?? configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        // In local development, or when no logs endpoint is configured, just log to console.
        if (consoleLogging || string.IsNullOrEmpty(logsEndpoint))
        {
            builder.AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss ");
            return;
        }

        var resourceBuilder = ResourceBuilder
            .CreateDefault()
            .AddService(serviceName: applicationName)
            .AddTelemetrySdk()
            .AddAttributes(GetClusterAttributes());

        builder.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resourceBuilder);
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.ParseStateValues = true;

            logging.AddOtlpExporter(options =>
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.ExportProcessorType = ExportProcessorType.Batch;
                options.Endpoint = new Uri(logsEndpoint);
            });
        });
    }

    /// <summary>
    /// Returns cluster-wide resource attributes applied to every log record so logs can be
    /// filtered by pod, region, etc. in the observability backend. Values are read from the
    /// downward-API environment variables <c>OTEL_POD_NAME</c> and <c>LOCATION</c>.
    /// </summary>
    private static Dictionary<string, object> GetClusterAttributes()
    {
        var podName = Environment.GetEnvironmentVariable("OTEL_POD_NAME");
        var region = Environment.GetEnvironmentVariable("LOCATION");
        var result = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(podName))
            result.Add("k8s.pod.name", podName);

        if (!string.IsNullOrEmpty(region))
            result.Add("cloud.region", region);

        return result;
    }
}
