using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Coflnet.Core.ErrorHandling;

public class ErrorHandler
{
    static string prefix = "api";
    static Prometheus.Counter errorCount = Prometheus.Metrics.CreateCounter($"{prefix}_api_error", "Counts the amount of error responses handed out");
    static Prometheus.Counter badRequestCount = Prometheus.Metrics.CreateCounter($"{prefix}_api_bad_request", "Counts the responses for invalid requests");
    static JsonSerializerOptions converter = new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static void Add(IApplicationBuilder errorApp, string serviceName)
    {
        var logger = errorApp.ApplicationServices.GetRequiredService<ILogger<ErrorHandler>>();
        Add(logger, errorApp, serviceName);
    }
    public static void Add(ILogger logger, IApplicationBuilder errorApp, string serviceName)
    {
        prefix = serviceName.Replace("-", "_");
        errorApp.Run(async context =>
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "text/json";

            var exceptionHandlerPathFeature =
                context.Features.Get<IExceptionHandlerPathFeature>();

            if (exceptionHandlerPathFeature?.Error is ApiException ex)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await context.Response.WriteAsync(
                                JsonSerializer.Serialize(new ErrorResponse() { Slug = ex.Slug, Message = ex.Message }, converter));
                badRequestCount.Inc();
                return;
            }
            var source = context.RequestServices.GetService<ActivitySource>();
            using var activity = source?.StartActivity("error", ActivityKind.Producer);
            if (activity == null)
            {
                logger.LogError("Could not start activity");
                return;
            }
            activity.AddTag("host", System.Net.Dns.GetHostName());
            activity.AddEvent(new ActivityEvent("error", default, new ActivityTagsCollection(new KeyValuePair<string, object?>[] {
                        new ("error", exceptionHandlerPathFeature?.Error?.Message),
                        new ("stack", exceptionHandlerPathFeature?.Error?.StackTrace),
                        new ("path", context.Request.Path),
                        new ("query", context.Request.QueryString) })));
            var traceId = System.Net.Dns.GetHostName().Replace(serviceName, "").Trim('-') + "." + activity.Context.TraceId;
            context.Response.Headers.Add("X-Trace-Id", traceId);
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new ErrorResponse
                {
                    Slug = "internal_error",
                    Message = $"An unexpected internal error occured. Please check that your request is valid. If it is please report he error and include reference '{activity.Context.TraceId}'.",
                    Trace = traceId
                }, converter));
            errorCount.Inc();
        });
    }
}
