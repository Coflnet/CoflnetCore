---
name: add-otel-logging
description: >-
  WORKFLOW SKILL — Add OpenTelemetry log shipping (ILogger -> OTLP -> Loki) to a Coflnet
  .NET service using the shared Coflnet.Core library. USE FOR: wiring AddOpenTelemetryLogging
  into a service's host builder; selecting the correct Loki OTLP endpoint; setting the required
  OTEL env vars in the k8s chart; verifying logs arrive in Loki. Covers the full path from
  CoflnetCore NuGet through Program.cs to the eu.yaml/talos.yaml global.envVars and the
  loki-scalable write component. DO NOT USE FOR: tracing setup (use AddCoflnetCore/AddTracing,
  already wired), metrics, or non-Coflnet services.
---

# Add OpenTelemetry logging to a Coflnet service

Ship application `ILogger` output to **Loki** via OTLP, while traces continue to go to
**Jaeger** via `AddCoflnetCore()/AddTracing()`. The shared implementation lives in
`Coflnet.Core.OpenTelemetryLoggingExtensions.AddOpenTelemetryLogging`.

## Architecture

```
Service (talos / eu)
  ├─ Traces ──→ jaeger-collector.observability:4317   (OTLP HttpProtobuf, via AddTracing)
  └─ Logs   ──→ loki-scalable-write.loki:3100/otlp/v1/logs  (OTLP HttpProtobuf, via AddOpenTelemetryLogging)
```

Logs and traces use separate endpoints. Logs go straight to the Loki **write** component
(the distributor, 3 replicas) and bypass the single-replica `loki-scalable-gateway`.

## Steps

### 1. Reference Coflnet.Core ≥ 0.7.3

```xml
<PackageReference Include="Coflnet.Core" Version="0.7.3" />
```

If the service references it transitively (e.g. via the `dev`/`hypixel.csproj` project
reference), bump the version there instead. Package must be published to nuget.org.

### 2. Wire it into the host builder

`AddOpenTelemetryLogging(IConfiguration, string applicationName)` clears the default
providers and installs a single pipeline (OTLP in-cluster, console in dev).

Minimal-hosting (`WebApplication.CreateBuilder`):

```csharp
using Coflnet.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddOpenBaoFromEnvironment(); // if used
builder.Logging.AddOpenTelemetryLogging(
    builder.Configuration,
    builder.Configuration["OTEL_SERVICE_NAME"] ?? "my-service");
```

Generic host (`Host.CreateDefaultBuilder`):

```csharp
using Coflnet.Core;

Host.CreateDefaultBuilder(args)
    .ConfigureLogging((context, logging) =>
        logging.AddOpenTelemetryLogging(
            context.Configuration,
            context.Configuration["JAEGER_SERVICE_NAME"] ?? "my-service"));
```

There must be exactly **one** `AddOpenTelemetryLogging` definition on the classpath. If a
service still references an old per-repo copy (it used to live in `dev/Helper/`), delete that
copy so the call binds to `Coflnet.Core` and the `using` is unambiguous.

### 3. Set the env vars in the k8s chart

In `main/sky/chart/eu.yaml` and `talos.yaml` under `global.envVars`:

```yaml
- name: OTEL_EXPORTER_OTLP_TRACES_ENDPOINT      # traces -> Jaeger (already present)
  value: "http://jaeger-collector.observability:4317"
- name: OTEL_EXPORTER_OTLP_LOGS_ENDPOINT        # logs  -> Loki
  value: "http://loki-scalable-write.loki:3100/otlp/v1/logs"
```

Optional resource attributes (read by the helper from the downward API):

```yaml
- name: OTEL_POD_NAME            # -> k8s.pod.name
  valueFrom: { fieldRef: { fieldPath: metadata.name } }
- name: LOCATION                 # -> cloud.region
  value: "eu"
```

> **Critical:** the OTLP **HttpProtobuf** exporter sends to `Endpoint` **as-is** when it is
> set programmatically — it does **not** append the signal path. The URL therefore MUST end
> in the full path `/otlp/v1/logs` (Loki 3.x native OTLP receiver). A value ending in just
> `/otlp` will 404 and logs are silently dropped.

Endpoint resolution order in the helper: `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` →
`OTEL_EXPORTER_OTLP_ENDPOINT`. The traces endpoint is intentionally NOT a fallback, so logs
never end up in Jaeger. If neither is set (or `DEV_LOGGING=true`), it falls back to console.

### 4. Verify the path is open

- Egress: the `sky` namespace NetworkPolicies are **Ingress-only** (no egress restriction),
  and there is no cluster-wide default-deny `CiliumClusterwideNetworkPolicy`, so sky → loki
  egress is allowed by default. If you add an egress policy later, allow TCP 3100 to loki.
- The Loki **gateway** (`loki-scalable-gateway`) is a separate single-replica nginx. On
  talos it needs `global.dnsService: kube-dns` (RKE2 uses `rke2-coredns-rke2-coredns`),
  otherwise its nginx `resolver` is unresolvable and it crash-loops. We bypass it for log
  ingestion by targeting `loki-scalable-write` directly, but fix the gateway too (talos
  override in `loki/helm/values-talos.yaml`) so reads/canary keep working.

### 5. Confirm logs land in Loki

```bash
# Distributor accepts OTLP (expect HTTP 204 on push, not 404):
kubectl -n loki exec deploy/loki-scalable-read -- \
  wget -qO- 'http://loki-scalable-read:3100/loki/api/v1/labels'

# Or query in Grafana / logcli by service.name:
{service_name="my-service"}
```

## Common pitfalls

- **`/otlp` instead of `/otlp/v1/logs`** → 404, logs dropped. Always include the signal path.
- **Pointing at `loki-scalable-read`** → wrong component (querier). Use `-write`.
- **Pointing at the gateway on talos while it crash-loops** → connection refused. Use `-write`.
- **Duplicate `AddOpenTelemetryLogging`** (old `dev/Helper` copy + Coflnet.Core) → ambiguous
  call / `using` conflict. Keep only the Coflnet.Core one.
- **Forgetting to bump the package version** on nuget.org → consumers restore the old DLL
  without the helper.
