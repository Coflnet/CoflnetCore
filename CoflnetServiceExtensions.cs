using Coflnet.Core.Tracing;
using Coflnet.Kafka;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using Microsoft.Extensions.Logging;
using Coflnet.Core.ErrorHandling;
using Coflnet.Cassandra;

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
    }

    /// <summary>
    /// Register relevant endpints, like metrics
    /// Put this after .UseRouting and before .UseEndpoints
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseCoflnetCore(this IApplicationBuilder builder)
    {
        builder.UseExceptionHandler(errorApp =>
        {
            var config = errorApp.ApplicationServices.GetRequiredService<IConfiguration>();
            var logger = errorApp.ApplicationServices.GetRequiredService<ILogger<ErrorHandler>>();
            var serviceName = config["SERVICE_NAME"] ?? "default";
            ErrorHandler.Add(logger, errorApp, serviceName);
        });
        builder.UseMetricServer();
        return builder;
    }
}
