using System.Text.Json.Serialization;
using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Hosting;
using SkailBatchBilling;
using SkailBatchBilling.Demo;
using SkailBatchBilling.Skail;

[assembly: VisibleToSkailPlatform]

// usage: dotnet run
//   then open http://localhost:8080
if (args.Any(a => !a.StartsWith('-')))
{
    Console.Error.WriteLine("This demo takes no arguments.");
    return 1;
}

DotEnv.Load();
string[] required = ["SKAIL_WORKLOAD", "SKAIL_NAMESPACE", "SKAIL_KEY", "SKAIL_SIDECAR"];
var missing = required.Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine($"Missing {string.Join(", ", missing)}. Copy .env.example to .env and fill it in.");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);

// keep the console for the demo: only warnings and errors from the host and the libraries
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// the page: localhost only, unless DEMO_LISTEN says otherwise (the container listens on all interfaces)
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("DEMO_LISTEN") ?? "http://localhost:8080");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<BillingDesk>();
builder.Services.AddSingleton<SimulatedCityHall>();
builder.Services.AddSingleton(SkailApiOptions.FromEnvironment());
builder.Services.AddHttpClient<SkailApiClient>(http => http.Timeout = TimeSpan.FromSeconds(10));

// hosts the worker that executes BillingFunctions; reads the SKAIL_* variables
builder.UseSkail();

var app = builder.Build();
var desk = app.Services.GetRequiredService<BillingDesk>();

var page = Path.Combine(AppContext.BaseDirectory, "Page", "index.html");
app.MapGet("/", () => Results.File(page, "text/html; charset=utf-8"));
// before the first one, the state is null: written as JSON, since an empty body is not JSON
app.MapGet("/api/state", () =>
    desk.View() is { } view ? Results.Json(view) : Results.Text("null", "application/json"));
app.MapPost("/api/batches/{size:int}", async (int size, CancellationToken ct) =>
{
    if (!BatchLimits.Sizes.Contains(size)) return Results.BadRequest();
    await desk.StartAsync(size, ct);
    return Results.Ok();
});

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine("Open http://localhost:8080 to bill a batch. Ctrl+C exits; a batch in the middle goes on in skail."));

await app.RunAsync();
return 0;
