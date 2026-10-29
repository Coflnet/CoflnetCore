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
