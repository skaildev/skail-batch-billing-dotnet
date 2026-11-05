using SkailBatchBilling.Contracts;
using SkailBatchBilling.Web.Batches;

namespace SkailBatchBilling.Tests;

public class BatchStoreTests
{
    private static Invoice Invoice(int n, InvoiceStatus s = InvoiceStatus.Authorized) =>
        new(n, $"Customer {n:000}", 100m, s, s == InvoiceStatus.Authorized ? $"NF-{n}" : null, null, s == InvoiceStatus.Authorized ? null : "reason");

    [Fact]
    public void Create_starts_with_one_issuing_row_per_invoice()
    {
        var store = new BatchStore();

        var batch = store.Create("b", 10);

        Assert.Equal(BatchPhase.Starting, batch.Phase);
        Assert.Equal(10, batch.Invoices.Count);
        Assert.All(batch.Invoices, n => Assert.Equal((InvoiceState.Issuing, 1), (n.State, n.Attempts)));
        Assert.True(batch.Active);
    }

    [Fact]
    public void Started_moves_to_issuing_and_a_duplicate_is_ignored()
    {
        var store = new BatchStore();
        store.Create("b", 10);

        Assert.True(store.Apply(BatchEvent.Started("b", 10)));
        Assert.False(store.Apply(BatchEvent.Started("b", 10)));
        Assert.Equal(BatchPhase.Issuing, store.Get("b")!.Phase);
    }

    [Fact]
    public void Each_retry_adds_an_attempt_to_that_invoice_only()
    {
        var store = new BatchStore();
        store.Create("b", 10);

        store.Apply(BatchEvent.Retrying("b", 10, 3, "fora do ar"));
        store.Apply(BatchEvent.Retrying("b", 10, 3, "fora do ar"));

        var invoices = store.Get("b")!.Invoices;
        Assert.Equal(3, invoices[2].Attempts);
        Assert.All(invoices.Where(n => n.Number != 3), n => Assert.Equal(1, n.Attempts));
    }

    [Theory]
    [InlineData(InvoiceStatus.Authorized, InvoiceState.Authorized)]
    [InlineData(InvoiceStatus.Rejected, InvoiceState.Rejected)]
    [InlineData(InvoiceStatus.Pending, InvoiceState.Pending)]
    public void InvoiceDone_sets_the_final_state_of_the_row(InvoiceStatus status, InvoiceState expected)
    {
        var store = new BatchStore();
        store.Create("b", 10);

        store.Apply(BatchEvent.InvoiceDone("b", 10, Invoice(2, status)));

        var row = store.Get("b")!.Invoices[1];
        Assert.Equal(expected, row.State);
        Assert.Equal(1, store.Get("b")!.Done);
    }

    [Fact]
    public void Duplicate_InvoiceDone_and_late_retry_do_not_change_a_finished_invoice()
    {
        var store = new BatchStore();
        store.Create("b", 10);
        store.Apply(BatchEvent.InvoiceDone("b", 10, Invoice(2)));

        Assert.False(store.Apply(BatchEvent.InvoiceDone("b", 10, Invoice(2, InvoiceStatus.Rejected))));
        Assert.False(store.Apply(BatchEvent.Retrying("b", 10, 2, "late")));

        var row = store.Get("b")!.Invoices[1];
        Assert.Equal((InvoiceState.Authorized, 1), (row.State, row.Attempts));
    }

    [Fact]
    public void An_event_that_arrives_before_Started_already_moves_the_batch_to_issuing()
    {
        var store = new BatchStore();
        store.Create("b", 10);

        store.Apply(BatchEvent.InvoiceDone("b", 10, Invoice(1)));

        Assert.Equal(BatchPhase.Issuing, store.Get("b")!.Phase);
    }

    [Fact]
    public void Completed_is_terminal_keeps_the_result_and_ignores_later_events()
    {
        var store = new BatchStore();
        store.Create("b", 10);
        var result = new BatchResult("b", 10, 7, 2, 1, 700m);

        store.Apply(BatchEvent.Completed("b", result));

        var batch = store.Get("b")!;
        Assert.Equal((BatchPhase.Completed, result, false), (batch.Phase, batch.Result, batch.Active));
        Assert.False(store.Apply(BatchEvent.Failed("b", 10, "x")));
    }

    [Fact]
    public void Failed_is_terminal_with_the_error()
    {
        var store = new BatchStore();
        store.Create("b", 10);

        store.Apply(BatchEvent.Failed("b", 10, "boom"));

        Assert.Equal((BatchPhase.Failed, "boom"), (store.Get("b")!.Phase, store.Get("b")!.Error));
    }

    [Fact]
    public void Events_for_unknown_batches_or_unknown_invoices_are_ignored()
    {
        var store = new BatchStore();
        store.Create("b", 10);

        Assert.False(store.Apply(BatchEvent.Started("other", 10)));
        Assert.False(store.Apply(BatchEvent.InvoiceDone("b", 10, Invoice(99))));
    }

    [Fact]
    public async Task Subscribers_receive_each_change_and_the_stream_completes_when_terminal()
    {
        var store = new BatchStore();
        store.Create("b", 10);
        using var sub = store.Subscribe("b");

        store.Apply(BatchEvent.Started("b", 10));
        store.Apply(BatchEvent.Completed("b", new BatchResult("b", 10, 10, 0, 0, 1m)));

        var phases = new List<BatchPhase>();
        await foreach (var s in sub.Reader.ReadAllAsync()) phases.Add(s.Phase);
        Assert.Equal(new[] { BatchPhase.Issuing, BatchPhase.Completed }, phases);
    }

    [Fact]
    public void Customer_inbox_receives_only_authorized_invoices_and_finance_inbox_only_the_others()
    {
        var store = new BatchStore();
        store.Create("b", 10);
        var ok = Invoice(1);
        var rejected = Invoice(5, InvoiceStatus.Rejected);
        var pending = Invoice(7, InvoiceStatus.Pending);

        store.Apply(BatchEvent.PdfSent("b", 10, ok));
        store.Apply(BatchEvent.FinanceNotified("b", 10, rejected));
        store.Apply(BatchEvent.FinanceNotified("b", 10, pending));

        var batch = store.Get("b")!;
        Assert.Equal(new[] { 1 }, batch.CustomerInbox.Select(m => m.Number));
        Assert.Equal("NF-1.pdf", batch.CustomerInbox[0].FileName);
        Assert.Equal(new[] { 5, 7 }, batch.FinanceInbox.Select(t => t.Number));
        Assert.Equal(new[] { InvoiceStatus.Rejected, InvoiceStatus.Pending }, batch.FinanceInbox.Select(t => t.Status));
    }

    [Fact]
    public void An_invoice_is_never_delivered_to_the_wrong_inbox_nor_twice()
    {
        var store = new BatchStore();
        store.Create("b", 10);

        Assert.False(store.Apply(BatchEvent.PdfSent("b", 10, Invoice(5, InvoiceStatus.Rejected))));
        Assert.False(store.Apply(BatchEvent.FinanceNotified("b", 10, Invoice(1))));
        Assert.True(store.Apply(BatchEvent.PdfSent("b", 10, Invoice(1))));
        Assert.False(store.Apply(BatchEvent.PdfSent("b", 10, Invoice(1))));   // retried command: same delivery

        var batch = store.Get("b")!;
        Assert.Single(batch.CustomerInbox);
        Assert.Empty(batch.FinanceInbox);
    }
}
