using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Tests;

public class ContractsTests
{
    [Fact]
    public void Orders_are_generated_deterministically_from_the_arguments()
    {
        var a = Orders.Generate("batch-1", 10);
        var b = Orders.Generate("batch-1", 10);

        Assert.Equal(a, b);
        Assert.Equal(Enumerable.Range(1, 10), a.Select(p => p.Number));
        Assert.Equal("batch-1-3", a[2].Id);
        Assert.All(a, p => Assert.True(p.Amount > 0));
    }

    [Theory]
    [InlineData(InvoiceStatus.Authorized, true)]
    [InlineData(InvoiceStatus.Rejected, false)]
    [InlineData(InvoiceStatus.Pending, false)]
    public void Only_an_invoice_with_status_Authorized_is_authorized(InvoiceStatus status, bool expected)
    {
        var invoice = new Invoice(1, "Customer 001", 100m, status, null, null, null);

        Assert.Equal(expected, invoice.IsAuthorized);
    }

    [Fact]
    public void Pending_factory_builds_a_pending_invoice_with_the_reason()
    {
        var order = new Order("l-4", 4, "Customer 004", 250m);

        var invoice = Invoice.Pending(order, "city hall unavailable");

        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Equal("city hall unavailable", invoice.Reason);
        Assert.Equal((4, 250m), (invoice.Number, invoice.Amount));
    }

    [Fact]
    public void Consolidate_counts_by_status_and_bills_only_authorized_invoices()
    {
        var invoices = new[]
        {
            new Invoice(1, "a", 100m, InvoiceStatus.Authorized, "NF-1", "P1", null),
            new Invoice(2, "b", 50m, InvoiceStatus.Authorized, "NF-2", "P2", null),
            new Invoice(3, "c", 70m, InvoiceStatus.Rejected, null, null, "CNPJ inválido"),
            new Invoice(4, "d", 30m, InvoiceStatus.Pending, null, null, "fora do ar"),
        };

        var result = BatchResult.Consolidate("l", invoices);

        Assert.Equal(("l", 4, 2, 1, 1, 150m), (result.BatchId, result.Total, result.Authorized, result.Rejected, result.Pending, result.AmountBilled));
    }

    [Fact]
    public void Batch_sizes_are_10_20_and_50_with_20_as_default()
    {
        Assert.Equal(new[] { 10, 20, 50 }, BatchLimits.Sizes);
        Assert.Equal(20, BatchLimits.DefaultSize);
        Assert.Equal(50, BatchLimits.MaxSize);
    }

    [Fact]
    public void BatchEvent_round_trips_through_the_shared_json_options()
    {
        var invoice = new Invoice(2, "b", 50m, InvoiceStatus.Rejected, null, null, "CNPJ");
        var evt = BatchEvent.InvoiceDone("l", 20, invoice);

        var json = System.Text.Json.JsonSerializer.Serialize(evt, Json.Options);

        Assert.Equal(evt, System.Text.Json.JsonSerializer.Deserialize<BatchEvent>(json, Json.Options));
        Assert.Contains("\"Rejected\"", json);
    }
}
