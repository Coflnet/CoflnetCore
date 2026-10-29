using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cassandra;
using AuthenticationException = System.Security.Authentication.AuthenticationException;
using Coflnet.Cassandra;
using Coflnet.Core.Security;
using NUnit.Framework;

namespace Coflnet.Core.Tests;

[NonParallelizable]
public class PemTlsTests
{
    private string directory = null!;
    private string homeDirectory = null!;
    private X509Certificate2 root = null!;
    private X509Certificate2 nextRoot = null!;
    private X509Certificate2 intermediate = null!;
    private X509Certificate2 nextIntermediate = null!;
    private X509Certificate2 serverIntermediate = null!;
    private X509Certificate2 server = null!;
    private X509Certificate2 client = null!;
    private PemTlsOptions credentials = null!;

    [OneTimeSetUp]
    public void RequireProcessIsolatedUserCertificateStore()
    {
        homeDirectory = Environment.GetEnvironmentVariable("COFLNET_TEST_HOME")
            ?? throw new InvalidOperationException("Run dotnet test through the project so private test-host HOME settings are applied.");
        Assert.That(Environment.GetEnvironmentVariable("HOME"), Is.EqualTo(homeDirectory));
    }

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "coflnet-tls-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        root = CreateRoot("old");
        nextRoot = CreateRoot("next");
        intermediate = Issue(root, "client issuing", "", authority: true);
        nextIntermediate = Issue(nextRoot, "next client issuing", "", authority: true);
        serverIntermediate = Issue(root, "server issuing", "", authority: true);
        server = Issue(serverIntermediate, "server", "1.3.6.1.5.5.7.3.1");
        client = Issue(intermediate, "client", "1.3.6.1.5.5.7.3.2");
        File.WriteAllText(Path.Combine(directory, "ca.pem"), root.ExportCertificatePem());
        WriteClient(client);
        credentials = new PemTlsOptions(Path.Combine(directory, "ca.pem"), Path.Combine(directory, "cert.pem"), Path.Combine(directory, "key.pem"));
    }

    [TearDown]
    public void TearDown()
    {
        credentials.Dispose();
        client.Dispose();
        server.Dispose();
        serverIntermediate.Dispose();
        intermediate.Dispose();
        nextIntermediate.Dispose();
        nextRoot.Dispose();
        root.Dispose();
        Directory.Delete(directory, true);
    }

    [Test]
    public void TrustIsExclusiveAndSupportsOverlapAndRemoval()
    {
        using var renewed = Issue(nextRoot, "server", "1.3.6.1.5.5.7.3.1");
        Assert.That(Validate(server), Is.True);
        Assert.That(Validate(renewed), Is.False);
        // Even SslPolicyErrors.None must not bypass the configured exclusive trust bundle.
        Assert.That(credentials.ValidateServerCertificate(this, renewed, null, SslPolicyErrors.None), Is.False);
        File.WriteAllText(Path.Combine(directory, "ca.pem"), root.ExportCertificatePem() + "\n" + nextRoot.ExportCertificatePem());
        Assert.That(Validate(server) && Validate(renewed), Is.True);
        File.WriteAllText(Path.Combine(directory, "ca.pem"), nextRoot.ExportCertificatePem());
        Assert.That(Validate(server), Is.False);
        Assert.That(Validate(renewed), Is.True);
    }

    [Test]
    public void RejectsNameMismatchExpiredWrongEkuAndBrokenBundle()
    {
        Assert.That(credentials.ValidateServerCertificate(this, server, null, SslPolicyErrors.RemoteCertificateNameMismatch), Is.False);
        using var expired = Issue(root, "server", "1.3.6.1.5.5.7.3.1", expired: true);
        Assert.That(Validate(expired), Is.False);
        Assert.That(Validate(client), Is.False);
        File.WriteAllText(Path.Combine(directory, "ca.pem"), "broken bundle");
        Assert.That(Validate(server), Is.False);
    }

    [Test]
    public void ClientReloadFailsClosedForMismatchExpiryAndEku()
    {
        Assert.That(credentials.GetCertificate().Thumbprint, Is.EqualTo(client.Thumbprint));
        using var renewed = Issue(nextIntermediate, "client", "1.3.6.1.5.5.7.3.2");
        WriteClient(renewed);
        Assert.That(credentials.GetCertificate().Thumbprint, Is.EqualTo(renewed.Thumbprint));
        File.WriteAllText(Path.Combine(directory, "key.pem"), client.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
        Assert.Throws<CryptographicException>(() => credentials.GetCertificate());
        Assert.That(Validate(server), Is.False);
        WriteClient(server);
        Assert.Throws<AuthenticationException>(() => credentials.GetCertificate());
        using var expired = Issue(root, "client", "1.3.6.1.5.5.7.3.2", expired: true);
        WriteClient(expired);
        Assert.Throws<AuthenticationException>(() => credentials.GetCertificate());
    }

    [Test]
    [Platform("Linux")]
    public async Task ActualCassandraDriverRefreshesClientIdentityAndRejectsFailedRefresh()
    {
        var options = credentials.CreateCassandraSslOptions();
        Assert.That(await DriverHandshake(options, new[] { root, nextRoot }), Is.EqualTo(client.Thumbprint));
        using var renewed = Issue(nextIntermediate, "client", "1.3.6.1.5.5.7.3.2");
        File.WriteAllText(Path.Combine(directory, "ca.pem"), root.ExportCertificatePem() + "\n" + nextRoot.ExportCertificatePem());
        WriteClient(renewed);
        Assert.That(await DriverHandshake(options, new[] { root, nextRoot }), Is.EqualTo(renewed.Thumbprint));
        File.WriteAllText(Path.Combine(directory, "key.pem"), "broken key");
        Assert.That(Validate(server), Is.False);
        Assert.That(await DriverHandshake(options, new[] { root, nextRoot }), Is.Null);
    }

    [Test]
    public async Task LoopbackTlsRejectsWrongSanUnknownServerAndUntrustedClient()
    {
        Assert.That(credentials.GetCertificateContext().IntermediateCertificates.Select(c => c.Thumbprint), Does.Contain(intermediate.Thumbprint));
        Assert.That(await TlsHandshake("localhost", new[] { root }), Is.True);
        Assert.That(await TlsHandshake("wrong.example", new[] { root }), Is.False);
        File.WriteAllText(Path.Combine(directory, "ca.pem"), nextRoot.ExportCertificatePem());
        Assert.That(await TlsHandshake("localhost", new[] { root }), Is.False);
        File.WriteAllText(Path.Combine(directory, "ca.pem"), root.ExportCertificatePem());
        Assert.That(await TlsHandshake("localhost", new[] { nextRoot }), Is.False);
    }

    [Test]
    public async Task LegacyCommonNameUsesNormalHostnameValidation()
    {
        using var commonNameOnly = Issue(root, "legacy-server", "1.3.6.1.5.5.7.3.1", includeSan: false);
        Assert.That(await TlsHandshake("legacy-server", new[] { root }, commonNameOnly), Is.True);
        Assert.That(await TlsHandshake("wrong.example", new[] { root }, commonNameOnly), Is.False);
    }

    [Test]
    public void LegacyValidatorRejectsUnknownRootEvenWhenPlatformReportsSuccess()
    {
        using var chain = PeerChain(server);
        var legacy = new CustomRootCaCertificateValidator(nextRoot);
        Assert.That(legacy.Validate(server, chain, SslPolicyErrors.None), Is.False);
        Assert.That(new CustomRootCaCertificateValidator(root).Validate(server, chain, SslPolicyErrors.RemoteCertificateChainErrors), Is.True);
    }

    [Test]
    [Platform("Linux")]
    public void CassandraStoreFailureCannotFallBackToCertificateLessTls()
    {
        var options = credentials.CreateCassandraSslOptions();
        var dotnetHome = Path.Combine(homeDirectory, ".dotnet");
        Directory.Delete(dotnetHome, true);
        File.WriteAllText(dotnetHome, "blocked store");
        try
        {
            Assert.Catch(() => options.HostNameResolver(IPAddress.Loopback));
            using var peer = PeerChain(server);
            Assert.That(options.RemoteCertValidationCallback(this, server, peer, SslPolicyErrors.RemoteCertificateChainErrors), Is.False);
        }
        finally
        {
            File.Delete(dotnetHome);
        }
    }

    [Test]
    public async Task ServerFullChainRenewsOnNewHandshakesAndValidatesClientPurpose()
    {
        WriteServer(server, serverIntermediate);
        using var owner = new PemTlsOptions(Path.Combine(directory, "ca.pem"),
            Path.Combine(directory, "server.pem"), Path.Combine(directory, "server.key"), serverCertificate: true);
        Assert.That(await TlsHandshake("localhost", new[] { root }, serverOwner: owner,
            expectedServerThumbprint: server.Thumbprint), Is.True);
        using (var peer = PeerChain(client))
            Assert.That(owner.ValidateClientCertificate(this, client, peer, SslPolicyErrors.RemoteCertificateNameMismatch), Is.True);
        using var untrusted = Issue(nextIntermediate, "client", "1.3.6.1.5.5.7.3.2");
        using (var peer = PeerChain(untrusted))
            Assert.That(owner.ValidateClientCertificate(this, untrusted, peer, SslPolicyErrors.RemoteCertificateChainErrors), Is.False);
        using (var peer = PeerChain(server))
            Assert.That(owner.ValidateClientCertificate(this, server, peer, SslPolicyErrors.None), Is.False);
        using var expired = Issue(root, "client", "1.3.6.1.5.5.7.3.2", expired: true);
        using (var peer = PeerChain(expired))
            Assert.That(owner.ValidateClientCertificate(this, expired, peer, SslPolicyErrors.None), Is.False);
        Assert.That(owner.ValidateClientCertificate(this, null, null, SslPolicyErrors.None), Is.False);
        using var renewed = Issue(nextIntermediate, "server", "1.3.6.1.5.5.7.3.1");
        File.WriteAllText(Path.Combine(directory, "ca.pem"), root.ExportCertificatePem() + "\n" + nextRoot.ExportCertificatePem());
        WriteServer(renewed, nextIntermediate);
        Assert.That(await TlsHandshake("localhost", new[] { root }, serverOwner: owner,
            expectedServerThumbprint: renewed.Thumbprint), Is.True);
        WriteServer(client, intermediate);
        Assert.Throws<AuthenticationException>(() => owner.GetCertificateContext());
    }

    private void WriteServer(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        File.WriteAllText(Path.Combine(directory, "server.pem"), certificate.ExportCertificatePem() + "\n" + issuer.ExportCertificatePem());
        using var key = certificate.GetRSAPrivateKey()!;
        File.WriteAllText(Path.Combine(directory, "server.key"), key.ExportPkcs8PrivateKeyPem());
    }

    [TestCase(false)]
    [TestCase(true)]
    [Platform("Linux")]
    public async Task IntermediateChainIsActuallySentToAnIsolatedOpenSslPeer(bool cassandra)
    {
        File.WriteAllText(Path.Combine(directory, "server.pem"), server.ExportCertificatePem());
        using (var key = server.GetRSAPrivateKey()!)
            File.WriteAllText(Path.Combine(directory, "server.key"), key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(directory, "server-chain.pem"), serverIntermediate.ExportCertificatePem());
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var start = new ProcessStartInfo("openssl")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = directory
        };
        foreach (var argument in new[] { "s_server", "-accept", "127.0.0.1:" + port,
            "-cert", "server.pem", "-key", "server.key", "-cert_chain", "server-chain.pem",
            "-Verify", "2", "-verify_return_error", "-CAfile", "ca.pem", "-no-CApath", "-no-CAstore", "-tls1_2" })
            start.ArgumentList.Add(argument);
        // OpenSSL cannot use .NET's CurrentUser CA store, and receives only the root CA file.
        start.Environment["HOME"] = directory;
        start.Environment["SSL_CERT_DIR"] = directory;
        start.Environment["SSL_CERT_FILE"] = Path.Combine(directory, "ca.pem");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<string>();
        using var peer = new Process { StartInfo = start };
        peer.OutputDataReceived += (_, e) => { if (e.Data?.StartsWith("ACCEPT") == true) ready.TrySetResult(); };
        bool sawLeaf = false;
        peer.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            messages.Enqueue(e.Data);
            if (e.Data.StartsWith("depth=0")) sawLeaf = true;
            if (sawLeaf && e.Data == "verify return:1") verified.TrySetResult();
        };
        peer.Start();
        peer.BeginOutputReadLine();
        peer.BeginErrorReadLine();
        Task? connection = null;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            connection = Task.Run(async () =>
            {
                if (cassandra)
                {
                    using var cluster = Cluster.Builder().AddContactPoint(IPAddress.Loopback).WithPort(port)
                        .WithSSL(credentials.CreateCassandraSslOptions())
                        .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(2000)).Build();
                    try { using var session = await cluster.ConnectAsync(); }
                    catch (NoHostAvailableException) { }
                }
                else
                {
                    using var socket = new TcpClient();
                    await socket.ConnectAsync(IPAddress.Loopback, port);
                    using var tls = new SslStream(socket.GetStream());
                    await tls.AuthenticateAsClientAsync(credentials.CreateClientAuthenticationOptions("localhost"));
                    await tls.WriteAsync("probe\n"u8.ToArray());
                }
            });
            await verified.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var evidence = string.Join("\n", messages);
            Assert.That(evidence, Does.Contain("depth=1"));
            Assert.That(evidence, Does.Not.Contain("verify error"));
        }
        finally
        {
            if (!peer.HasExited) peer.Kill();
            await peer.WaitForExitAsync();
            if (connection is not null) await connection.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private bool Validate(X509Certificate2 certificate)
    {
        using var chain = PeerChain(certificate);
        return credentials.ValidateServerCertificate(this, certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors);
    }

    private void WriteClient(X509Certificate2 certificate)
    {
        File.WriteAllText(Path.Combine(directory, "cert.pem"), certificate.ExportCertificatePem() + "\n" +
            (certificate.Issuer == intermediate.Subject ? intermediate.ExportCertificatePem() :
            certificate.Issuer == nextIntermediate.Subject ? nextIntermediate.ExportCertificatePem() : ""));
        using var key = certificate.GetRSAPrivateKey()!;
        File.WriteAllText(Path.Combine(directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());
    }

    private async Task<string?> DriverHandshake(SSLOptions options, X509Certificate2[] clientRoots)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cluster = Cluster.Builder().AddContactPoint(IPAddress.Loopback)
            .WithPort(((IPEndPoint)listener.LocalEndpoint).Port).WithSSL(options)
            .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(2000)).Build();
        var connection = Task.Run(async () =>
        {
            try { using var session = await cluster.ConnectAsync(); }
            catch (NoHostAvailableException) { }
        });
        string? thumbprint = null;
        using (var socket = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10)))
        using (var tls = new SslStream(socket.GetStream(), false, (_, cert, peerChain, _) => ValidateClient(cert, peerChain, clientRoots)))
        {
            try
            {
                await tls.AuthenticateAsServerAsync(ServerOptions()).WaitAsync(TimeSpan.FromSeconds(10));
                // Require application bytes: server-side TLS can finish before the peer rejects it.
                if (await tls.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)) > 0)
                    thumbprint = tls.RemoteCertificate?.GetCertHashString();
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        }
        listener.Stop();
        await connection.WaitAsync(TimeSpan.FromSeconds(10));
        return thumbprint;
    }

    private async Task<bool> TlsHandshake(string hostname, X509Certificate2[] clientRoots, X509Certificate2? serverCertificate = null,
        PemTlsOptions? serverOwner = null, string? expectedServerThumbprint = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            using var tls = new SslStream(accepted.GetStream(), false, (sender, cert, peerChain, errors) =>
                serverOwner?.ValidateClientCertificate(sender, cert, peerChain, errors) ?? ValidateClient(cert, peerChain, clientRoots));
            try { await tls.AuthenticateAsServerAsync(ServerOptions(serverCertificate, serverOwner)); return true; }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { return false; }
        });
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var stream = new SslStream(socket.GetStream());
        bool connected;
        try
        {
            await stream.AuthenticateAsClientAsync(credentials.CreateClientAuthenticationOptions(hostname));
            if (expectedServerThumbprint is not null)
                Assert.That(stream.RemoteCertificate?.GetCertHashString(), Is.EqualTo(expectedServerThumbprint));
            connected = true;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException) { connected = false; }
        return await accept.WaitAsync(TimeSpan.FromSeconds(10)) && connected;
    }

    private SslServerAuthenticationOptions ServerOptions(X509Certificate2? serverCertificate = null, PemTlsOptions? serverOwner = null) => new()
    {
        ServerCertificateContext = serverOwner?.GetCertificateContext() ?? SslStreamCertificateContext.Create(serverCertificate ?? server, new X509Certificate2Collection(serverIntermediate), offline: true), ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12, AllowTlsResume = false
    };

    private static bool ValidateClient(X509Certificate? certificate, X509Chain? peerChain, X509Certificate2[] roots)
    {
        if (certificate is null) return false;
        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        if (peerChain is not null)
            foreach (var element in peerChain.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        return chain.Build(leaf);
    }

    private X509Chain PeerChain(X509Certificate2 leaf)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(new[] { root, nextRoot });
        chain.ChainPolicy.ExtraStore.AddRange(new[] { intermediate, nextIntermediate, serverIntermediate });
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.Build(leaf);
        return chain;
    }

    private static X509Certificate2 CreateRoot(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 Issue(X509Certificate2 ca, string name, string eku, bool expired = false, bool includeSan = true, bool authority = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(authority ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        if (!authority) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(eku) }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        if (includeSan) request.CertificateExtensions.Add(san.Build());
        using var certificate = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-2),
            expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(authority ? 10 : 2), RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }
}
