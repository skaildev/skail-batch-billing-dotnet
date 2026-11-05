using System.Text.Json;
using System.Text.Json.Serialization;
using SkailBatchBilling.Contracts;
using SkailBatchBilling.Web.Batches;
using SkailBatchBilling.Web.Skail;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<BatchStore>();
builder.Services.AddSingleton(SkailOptions.FromEnvironment());
builder.Services.AddHttpClient<ISkailGateway, SkailApiGateway>(c => c.Timeout = TimeSpan.FromSeconds(10));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" });

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapGet("/api/config", () => Results.Ok(new
{
    sizes = BatchLimits.Sizes,
    defaultSize = BatchLimits.DefaultSize,
    monitorUrl = Environment.GetEnvironmentVariable("MONITOR_URL") is { Length: > 0 } url ? url : null,
}));

app.MapPost("/api/batches", async (StartBatchRequest? request, BatchStore store, ISkailGateway skail, CancellationToken ct) =>
{
    var size = request?.Size ?? BatchLimits.DefaultSize;
    if (!BatchLimits.Sizes.Contains(size))
        return Results.BadRequest(new { error = $"Unsupported batch size {size}. Use one of: {string.Join(", ", BatchLimits.Sizes)}" });

    var batchId = Guid.NewGuid().ToString();
    var batch = store.Create(batchId, size);
    try
    {
        await skail.TriggerAsync(batchId, size, ct);
    }
    catch (SkailGatewayException e)
    {
        store.Remove(batchId);
        return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    return Results.Ok(BatchView.From(batch));
});

app.MapGet("/api/batches/{id}", (string id, BatchStore store) =>
    store.Get(id) is { } batch ? Results.Ok(BatchView.From(batch)) : Results.NotFound());

// called by the worker's ReportStatus command (and by the city hall retries)
app.MapPost("/internal/batches/{id}/events", (string id, BatchEvent evt, BatchStore store) =>
{
    store.Apply(evt with { BatchId = id });
    return Results.NoContent();
});

app.MapGet("/api/batches/{id}/stream", async (string id, HttpContext http, BatchStore store, CancellationToken ct) =>
{
    using var subscription = store.Subscribe(id); // subscribe before reading the snapshot: no update is lost
    if (store.Get(id) is not { } current) return Results.NotFound();

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task Send(BatchState state)
    {
        var json = JsonSerializer.Serialize(BatchView.From(state), Json.Options);
        await http.Response.WriteAsync($"data: {json}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        await Send(current);
        if (current.IsTerminal) return Results.Empty;

        await foreach (var state in subscription.Reader.ReadAllAsync(ct))
        {
            await Send(state);
            if (state.IsTerminal) break;
        }
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
    return Results.Empty;
});

app.Run();

public sealed record StartBatchRequest(int? Size);

public sealed record BatchView(
    string BatchId,
    int Size,
    BatchPhase Phase,
    int Done,
    bool Active,
    IReadOnlyList<InvoiceView> Invoices,
    BatchResult? Result,
    string? Error,
    IReadOnlyList<CustomerMail> CustomerInbox,
    IReadOnlyList<FinanceTicket> FinanceInbox)
{
    public static BatchView From(BatchState s) => new(s.BatchId, s.Size, s.Phase, s.Done, s.Active, s.Invoices, s.Result, s.Error, s.CustomerInbox, s.FinanceInbox);
}

public partial class Program;
