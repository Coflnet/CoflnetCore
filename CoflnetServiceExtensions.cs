using Coflnet.Core.Tracing;
using Coflnet.Kafka;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using Microsoft.Extensions.Logging;
using Coflnet.Core.ErrorHandling;
using Coflnet.Cassandra;
using Coflnet.OpenApi;

namespace Coflnet.Core;

public static class CoflnetServiceExtensions
{
    /// <summary>
    /// Registers default coflnet services
    /// </summary>
    /// <param name="services"></param>
    public static void AddCoflnetCore(this IServiceCollection services)
    {
        var sp = services.BuildServiceProvider();
        var config = sp.GetRequiredService<IConfiguration>();
        services.AddJaeger(config);
        services.AddKafka();
        services.AddCassandra();
        services.AddOpenApi(config["OTEL_SERVICE_NAME"] ?? "Api");
    }

    /// <summary>
    /// Register relevant endpints, like metrics
    /// Put this after .UseRouting and before .UseEndpoints
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseCoflnetCore(this IApplicationBuilder builder)
    {
        var config = builder.ApplicationServices.GetRequiredService<IConfiguration>();
        var serviceName = config["SERVICE_NAME"] ?? config["OTEL_SERVICE_NAME"] ?? "default";
        builder.UseExceptionHandler(errorApp =>
        {
            var config = errorApp.ApplicationServices.GetRequiredService<IConfiguration>();
            var logger = errorApp.ApplicationServices.GetRequiredService<ILogger<ErrorHandler>>();
            ErrorHandler.Add(logger, errorApp, serviceName);
        });
        builder.UseMetricServer();
        builder.UseOpenApi(serviceName);
        return builder;
    }
}
