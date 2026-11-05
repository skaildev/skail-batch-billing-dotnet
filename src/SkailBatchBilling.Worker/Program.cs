using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Hosting;
using SkailBatchBilling.Worker;
using SkailBatchBilling.Worker.CityHall;
using SkailBatchBilling.Worker.Progress;

[assembly: VisibleToSkailPlatform]

var builder = Host.CreateApplicationBuilder(args);

// HttpClient request logs would show up as extra lines under each command in the skail Monitor
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

var webBaseUrl = Environment.GetEnvironmentVariable("WEB_BASE_URL") ?? "http://localhost:8080";
var provider = Environment.GetEnvironmentVariable("CITY_HALL_PROVIDER") ?? "simulated";
var outagePercent = int.TryParse(Environment.GetEnvironmentVariable("CITY_HALL_OUTAGE_PERCENT"), out var p) ? Math.Clamp(p, 0, 100) : 10;

if (provider.Equals("fake", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<ICityHall>(new FakeCityHall());
else
    builder.Services.AddSingleton<ICityHall>(new SimulatedCityHall(outagePercent));

builder.Services.AddHttpClient<IProgressNotifier, HttpProgressNotifier>(c =>
{
    c.BaseAddress = new Uri(webBaseUrl.EndsWith('/') ? webBaseUrl : webBaseUrl + "/");
    c.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<BillingOperations>();

// reads SKAIL_WORKLOAD, SKAIL_NAMESPACE, SKAIL_KEY and SKAIL_SIDECAR from the environment (.env)
builder.UseSkail();

await builder.Build().RunAsync();
