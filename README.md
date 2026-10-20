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
