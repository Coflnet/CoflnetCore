using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Cassandra;
using Coflnet.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Coflnet.Cassandra;

public static class CassandraServiceExtensions
{
    public static void AddCassandra(this IServiceCollection services)
    {
        services.AddSingleton(p =>
        {
            return ConstructSessionFromSettingsSection(p, "CASSANDRA");
        });
        services.AddSingleton(p =>
        {
            return new OldSession(ConstructSessionFromSettingsSection(p, "OLD_CASSANDRA"));
        });
    }

    private static ISession ConstructSessionFromSettingsSection(IServiceProvider p, string sectionName)
    {
        var Configuration = p.GetRequiredService<IConfiguration>();
        var section = Configuration.GetSection(sectionName);
        Console.WriteLine($"Connecting to {sectionName}...");
        var builder = Cluster.Builder().AddContactPoints(section["HOSTS"]?.Split(","))
            .WithLoadBalancingPolicy(new TokenAwarePolicy(new DCAwareRoundRobinPolicy()))
            .WithCredentials(section["USER"], section["PASSWORD"])
            .WithDefaultKeyspace(section["KEYSPACE"]);

        Console.WriteLine("Connecting to servers " + section["HOSTS"]);
        Console.WriteLine("Using keyspace " + section["KEYSPACE"]);
        Console.WriteLine("Using replication class " + section["REPLICATION_CLASS"]);
        Console.WriteLine("Using replication factor " + section["REPLICATION_FACTOR"]);
        Console.WriteLine("Using user " + section["USER"]);
        Console.WriteLine("Using password " + section["PASSWORD"]?.Truncate(2) + "...");
        var certificatePaths = section["X509Certificate_PATHS"];
        Console.WriteLine("Using certificate paths " + certificatePaths);
        Console.WriteLine("Using certificate password " + section["X509Certificate_PASSWORD"]?.Truncate(2) + "...");
        var validationCertificatePath = section["X509Certificate_VALIDATION_PATH"];
        if (!string.IsNullOrEmpty(certificatePaths))
        {
            var password = section["X509Certificate_PASSWORD"]
                ?? throw new InvalidOperationException("CASSANDRA:X509Certificate_PASSWORD must be set if CASSANDRA:X509Certificate_PATHS is set.");
            CustomRootCaCertificateValidator? certificateValidator = null;
            if (!string.IsNullOrEmpty(validationCertificatePath))
                certificateValidator = new CustomRootCaCertificateValidator(new X509Certificate2(validationCertificatePath, password));
            var sslOptions = new SSLOptions(
                // TLSv1.2 is required as of October 9, 2019.
                // See: https://www.instaclustr.com/removing-support-for-outdated-encryption-mechanisms/
                SslProtocols.Tls12,
                false,
                // Custom validator avoids need to trust the CA system-wide.
                (sender, certificate, chain, errors) => certificate != null && chain != null && (certificateValidator?.Validate(certificate, chain, errors) ?? true)
            ).SetCertificateCollection(new(certificatePaths.Split(',').Select(p => new X509Certificate2(p, password)).ToArray()));
            builder.WithSSL(sslOptions);
        }
        var cluster = builder.Build();
        var session = cluster.Connect(null);
        var defaultKeyspace = cluster.Configuration.ClientOptions.DefaultKeyspace;
        try
        {
            session.CreateKeyspaceIfNotExists(defaultKeyspace, new Dictionary<string, string>()
                    {
                        {"class", section["REPLICATION_CLASS"] ?? "NetworkTopologyStrategy"},
                        {"replication_factor", section["REPLICATION_FACTOR"] ?? "3"}
                    });
            session.ChangeKeyspace(defaultKeyspace);
            Console.WriteLine("Created cassandra keyspace");
        }
        catch (UnauthorizedException)
        {
            Console.WriteLine("User unauthorized to create keyspace, trying to switch it");
        }
        finally
        {
            session.ChangeKeyspace(defaultKeyspace);
        }
        return session;
    }
}
/// <summary>
/// For migrating to another database this DI container wraps the secondary (old) <see cref="ISession"/>
/// </summary>
public class OldSession
{
    public ISession Session { get; }

    public OldSession(ISession session)
    {
        Session = session;
    }
}