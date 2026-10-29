using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Cassandra;
using Coflnet.Core.Security;

namespace Coflnet.Cassandra;

public static class PemTlsExtensions
{
    /// <summary>Refreshes PEM credentials for each new connection; existing connections continue uninterrupted.</summary>
    public static SSLOptions CreateCassandraSslOptions(this PemTlsOptions credentials, string? serverName = null)
    {
        var options = new SSLOptions(SslProtocols.Tls12 | SslProtocols.Tls13, false,
            credentials.ValidateServerCertificate);
        options.SetRemoteCertValidationCallback((sender, certificate, chain, errors) =>
            options.CertificateCollection.Count > 0 && credentials.ValidateServerCertificate(sender, certificate, chain, errors));
        credentials.RegisterClientIntermediates();
        options.SetCertificateCollection(new X509CertificateCollection { credentials.GetCertificate() });
        // CassandraCSharpDriver 3.23 calls this for each connection immediately before reading
        // CertificateCollection in TcpSocket.ConnectSsl. Replace the collection, never mutate it.
        options.SetHostNameResolver(address =>
        {
            try
            {
                credentials.RegisterClientIntermediates();
                options.SetCertificateCollection(new X509CertificateCollection { credentials.GetCertificate() });
            }
            catch
            {
                // The driver catches resolver exceptions and falls back to the IP. Never retain an old identity.
                options.SetCertificateCollection(new X509CertificateCollection());
                throw;
            }
            return string.IsNullOrWhiteSpace(serverName) ? address.ToString() : serverName;
        });
        return options;
    }
}
