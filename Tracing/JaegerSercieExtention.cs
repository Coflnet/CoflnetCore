using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using OpenTelemetry;
using System.Diagnostics;
using OpenTelemetry.Resources;
using Confluent.Kafka.Extensions.OpenTelemetry;
using System.Globalization;
using System.Collections.Concurrent;

namespace Coflnet.Core.Tracing;
public static class JaegerSercieExtention
{
    public static void AddJaeger(this IServiceCollection services, IConfiguration config, double samplingRate = 0.03, double lowerBoundInSeconds = 60)
    {
        var batchOptions = new BatchExportProcessorOptions<Activity>
        {
            MaxQueueSize = 2000,
            MaxExportBatchSize = 1000,
            ExporterTimeoutMilliseconds = 10000,
            ScheduledDelayMilliseconds = 2000
        };
        services.AddOpenTelemetry()
        .WithTracing((builder) =>
        {
            builder
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSqlClientInstrumentation()
            .AddConfluentKafkaInstrumentation()
            .SetSampler(new RationOrTimeBasedSampler(samplingRate, lowerBoundInSeconds))
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(config["OTEL_SERVICE_NAME"] ?? config["JAEGER_SERVICE_NAME"] ?? "default"));
            if (config["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] != null)
            {
                builder.AddOtlpExporter(c => c.BatchExportProcessorOptions = batchOptions);
            }
            else
                builder
                .AddJaegerExporter(j =>
                {
                    j.Protocol = JaegerExportProtocol.UdpCompactThrift;
                    j.AgentHost = config["JAEGER_AGENT_HOST"];
                    j.BatchExportProcessorOptions = batchOptions;
                });
        });
    }


    // <copyright file="TraceIdRatioBasedSampler.cs" company="OpenTelemetry Authors">
    // Copyright The OpenTelemetry Authors
    //
    // Licensed under the Apache License, Version 2.0 (the "License");
    // you may not use this file except in compliance with the License.
    // You may obtain a copy of the License at
    //
    //     http://www.apache.org/licenses/LICENSE-2.0
    //
    // Unless required by applicable law or agreed to in writing, software
    // distributed under the License is distributed on an "AS IS" BASIS,
    // WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    // See the License for the specific language governing permissions and
    // limitations under the License.
    // </copyright>
    public sealed class RationOrTimeBasedSampler
        : Sampler
    {
        private readonly long idUpperBound;
        private readonly double probability;
        private ConcurrentDictionary<string, DateTime> lastSampled = new ConcurrentDictionary<string, DateTime>();

        /// <summary>
        /// Initializes a new instance of the <see cref="RationOrTimeBasedSampler"/> class.
        /// </summary>
        /// <param name="probability">The desired probability of sampling. This must be between 0.0 and 1.0.
        /// Higher the value, higher is the probability of a given Activity to be sampled in.
        /// </param>
        /// <param name="lowerBoundInSeconds">The time until sampling will forward all matches in seconds.</param>
        public RationOrTimeBasedSampler(double probability, double lowerBoundInSeconds = 30)
        {
            this.probability = probability;

            // The expected description is like TraceIdRatioBasedSampler{0.000100}
            this.Description = "TraceIdRatioBasedSampler{" + this.probability.ToString("F6", CultureInfo.InvariantCulture) + "}";

            // Special case the limits, to avoid any possible issues with lack of precision across
            // double/long boundaries. For probability == 0.0, we use Long.MIN_VALUE as this guarantees
            // that we will never sample a trace, even in the case where the id == Long.MIN_VALUE, since
            // Math.Abs(Long.MIN_VALUE) == Long.MIN_VALUE.
            if (this.probability == 0.0)
            {
                this.idUpperBound = long.MinValue;
            }
            else if (this.probability == 1.0)
            {
                this.idUpperBound = long.MaxValue;
            }
            else
            {
                this.idUpperBound = (long)(probability * long.MaxValue);
            }
            Console.WriteLine("started sampler with " + this.probability + " " + this.idUpperBound);
        }

        /// <inheritdoc />
        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        {
            // Always sample if we are within probability range. This is true even for child activities (that
            // may have had a different sampling decision made) to allow for different sampling policies,
            // and dynamic increases to sampling probabilities for debugging purposes.
            // Note use of '<' for comparison. This ensures that we never sample for probability == 0.0,
            // while allowing for a (very) small chance of *not* sampling if the id == Long.MAX_VALUE.
            // This is considered a reasonable trade-off for the simplicity/performance requirements (this
            // code is executed in-line for every Activity creation).
            Span<byte> traceIdBytes = stackalloc byte[16];
            if (samplingParameters.Name == "error")
                return new SamplingResult(SamplingDecision.RecordAndSample);
            if (!lastSampled.TryGetValue(samplingParameters.Name, out DateTime lastSampledTime) || lastSampledTime.AddSeconds(10) < DateTime.Now)
            {
                lastSampled.AddOrUpdate(samplingParameters.Name, DateTime.Now, (key, oldValue) => DateTime.Now);
                return new SamplingResult(SamplingDecision.RecordAndSample);
            }

            samplingParameters.TraceId.CopyTo(traceIdBytes);
            return new SamplingResult(Math.Abs(GetLowerLong(traceIdBytes)) < this.idUpperBound);
        }

        private static long GetLowerLong(ReadOnlySpan<byte> bytes)
        {
            long result = 0;
            for (var i = 0; i < 8; i++)
            {
                result <<= 8;
#pragma warning disable CS0675 // Bitwise-or operator used on a sign-extended operand
                result |= bytes[i] & 0xff;
#pragma warning restore CS0675 // Bitwise-or operator used on a sign-extended operand
            }

            return result;
        }
    }

    public static Activity? Log(this Activity? activity, string message, int maxcontextLength = 6_000)
    {
        return activity?.AddEvent(new ActivityEvent("log", DateTimeOffset.Now, new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("message", message.Truncate(maxcontextLength)) })));
    }
}
