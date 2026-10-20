using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

#nullable enable

namespace Coflnet.Security.OpenBao;

public static class OpenBaoConfigurationExtensions
{
    public static IConfigurationBuilder AddOpenBaoFromEnvironment(this IConfigurationBuilder builder)
    {
        var options = OpenBaoOptions.FromEnvironment();
        if (!options.Enabled && string.IsNullOrWhiteSpace(options.Address))
            return builder;
        return builder.Add(new OpenBaoConfigurationSource(options));
    }
}

internal sealed class OpenBaoConfigurationSource : IConfigurationSource
{
    private readonly OpenBaoOptions options;
    public OpenBaoConfigurationSource(OpenBaoOptions options) => this.options = options;
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new OpenBaoConfigurationProvider(options);
}

internal sealed class OpenBaoConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly object reloadLock = new();
    private readonly OpenBaoOptions options;
    private readonly Timer? timer;

    public OpenBaoConfigurationProvider(OpenBaoOptions options)
    {
        this.options = options;
        if (options.ReloadInterval > TimeSpan.Zero)
        {
            timer = new Timer(_ => Reload(initialLoad: false), null, Timeout.InfiniteTimeSpan, options.ReloadInterval);
        }
    }

    public override void Load()
    {
        Reload(initialLoad: true);
        if (timer != null)
            timer.Change(options.ReloadInterval, options.ReloadInterval);
    }

    public void Dispose() => timer?.Dispose();

    private void Reload(bool initialLoad)
    {
        if (!Monitor.TryEnter(reloadLock))
            return;

        try
        {
            Data = LoadWithRetriesAsync(initialLoad).GetAwaiter().GetResult();
            LogInfo($"loaded {Data.Count} configuration keys from {options.Mount}/{options.Path}");
            OnReload();
        }
        catch (Exception ex) when (options.Optional || !initialLoad)
        {
            LogError("load; retaining previous values", ex);
        }
        finally
        {
            Monitor.Exit(reloadLock);
        }
    }

    private async Task<IDictionary<string, string?>> LoadWithRetriesAsync(bool initialLoad)
    {
        var retryWindow = initialLoad ? options.StartupRetryWindow : options.RequestTimeout;
        var deadline = DateTimeOffset.UtcNow + retryWindow;
        Exception? lastError = null;
        var attempt = 0;

        do
        {
            attempt++;
            try
            {
                return await LoadAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (DateTimeOffset.UtcNow < deadline)
            {
                lastError = ex;
                LogError($"attempt {attempt}; retrying", ex);
                await Task.Delay(options.RetryDelay).ConfigureAwait(false);
            }
        } while (DateTimeOffset.UtcNow < deadline);

        throw new InvalidOperationException("OpenBao configuration retries exhausted.", lastError);
    }

    private static void LogError(string operation, Exception ex)
    {
        Console.Error.WriteLine($"[OpenBao] {operation} failed: {ex}");
    }

    private static void LogInfo(string message)
    {
        Console.Error.WriteLine($"[OpenBao] {message}");
    }

    private async Task<IDictionary<string, string?>> LoadAsync()
    {
        LogInfo($"Starting: addr={options.Address} role={options.Role} path={options.Path} mount={options.Mount} optional={options.Optional}");

        // ── validation ──────────────────────────────────────────────
        try { options.Validate(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"OpenBao configuration validation failed. " +
                $"Required: OPENBAO__ADDR, OPENBAO__ROLE, OPENBAO__PATH, and a valid OPENBAO__TOKEN_PATH. " +
                $"Check your environment variables.", ex);
        }
        LogInfo($"Validation passed. Reading JWT from {options.TokenPath}");

        var jwt = await File.ReadAllTextAsync(options.TokenPath).ConfigureAwait(false);
        LogInfo($"JWT read ({jwt.Length} chars)");

        using var trustedRoot = string.IsNullOrWhiteSpace(options.CACertPath)
            ? null
            : X509CertificateLoader.LoadCertificateFromFile(options.CACertPath);
        using var handler = CreateHttpHandler(trustedRoot);
        using var client = new HttpClient(handler) { BaseAddress = new Uri(options.Address.TrimEnd('/') + "/") };
        client.Timeout = options.RequestTimeout;
        LogInfo($"HttpClient created with base={client.BaseAddress} timeout={client.Timeout.TotalSeconds}s");

        string? token = null;
        try
        {
            // ── login ───────────────────────────────────────────────
            LogInfo($"Logging in to OpenBao at v1/auth/{options.AuthPath.Trim('/')}/login with role={options.Role}");
            string loginBody;
            try
            {
                var loginPayload = JsonSerializer.Serialize(new { role = options.Role, jwt });
                using var loginRequest = new HttpRequestMessage(HttpMethod.Post, $"v1/auth/{options.AuthPath.Trim('/')}/login")
                {
                    Content = new StringContent(loginPayload, Encoding.UTF8, "application/json")
                };
                using var loginResponse = await client.SendAsync(loginRequest).ConfigureAwait(false);
                loginResponse.EnsureSuccessStatusCode();
                loginBody = await loginResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"OpenBao login failed at {options.Address}/v1/auth/{options.AuthPath.Trim('/')}/login " +
                    $"(role={options.Role}). Verify the OpenBao address, auth path, role, and service-account token.", ex);
            }

            using (var loginDocument = JsonDocument.Parse(loginBody))
                token = loginDocument.RootElement.GetProperty("auth").GetProperty("client_token").GetString();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("OpenBao login response did not contain a client_token.");
            LogInfo("Login succeeded");

            // ── fetch secret ────────────────────────────────────────
            LogInfo($"Fetching secret from v1/{options.Mount.Trim('/')}/data/{options.Path.Trim('/')}");
            string secretBody;
            try
            {
                using var secretRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/{options.Mount.Trim('/')}/data/{options.Path.Trim('/')}");
                secretRequest.Headers.Add("X-Vault-Token", token);
                secretRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var secretResponse = await client.SendAsync(secretRequest).ConfigureAwait(false);
                secretResponse.EnsureSuccessStatusCode();
                secretBody = await secretResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"OpenBao secret read failed at {options.Address}/v1/{options.Mount.Trim('/')}/data/{options.Path.Trim('/')}. " +
                    "Verify the mount point and secret path.", ex);
            }

            // ── parse secret ────────────────────────────────────────
            try
            {
                using var secretDocument = JsonDocument.Parse(secretBody);
                var data = secretDocument.RootElement.GetProperty("data").GetProperty("data");
                var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in data.EnumerateObject())
                {
                    var key = property.Name.Replace("__", ":", StringComparison.Ordinal);
                    result[key] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.GetRawText();
                }
                return result;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"OpenBao secret response was malformed for path {options.Mount}/{options.Path}. " +
                    "Expected KV v2 response with .data.data.", ex);
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(token))
                await RevokeTokenAsync(client, token).ConfigureAwait(false);
        }
    }

    private HttpClientHandler CreateHttpHandler(X509Certificate2? trustedRoot)
    {
        var handler = new HttpClientHandler();
        if (trustedRoot is null)
            return handler;

        handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
        {
            if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
                return false;

            var expectedName = string.IsNullOrWhiteSpace(options.TLSServerName)
                ? request.RequestUri?.Host
                : options.TLSServerName;
            if (string.IsNullOrWhiteSpace(expectedName) ||
                !certificate.MatchesHostname(expectedName, allowWildcards: true, allowCommonName: true))
                return false;

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            return chain.Build(certificate);
        };
        return handler;
    }

    private static async Task RevokeTokenAsync(HttpClient client, string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/auth/token/revoke-self");
            request.Headers.Add("X-Vault-Token", token);
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.OK)
                LogInfo($"Token self-revocation returned HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            LogError("token self-revocation", ex);
        }
    }
}

