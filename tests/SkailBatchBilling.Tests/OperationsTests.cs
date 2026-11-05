using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SkailBatchBilling.Contracts;
using SkailBatchBilling.Worker;
using SkailBatchBilling.Worker.CityHall;
using SkailBatchBilling.Worker.Progress;

namespace SkailBatchBilling.Tests;

public sealed class RecordingNotifier : IProgressNotifier
{
    public List<BatchEvent> Events { get; } = [];
    public Exception? FailWith { get; set; }

    public Task NotifyAsync(BatchEvent evt, CancellationToken ct = default)
    {
        if (FailWith is not null) throw FailWith;
        Events.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>Commands need the skail runtime; the logic they delegate to is tested here.</summary>
public class OperationsTests
{
    private static readonly Order P5 = new("l-5", 5, "Customer 005", 100m);

    private static BillingOperations Commands(ICityHall cityHall, RecordingNotifier? notifier = null) =>
        new(cityHall, notifier ?? new RecordingNotifier(), NullLogger<BillingOperations>.Instance);

    [Fact]
    public async Task Rejection_is_returned_as_a_result_not_thrown()
    {
        var invoice = await Commands(new FakeCityHall()).IssueAsync("l", 20, P5);

        Assert.Equal(InvoiceStatus.Rejected, invoice.Status);
    }

    [Fact]
    public async Task Technical_outage_is_rethrown_so_skail_retries_and_reported_as_a_retry_event()
    {
        var notifier = new RecordingNotifier();
        var commands = Commands(new FakeCityHall(failFirstAttempts: 1), notifier);

        await Assert.ThrowsAsync<CityHallUnavailableException>(() => commands.IssueAsync("l", 20, P5));

        var evt = Assert.Single(notifier.Events);
        Assert.Equal((BatchStage.Retrying, 5, "l"), (evt.Stage, evt.Number, evt.BatchId));
    }

    [Fact]
    public async Task A_failing_retry_notification_does_not_hide_the_outage()
    {
        var notifier = new RecordingNotifier { FailWith = new HttpRequestException("web down") };
        var commands = Commands(new FakeCityHall(failFirstAttempts: 1), notifier);

        await Assert.ThrowsAsync<CityHallUnavailableException>(() => commands.IssueAsync("l", 20, P5));
    }

    [Fact]
    public async Task ReportStatus_forwards_the_event_to_the_notifier()
    {
        var notifier = new RecordingNotifier();
        var evt = BatchEvent.Started("l", 20);

        await Commands(new FakeCityHall(), notifier).ReportStatusAsync(evt);

        Assert.Equal(evt, Assert.Single(notifier.Events));
    }

    [Fact]
    public async Task Finance_warning_delivers_a_ticket_for_a_rejected_invoice()
    {
        var notifier = new RecordingNotifier();
        var invoice = new Invoice(5, "Customer 005", 100m, InvoiceStatus.Rejected, null, null, "CNPJ");

        await Commands(new FakeCityHall(), notifier).NotifyFinanceAsync("l", 20, invoice);

        Assert.Equal(BatchEvent.FinanceNotified("l", 20, invoice), Assert.Single(notifier.Events));
    }

    [Fact]
    public async Task Pdf_delivery_reaches_the_customer_for_an_authorized_invoice()
    {
        var notifier = new RecordingNotifier();
        var invoice = new Invoice(1, "Customer 001", 100m, InvoiceStatus.Authorized, "NF-000001", "P", null);

        await Commands(new FakeCityHall(), notifier).SendPdfAsync("l", 20, invoice);

        Assert.Equal(BatchEvent.PdfSent("l", 20, invoice), Assert.Single(notifier.Events));
    }

    [Fact]
    public async Task A_failed_delivery_throws_so_skail_retries_the_command()
    {
        var notifier = new RecordingNotifier { FailWith = new HttpRequestException("web down") };
        var invoice = new Invoice(1, "c", 100m, InvoiceStatus.Authorized, "NF-1", "P", null);

        await Assert.ThrowsAsync<HttpRequestException>(() => Commands(new FakeCityHall(), notifier).SendPdfAsync("l", 20, invoice));
    }

    [Fact]
    public async Task Finance_is_not_warned_about_an_authorized_invoice()
    {
        var invoice = new Invoice(1, "c", 100m, InvoiceStatus.Authorized, "NF-1", "P", null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Commands(new FakeCityHall()).NotifyFinanceAsync("l", 20, invoice));
    }

    [Fact]
    public async Task SendPdfToCustomer_refuses_an_invoice_that_is_not_authorized()
    {
        var invoice = new Invoice(5, "c", 100m, InvoiceStatus.Pending, null, null, "x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Commands(new FakeCityHall()).SendPdfAsync("l", 20, invoice));
    }
}

public class NotifierTests
{
    private static HttpProgressNotifier Notifier(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://web.test:8080/") });

    [Fact]
    public async Task Posts_the_event_to_the_internal_endpoint_of_the_batch()
    {
        var handler = StubHandler.Status(HttpStatusCode.NoContent);
        var evt = BatchEvent.InvoiceDone("batch-7", 10, new Invoice(2, "c", 50m, InvoiceStatus.Authorized, "NF-2", "P", null));

        await Notifier(handler).NotifyAsync(evt);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://web.test:8080/internal/batches/batch-7/events", request.RequestUri!.ToString());
        Assert.Equal(evt, JsonSerializer.Deserialize<BatchEvent>(body, Json.Options));
    }

    [Fact]
    public async Task Throws_on_failure_so_the_command_is_retried()
    {
        var notifier = Notifier(StubHandler.Status(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HttpRequestException>(() => notifier.NotifyAsync(BatchEvent.Started("b", 10)));
    }
}
