using System.Net;
using System.Text.Json;
using SkailBatchBilling.Web.Skail;

namespace SkailBatchBilling.Tests;

public class GatewayTests
{
    private static readonly SkailOptions Opts = new()
    {
        BaseUrl = "https://skail.example.test/",
        Namespace = "tenant-1/env-1",
        Key = "env-key",
        Workload = "batch-billing:v0.0.1",
    };

    private static SkailApiGateway Gateway(StubHandler handler, SkailOptions? options = null) =>
        new(new HttpClient(handler), options ?? Opts, retryDelay: _ => TimeSpan.Zero);

    [Fact]
    public async Task Trigger_posts_the_arguments_to_the_function_route_of_the_workload()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).TriggerAsync("batch-1", 20);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://skail.example.test/api/v1/trigger/batch-billing/v0.0.1/BillBatch/batch-1", request.RequestUri!.ToString());
        Assert.Equal("env-key", request.Headers.GetValues("skail-key").Single());
        Assert.Equal("Bearer env-key", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("tenant-1/env-1", request.Headers.GetValues("Skail-namespace").Single());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
        var args = JsonSerializer.Deserialize<JsonElement[]>(body)!;
        Assert.Equal("batch-1", args[0].GetString());
        Assert.Equal(20, args[1].GetInt32()); // size is a JSON number, not a string
    }

    [Fact]
    public void Options_read_the_api_base_url_from_SKAIL_BASE_URL()
    {
        var names = new[] { "SKAIL_BASE_URL", "SKAIL_NAMESPACE", "SKAIL_KEY", "SKAIL_WORKLOAD" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("SKAIL_BASE_URL", "https://env.example.test");
            Environment.SetEnvironmentVariable("SKAIL_NAMESPACE", "t/e");
            Environment.SetEnvironmentVariable("SKAIL_KEY", "k");
            Environment.SetEnvironmentVariable("SKAIL_WORKLOAD", "w:v1");

            var options = SkailOptions.FromEnvironment();

            Assert.Equal("https://env.example.test", options.BaseUrl);
            Assert.Equal(("t/e", "k", "w:v1"), (options.Namespace, options.Key, options.Workload));
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task Batch_id_is_escaped_in_the_route()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).TriggerAsync("a/b c", 10);

        Assert.EndsWith("/BillBatch/a%2Fb%20c", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_the_same_id_and_body()
    {
        var handler = new StubHandler((_, n) => new HttpResponseMessage(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        await Gateway(handler).TriggerAsync("batch-1", 10);

        Assert.Equal(3, handler.Calls.Count);
        Assert.Single(handler.Calls.Select(c => c.Request.RequestUri!.ToString()).Distinct());
        Assert.Single(handler.Calls.Select(c => c.Body).Distinct());
    }

    [Fact]
    public async Task Gives_up_after_the_maximum_attempts()
    {
        var handler = StubHandler.Status(HttpStatusCode.TooManyRequests);

        var e = await Assert.ThrowsAsync<SkailGatewayException>(() => Gateway(handler).TriggerAsync("b", 10));

        Assert.Equal(SkailApiGateway.MaxAttempts, handler.Calls.Count);
        Assert.Contains("giving up", e.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Unauthorized_is_not_retried_and_points_at_the_key_and_namespace(HttpStatusCode status)
    {
        var handler = StubHandler.Status(status);

        var e = await Assert.ThrowsAsync<SkailGatewayException>(() => Gateway(handler).TriggerAsync("b", 10));

        Assert.Single(handler.Calls);
        Assert.Contains("SKAIL_KEY", e.Message);
    }

    [Fact]
    public async Task Missing_base_url_or_a_malformed_workload_is_reported()
    {
        var noUrl = await Assert.ThrowsAsync<SkailGatewayException>(() =>
            Gateway(StubHandler.Status(HttpStatusCode.OK), new SkailOptions { Workload = "w:v1" }).TriggerAsync("b", 10));
        Assert.Contains("SKAIL_BASE_URL", noUrl.Message);

        var badWorkload = await Assert.ThrowsAsync<SkailGatewayException>(() =>
            Gateway(StubHandler.Status(HttpStatusCode.OK), new SkailOptions { BaseUrl = "https://x", Workload = "oops" }).TriggerAsync("b", 10));
        Assert.Contains("SKAIL_WORKLOAD", badWorkload.Message);
    }
}