internal sealed record OpenBaoOptions
{
    public bool Enabled { get; init; }
    public bool Optional { get; init; }
    public string Address { get; init; } = "";
    public string AuthPath { get; init; } = "kubernetes";
    public string Mount { get; init; } = "kv";
    public string Path { get; init; } = "";
    public string Role { get; init; } = "";
    public string TokenPath { get; init; } = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    public string CACertPath { get; init; } = "";
    public string TLSServerName { get; init; } = "";
    public TimeSpan ReloadInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StartupRetryWindow { get; init; } = TimeSpan.Zero;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public static OpenBaoOptions FromEnvironment()
    {
        return new OpenBaoOptions
        {
            Enabled = Bool("OPENBAO__ENABLED", false),
            Optional = Bool("OPENBAO__OPTIONAL", true),
            Address = Env("OPENBAO__ADDR"),
            AuthPath = Env("OPENBAO__AUTH_PATH", "kubernetes"),
            Mount = Env("OPENBAO__MOUNT", "kv"),
            Path = Env("OPENBAO__PATH"),
            Role = Env("OPENBAO__ROLE"),
            TokenPath = Env("OPENBAO__TOKEN_PATH", "/var/run/secrets/kubernetes.io/serviceaccount/token"),
            CACertPath = Env("OPENBAO__CACERT"),
            TLSServerName = Env("OPENBAO__TLS_SERVER_NAME"),
            ReloadInterval = TimeSpan.FromSeconds(Int("OPENBAO__RELOAD_SECONDS", 300)),
            RequestTimeout = TimeSpan.FromSeconds(Int("OPENBAO__REQUEST_TIMEOUT_SECONDS", 30)),
            StartupRetryWindow = TimeSpan.FromSeconds(Int("OPENBAO__STARTUP_RETRY_SECONDS", 0)),
            RetryDelay = TimeSpan.FromSeconds(Int("OPENBAO__RETRY_DELAY_SECONDS", 2))
        };
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Address)) throw new InvalidOperationException("OPENBAO__ADDR is required.");
        if (string.IsNullOrWhiteSpace(Role)) throw new InvalidOperationException("OPENBAO__ROLE is required.");
        if (string.IsNullOrWhiteSpace(Path)) throw new InvalidOperationException("OPENBAO__PATH is required.");
        if (!File.Exists(TokenPath)) throw new FileNotFoundException("Kubernetes service account token not found.", TokenPath);
        if (!string.IsNullOrWhiteSpace(CACertPath) && !File.Exists(CACertPath))
            throw new FileNotFoundException("OpenBao CA certificate not found.", CACertPath);
        if (RequestTimeout <= TimeSpan.Zero || StartupRetryWindow < TimeSpan.Zero || RetryDelay <= TimeSpan.Zero)
            throw new InvalidOperationException("OpenBao timeout and retry settings are invalid.");
    }

    private static string Env(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) ?? fallback;
    private static bool Bool(string key, bool fallback) => bool.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : fallback;
    private static int Int(string key, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : fallback;
}
