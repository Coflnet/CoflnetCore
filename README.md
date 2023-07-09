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
