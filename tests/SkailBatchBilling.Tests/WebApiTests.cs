using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkailBatchBilling.Contracts;
using SkailBatchBilling.Web.Skail;

namespace SkailBatchBilling.Tests;

public sealed class FakeGateway : ISkailGateway
{
    public List<(string BatchId, int Size)> Triggers { get; } = [];
    public Exception? FailWith { get; set; }

    public Task TriggerAsync(string batchId, int size, CancellationToken ct = default)
    {
        if (FailWith is not null) throw FailWith;
        Triggers.Add((batchId, size));
        return Task.CompletedTask;
    }
}

public sealed class WebFactory : WebApplicationFactory<Program>
{
    public FakeGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<ISkailGateway>();
            s.AddSingleton<ISkailGateway>(Gateway);
        });
    }
}

public class WebApiTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _factory;
    private readonly HttpClient _http;

    public WebApiTests(WebFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    private async Task<JsonElement> StartAsync(int? size = null)
    {
        var res = await _http.PostAsJsonAsync("/api/batches", size is null ? (object)new { } : new { size });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> PostEventAsync(string id, BatchEvent evt) =>
        _http.PostAsJsonAsync($"/internal/batches/{id}/events", evt, Json.Options);

    [Fact]
    public async Task Health_endpoint_is_ok()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Index_page_is_served_with_the_batch_size_dropdown()
    {
        var html = await _http.GetStringAsync("/");

        Assert.Contains("<select", html);
        foreach (var size in new[] { 10, 20, 50 })
            Assert.Contains($"value=\"{size}\"", html);
        Assert.Contains("id=\"start\"", html);
    }

    [Fact]
    public async Task Start_without_a_size_uses_the_default_of_20_and_triggers_skail()
    {
        var batch = await StartAsync();

        Assert.Equal(20, batch.GetProperty("size").GetInt32());
        Assert.Equal(20, batch.GetProperty("invoices").GetArrayLength());
        Assert.True(batch.GetProperty("active").GetBoolean());
        Assert.Equal("Starting", batch.GetProperty("phase").GetString());
        Assert.Contains(_factory.Gateway.Triggers, t => t.BatchId == batch.GetProperty("batchId").GetString() && t.Size == 20);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    public async Task Start_accepts_the_listed_sizes(int size)
    {
        Assert.Equal(size, (await StartAsync(size)).GetProperty("size").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(51)]
    public async Task Start_rejects_a_size_outside_the_list(int size)
    {
        var res = await _http.PostAsJsonAsync("/api/batches", new { size });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Start_failure_returns_502_and_forgets_the_batch()
    {
        _factory.Gateway.FailWith = new SkailGatewayException("skail answered 401");
        try
        {
            var res = await _http.PostAsJsonAsync("/api/batches", new { size = 10 });
            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
            Assert.Contains("401", await res.Content.ReadAsStringAsync());
        }
        finally { _factory.Gateway.FailWith = null; }
    }

    [Fact]
    public async Task Get_unknown_batch_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/batches/nope")).StatusCode);
    }

    [Fact]
    public async Task Internal_events_update_the_state_served_by_the_api()
    {
        var id = (await StartAsync(10)).GetProperty("batchId").GetString()!;
        var invoice = new Invoice(5, "Customer 005", 100m, InvoiceStatus.Rejected, null, null, "CNPJ");

        Assert.Equal(HttpStatusCode.NoContent, (await PostEventAsync(id, BatchEvent.Started(id, 10))).StatusCode);
        await PostEventAsync(id, BatchEvent.Retrying(id, 10, 5, "fora do ar"));
        await PostEventAsync(id, BatchEvent.InvoiceDone(id, 10, invoice));

        var view = await _http.GetFromJsonAsync<JsonElement>($"/api/batches/{id}");
        Assert.Equal("Issuing", view.GetProperty("phase").GetString());
        Assert.Equal(1, view.GetProperty("done").GetInt32());
        var row = view.GetProperty("invoices")[4];
        Assert.Equal("Rejected", row.GetProperty("state").GetString());
        Assert.Equal(2, row.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task Stream_sends_the_snapshot_then_each_update_and_closes_when_the_batch_ends()
    {
        var id = (await StartAsync(10)).GetProperty("batchId").GetString()!;
        using var response = await _http.GetAsync($"/api/batches/{id}/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        await PostEventAsync(id, BatchEvent.Started(id, 10));
        await PostEventAsync(id, BatchEvent.Completed(id, new BatchResult(id, 10, 10, 0, 0, 1000m)));

        var phases = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            phases.Add(JsonDocument.Parse(line[6..]).RootElement.GetProperty("phase").GetString()!);
        }

        Assert.Equal("Starting", phases[0]);
        Assert.Equal("Completed", phases[^1]);
        Assert.Contains("Issuing", phases);
    }

    [Fact]
    public async Task Stream_of_an_unknown_batch_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/batches/nope/stream")).StatusCode);
    }

    [Fact]
    public async Task Config_lists_the_sizes()
    {
        var config = await _http.GetFromJsonAsync<JsonElement>("/api/config");

        Assert.Equal(new[] { 10, 20, 50 }, config.GetProperty("sizes").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(20, config.GetProperty("defaultSize").GetInt32());
    }
}
