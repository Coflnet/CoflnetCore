using Microsoft.Extensions.DependencyInjection;

namespace Coflnet.Kafka;

public static class KafkaServiceExtensions
{
    public static void AddKafka(this IServiceCollection services)
    {
        services.AddSingleton<SerializerFactory>();
        services.AddSingleton<KafkaCreator>();
        services.AddSingleton<KafkaConsumer>();
    }
}
