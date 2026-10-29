using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Coflnet.Core.Security;

namespace Coflnet.Cassandra;

public class CustomRootCaCertificateValidator
{
    private readonly X509Certificate2Collection trustedRoots;

    public CustomRootCaCertificateValidator(X509Certificate2 trustedRootCertificateAuthority)
    {
        trustedRoots = new X509Certificate2Collection(trustedRootCertificateAuthority);
    }

    public bool Validate(X509Certificate cert, X509Chain chain, SslPolicyErrors errors) =>
        (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0 &&
        PemTlsOptions.ValidateChain(cert, chain, trustedRoots);
}
