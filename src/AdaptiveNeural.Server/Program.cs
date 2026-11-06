using AdaptiveNeural.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SimulationEngine>();
builder.Services.AddHostedService<SimulationWorker>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/snapshot", (int? version, SimulationEngine engine) => engine.Snapshot(version ?? -1));
app.MapPost("/api/control", (ControlRequest request, SimulationEngine engine) =>
{
    engine.Apply(request);
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/stimulus", (StimulusRequest request, SimulationEngine engine) =>
{
    engine.Inject(request);
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/world/spawn", (SpawnRequest request, SimulationEngine engine) =>
{
    engine.Spawn(request);
    return Results.Ok(new { ok = true });
});
app.MapGet("/api/health", () => Results.Ok(new { status = "ready" }));
app.MapFallbackToFile("index.html");
app.Run();

internal sealed class SimulationWorker(SimulationEngine engine, ILogger<SimulationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000d / 30d));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) engine.Tick();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Simulation stopped");
        }
    }
}
