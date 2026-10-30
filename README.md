# CoflnetCore
Core library for our Microservices, producing, consuming, metrics etc


## Setup
Add services in your `Startup.cs` or `Program.cs` file
```csharp
builder.Services.AddCoflnetCore();

[...]
// register middlewares before controllers
app.UseCoflnetCore();
app.MapControllers();
```

## Usage
### Consuming 
```csharp
public class ConsumeService : BackgroundService
{
    private readonly KafkaConsumer _consumer;

    public MyController(KafkaConsumer consumer)
    {
        _consumer = consumer;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _consumer.Consume<string>("my-topic", async (message) =>
        {
            // do something with the message
        }, stoppingToken);
    }
}
```

## OpenAPI documents
`AddCoflnetOpenApi` (called by `AddCoflnetCore`) shapes the generated OpenAPI document so clients generated from it compile and match the server:

- **ApiException**: `[ProducesResponseType(typeof(ApiException), 401)]` (and any subclass, e.g. `ApiStatusException`) is documented as the error body that `ErrorHandler` really sends, a `$ref` to the shared `ApiErrorResponse` component (`slug`, `message` strings and a nullable `trace`). The System.Exception reflection types are not part of the document. The component is only added when the document references it. Subclasses are found by scanning the loaded assemblies when the swagger options are first resolved. A service class that is itself named `ApiErrorResponse` clashes with the component, generating the document throws an `InvalidOperationException` naming the clash, rename the class. A service `ErrorResponse` class is fine. A 400 response is still replaced by the generic inline error schema of `ErrorResponseOperationFilter`.
- **Enums**: enums are sent as names, so the `enum` list has no numbers. Every enum schema (nullable enums included, also with `UseInlineDefinitionsForEnums`) additionally carries `x-enum-members` (a list of `{name, value}` with the numeric value, ulong enums included) and, for `[Flags]` enums, `x-flags: true`. The `enum` list itself is not changed. Clients should read these extensions to rebuild the enum, a reference template for openapi-generator csharp is `Client/templates/modelEnum.mustache` in Coflnet/AneNotifier (added in PR #47). Limitation: `x-enum-members` uses the C# member names. If the wire name differs (`[JsonStringEnumMemberName]` or a naming policy on the converter) the names do not match the values of the `enum` list.
- **`object?` properties and dictionary values**: a schema whose type is only `null` (`object?`, the value of `Dictionary<string, object?>`) ends up with no type at all (`{}`) instead of a `null` only type that openapi-generator turns into an enum (`enum: [null]`) that does not compile. This is done on the whole document, also for `allOf` wrappers, references and schemas with enum, properties, items or composition are not touched. Nullable strings, ints etc. are not affected.

## Releasing
Bump `<Version>` in `Coflnet.Core.csproj` and merge to `main`. The `Package` workflow
(`.github/workflows/package.yml`) runs the tests, packs, attests the build and publishes `Coflnet.Core`
to nuget.org via NuGet trusted publishing; no API key is stored anywhere. A version that already exists
on nuget.org is skipped. `publishPackage.sh` only packs locally into `./out`.

## PEM mutual TLS

`AddCassandra()` accepts `CASSANDRA:TLS:CA_CERT_PATH`, `CASSANDRA:TLS:CERT_PATH`
and `CASSANDRA:TLS:KEY_PATH` (likewise under `OLD_CASSANDRA`). Set all three to
mounted PEM files. The CA file may contain overlapping old and new root
certificates. TLS is opt-in; Cassandra username/password authentication remains
in place. Existing `X509Certificate_PATHS`, `X509Certificate_PASSWORD` and
`X509Certificate_VALIDATION_PATH` PFX configuration remains supported. Without a
legacy custom CA, normal platform validation applies; invalid certificates are
never implicitly accepted.

PEM Cassandra connections validate the discovered endpoint's **IP SAN** by
default. If every node has a shared DNS SAN, set `TLS:SERVER_NAME` explicitly to
that name; this changes the TLS target name and still requires a matching SAN.
Legacy server certificates without SANs use normal TLS common-name matching;
there is no hostname-error exception. New PKI certificates should include SANs.
Server certificates require valid dates, server authentication usage and a chain
to the configured bundle; platform trust alone cannot bypass that bundle.
Revocation network lookups are disabled for this private PKI. Use short-lived
certificates and remove retired roots from the bundle after the overlap window.

PEM files are read for every new handshake, so atomic projected-volume renewal
needs no process restart and leaves active connections alone. Broken, mismatched
or expired replacements fail closed. Cassandra refresh uses the supported
`SSLOptions.HostNameResolver` hook immediately before the driver reads its client
certificate collection. This ordering is verified against **CassandraCSharpDriver
3.23.0** by a real loopback TLS test; run that test when upgrading the driver.

Other TLS clients can use `Coflnet.Core.Security.PemTlsOptions` without a Redis
package dependency in Core. For StackExchange.Redis, retain your existing parsed
connection options, including username/password, and register a single owner:

```csharp
services.AddSingleton(p => PemTlsOptions.FromConfiguration(
    p.GetRequiredService<IConfiguration>().GetSection("REDIS:TLS")));
services.AddSingleton<IConnectionMultiplexer>(p =>
{
    var config = p.GetRequiredService<IConfiguration>();
    var options = ConfigurationOptions.Parse(config["REDIS_HOST"]!);
    var tls = p.GetRequiredService<PemTlsOptions>();
    options.Ssl = true;
    // StackExchange.Redis 2.8.16 uses its net6.0 asset on modern .NET.
    options.SslClientAuthenticationOptions = host =>
        tls.CreateClientAuthenticationOptions(options.SslHost ?? host);
    return ConnectionMultiplexer.Connect(options);
});
```

Redis uses its endpoint hostname for TLS validation, or its explicit `SslHost`
setting. The options provider sends the PEM leaf and intermediate chain; a
leaf-only `CertificateSelection` callback cannot reliably transmit intermediates.
This provider replaces Redis TLS settings, while parsed Redis authentication and
connection settings remain in place. Keep the `PemTlsOptions` owner alive until
its connections are disposed.
It retains superseded certificates until disposal to protect simultaneous
handshakes; retained objects grow only with successful certificate rotations.
Publish certificate/key pairs atomically, for example with a projected-volume
symlink switch. A handshake spanning a file replacement can fail closed and be
retried by the client. Do not add another validation callback that accepts errors.

`CERT_PATH` is a PEM fullchain containing the leaf followed by its issuing
intermediates. `GetCertificate()` returns the leaf with its key;
`GetCertificateContext()` returns the full chain for modern TLS APIs. Neither
changes machine trust. Cassandra 3.23 exposes only the older leaf API, so its
adapter explicitly registers validated **non-root public intermediates** in
`StoreName.CertificateAuthority` for `StoreLocation.CurrentUser`. Existing entries
are preserved and duplicate writes are avoided. Registration failure aborts new
connections. The workload UID must have a writable, private, ephemeral `HOME`
(on Linux the store is below `$HOME/.dotnet/corefx/cryptography/x509stores/ca`).
Use an emptyDir or equivalent per-workload home; no root certificate is added to
this store and no machine store is modified. Public intermediate entries remain
until that ephemeral home is discarded; they do not override the explicit root
bundle used for peer validation.

For a TLS server, use `PemTlsOptions.FromConfiguration(section,
serverCertificate: true)` to require server-purpose credentials. Obtain
`GetCertificateContext()` on each new server handshake to pick up leaf/key/chain
renewals, and use `ValidateClientCertificate` for client authentication against
the current root bundle, client EKU and validity dates. This callback rejects a
missing certificate; an application offering optional mTLS must make its own
explicit decision for clients that present none. It performs no client DNS-name
check. The library does not configure listeners or alter existing HTTP endpoints.

### MySqlConnector 2.6.2 and MariaDB

For strict private-PKI verification, use a single long-lived `PemTlsOptions`
owner and install its `ValidateServerCertificate` callback on every
`MySqlConnection` before opening it. Use the driver's supported `SslMode=Required`
with an empty native `SslCa`: setting `SslCa`, `VerifyCA` or `VerifyFull` prevents
that custom callback from running. This is verified TLS through the strict
callback, not acceptance of certificate errors.

A real MariaDB 11.8.8 test demonstrated that native `VerifyFull` can accept both
wrong-name and unknown-CA certificates using MariaDB's password-bound TLS
fingerprint. The strict Core callback returns false during TLS and prevents that
fallback before authentication. Preserve database usernames/passwords and require
normal hostname validation for every configured host.

Keep a multi-host `Server` list intact. MySqlConnector supplies the **selected
host for each physical attempt** as the callback sender's
`SslStream.TargetHostName`; the TLS stack checks that host's SAN. Do not compare
the certificate with the entire comma-separated list or pin validation to its
first host. Keep `ValidateServerCertificate` unchanged so hostname errors remain
fatal. An isolated native test forced an unavailable first host, succeeded on a
matching second IP, rejected a reachable selected IP missing from the SAN, then
succeeded again on the matching alternate endpoint with normal client renewal.

The driver's native `SslCert`/`SslKey` loader supplies only the leaf certificate.
Use `ProvideClientCertificatesCallback` to call `RegisterClientIntermediates()`
and add `GetCertificate()` to its collection on fresh physical TLS connections.
Clear native `SslCert`/`SslKey` settings so they cannot override that provider.
Only validated non-root public intermediates enter the workload UID's CurrentUser
CA store; a private writable ephemeral `HOME` is required on Linux. No machine
or root trust store is changed. A separate MariaDB process with root-only trust,
a different server intermediate and an account requiring X509 verified fullchain
transmission, client renewal and old-client-root rejection.

Pooled physical connections do not renegotiate TLS. Set a bounded
`ConnectionLifeTime` (for example, at most 300 seconds while preserving a shorter
explicit value), and publish certificate/key pairs atomically. An EF
`DbConnectionInterceptor` should only install the strict validation and client
certificate-provider callbacks for synchronous/asynchronous logical opens.
Perform no file reads, chain building or store registration on pooled reuse:
doing that for every logical open caused sustained CPU throttling in the live
PlayerName pilot. The certificate provider and strict validation callback reload
material on physical TLS handshakes; broken replacements fail closed there.

### Kafka consumer renewal (0.7.6, opt-in)

`KAFKA:TLS:RELOAD_ON_CHANGE=true` opts the single-topic `KafkaConsumer.Consume`
path into native handle reconstruction. Default behavior and producer APIs stay
unchanged. Activation is limited to SASL_SSL with the existing SASL username and
credentials; bare-SSL certificate-principal changes are outside this consumer path.
Continue using `KAFKA:TLS:CA_LOCATION`, `CERTIFICATE_LOCATION` and
`KEY_LOCATION`; no global or producer cutover is implied.

The consumer watches containing directories, including Kubernetes projected
`..data` replacement. On a new generation it validates an immutable CA/fullchain/
key snapshot and client EKU/lifetime, then preflights read-only topic metadata with
a separate AdminClient and the existing SASL settings, bounded at 60 seconds.
Location fields are cleared when PEM strings are installed, so concurrent file
replacement cannot change a preflighted candidate. Incomplete, invalid, near-expiry
replacements leave the current native context running and wait for new material.
A locally validated snapshot that fails network preflight or native handoff is
retried quietly after 30 seconds, doubling to a five-minute cap. Retries reuse
that immutable snapshot without reading files or rebuilding certificate chains;
a new file event cancels/supersedes old retries, including stale preflight results.
Failures log once per material generation/type, with no heartbeat logs. An
already preflighted candidate may remain pending when
a later generation is rejected; its lifetime is checked again at the drain
boundary. No file polling or per-record certificate/chain work is performed.

After successful preflight, idle consumption is interrupted; a record returned
concurrently with cancellation is still processed. In-flight work/commit finish
at a safe boundary, then an unsubscribed replacement native consumer is built
while the old handle is alive. Pre-Close build/auth/expiry/supersession failure
retains the original handle. After Close, the old handle cannot be resurrected:
failed handoff reconstructs the previous validated immutable generation and
explicitly reports failure. Assignment/auth readiness is bounded at 60 seconds
and requires no business message, so empty topics are supported. Candidate
validity is checked again after Close and assignment.

The opt-in path disables auto-commit and auto-offset-store and explicitly commits
the next offset for each processed partition. Close cannot acknowledge a fetched
but unprocessed record; commit failure may still cause replay. Exactly-once
delivery is not promised. Legacy offset values and Close behavior remain
unchanged outside opt-in rotation. Independent configuration dictionaries keep
candidate/preflight settings from mutating the previous generation.

Metadata preflight proves transport/metadata access, not consumer-group/read ACLs
or application delivery. Production enrollment still needs fresh-handle delivery,
broker identity, rebalance, malformed renewal and rollback evidence. If no valid
renewal arrives before the old leaf expires, retaining an old context cannot make
its next handshake valid; expiry alerting/recovery remains mandatory. Do not
activate a short-lived Kafka leaf merely because its Secret renewed.

Run tests with `dotnet test Coflnet.Core.Tests/Coflnet.Core.Tests.csproj`; the
test project supplies a private HOME to the test host at process launch, before
any X509 store path can cache. Restore keeps normal authentication/configuration.
The store-failure fixture uses that actual process store and never changes HOME
in-process. The test target removes the private test home afterward.
The explicit `NativeFullChainRotationRebuildsTheHandleAndDeliversWithStrictServerValidation`
test starts only a private local Docker network and loopback broker from cached
`redpandadata/redpanda@sha256:df0d30a08e464fd4f2dfde9735f9b5e73f7cd1d81de87a86a97df859c01bf091`;
it uses synthetic keys/messages and removes its
container/network afterward. It proves renewal with two members sharing one
partition, including a member with no partitions, and delivery after renewal.
Injected post-Close recovery is unit-tested; native broker failure injection
remains unverified. Local fixture success does not attest production broker
identity, account permissions or ACLs. It does not use any production Kafka endpoint.
