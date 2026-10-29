using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Core.Security;

/// <summary>File-backed mutual TLS credentials. Keep alive for the lifetime of the connections using them.</summary>
public sealed class PemTlsOptions : IDisposable
{
    private readonly string caCertPath;
    private readonly string certPath;
    private readonly string keyPath;
    private readonly bool serverCertificate;
    private readonly object gate = new();
    private readonly List<SslStreamCertificateContext> contexts = new();
    private byte[]? certificateHash;
    private bool disposed;

    public PemTlsOptions(string caCertPath, string certPath, string keyPath, bool serverCertificate = false)
    {
        this.caCertPath = caCertPath;
        this.certPath = certPath;
        this.keyPath = keyPath;
        this.serverCertificate = serverCertificate;
    }

    public static PemTlsOptions FromConfiguration(IConfiguration section, bool serverCertificate = false) => new(
        Required(section, "CA_CERT_PATH"), Required(section, "CERT_PATH"), Required(section, "KEY_PATH"), serverCertificate);

    private static string Required(IConfiguration section, string key) =>
        !string.IsNullOrWhiteSpace(section[key]) ? section[key]! :
        throw new InvalidOperationException($"TLS:{key} must be set for PEM mutual TLS.");

    /// <summary>Loads rotated PEM files on new handshakes. Superseded certificates stay alive for concurrent handshakes.</summary>
    public X509Certificate2 GetCertificate()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var certPem = File.ReadAllText(certPath);
            var keyPem = File.ReadAllText(keyPath);
            var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(certPem + keyPem));
            if (certificateHash is not null && hash.AsSpan().SequenceEqual(certificateHash))
            {
                CheckCertificatePurpose(contexts[^1].TargetCertificate);
                return contexts[^1].TargetCertificate;
            }
            var certificate = X509Certificate2.CreateFromPem(certPem, keyPem);
            try
            {
                CheckCertificatePurpose(certificate);
                var chain = new X509Certificate2Collection();
                try
                {
                    chain.ImportFromPem(certPem);
                    contexts.Add(SslStreamCertificateContext.Create(certificate, chain, offline: true));
                }
                finally
                {
                    foreach (var item in chain) item.Dispose();
                }
            }
            catch { certificate.Dispose(); throw; }
            certificateHash = hash;
            return certificate;
        }
    }

    /// <summary>Returns the current leaf and its transmitted intermediate chain, without changing platform trust.</summary>
    public SslStreamCertificateContext GetCertificateContext()
    {
        lock (gate)
        {
            GetCertificate();
            return contexts[^1];
        }
    }

    /// <summary>
    /// Registers validated public intermediates in the workload user's CA store for older TLS APIs
    /// that accept only a leaf certificate. Never adds root certificates or changes machine trust.
    /// </summary>
    public void RegisterClientIntermediates()
    {
        lock (gate)
        {
            var context = GetCertificateContext();
            var intermediates = new X509Certificate2Collection(context.IntermediateCertificates
                .Where(c => !c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData) &&
                    c.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority)).ToArray());
            if (intermediates.Count == 0) return;
            var roots = new X509Certificate2Collection();
            try
            {
                roots.ImportFromPemFile(caCertPath);
                if (!ValidateChain(context.TargetCertificate, null, roots, clientCertificate: true, intermediates))
                    throw new AuthenticationException("The client certificate chain must validate against the configured roots before registering intermediates.");
                using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                var existing = store.Certificates;
                try
                {
                    foreach (var intermediate in intermediates)
                        if (!existing.Any(c => c.Thumbprint == intermediate.Thumbprint))
                            store.Add(intermediate);
                }
                finally
                {
                    foreach (var certificate in existing) certificate.Dispose();
                }
            }
            finally
            {
                foreach (var root in roots) root.Dispose();
            }
        }
    }

    /// <summary>Creates fresh TLS options for clients that accept a per-connection options provider.</summary>
    public SslClientAuthenticationOptions CreateClientAuthenticationOptions(string targetHost) => new()
    {
        TargetHost = targetHost,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        RemoteCertificateValidationCallback = ValidateServerCertificate,
        ClientCertificateContext = GetCertificateContext()
    };

    private void CheckCertificatePurpose(X509Certificate2 certificate)
    {
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (!certificate.HasPrivateKey || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
            DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime() ||
            (usages is not null && !usages.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == (serverCertificate ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") || o.Value == "2.5.29.37.0")))
            throw new AuthenticationException("The PEM certificate must have a private key, the configured TLS purpose and current validity.");
    }

    /// <summary>Validates against only the current PEM bundle, preserving the TLS stack's hostname check.</summary>
    public bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        ValidateRemoteCertificate(certificate, chain, errors, clientCertificate: false);

    /// <summary>Validates an authenticated client against the current roots and client EKU, without a DNS check.</summary>
    public bool ValidateClientCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        ValidateRemoteCertificate(certificate, chain, errors, clientCertificate: true);

    private bool ValidateRemoteCertificate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, bool clientCertificate)
    {
        var allowedErrors = SslPolicyErrors.RemoteCertificateChainErrors |
            (clientCertificate ? SslPolicyErrors.RemoteCertificateNameMismatch : SslPolicyErrors.None);
        if (certificate is null || (errors & ~allowedErrors) != 0)
            return false;
        var roots = new X509Certificate2Collection();
        try
        {
            GetCertificate();
            roots.ImportFromPemFile(caCertPath);
            return ValidateChain(certificate, chain, roots, clientCertificate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or AuthenticationException)
        {
            return false;
        }
        finally
        {
            foreach (var root in roots)
                root.Dispose();
        }
    }

    internal static bool ValidateChain(X509Certificate certificate, X509Chain? peerChain, X509Certificate2Collection roots, bool clientCertificate = false, X509Certificate2Collection? intermediates = null)
    {
        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(clientCertificate ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1"));
        if (intermediates is not null) chain.ChainPolicy.ExtraStore.AddRange(intermediates);
        if (peerChain is not null)
            foreach (var element in peerChain.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        return roots.Count > 0 && chain.Build(leaf);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var context in contexts)
            {
                foreach (var intermediate in context.IntermediateCertificates)
                    intermediate.Dispose();
                context.TargetCertificate.Dispose();
            }
            contexts.Clear();
        }
    }
}
