using System.Globalization;
using SkailBatchBilling.Skail;

namespace SkailBatchBilling.Demo;

/// <summary>
/// Starts the batches for the page and keeps what it shows: each invoice with its attempts at the city hall, the
/// customer's mailbox and the finance inbox. A batch the app does not know (it was started before the app restarted)
/// shows up with its next report, and the final report fills in the invoices issued before the restart.
/// </summary>
public sealed class BillingDesk(SkailApiClient skail)
{
    private sealed class Row(Order order)
    {
        public Order Order { get; } = order;
        public int Attempts { get; set; }
        public bool Retrying { get; set; }
        public Invoice? Invoice { get; set; }
    }

    private sealed class Batch(string id, int size)
    {
        public string Id { get; } = id;
        public int Size { get; } = size;
        public Row[] Rows { get; } = [.. Orders.Generate(id, size).Select(o => new Row(o))];
        public List<string> CustomerInbox { get; } = [];
        public List<string> FinanceInbox { get; } = [];
        public BatchResult? Result { get; set; }
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Batch> batches = [];
    private Batch? current;

    public async Task StartAsync(int size, CancellationToken ct)
    {
        var batchId = Guid.NewGuid().ToString();
        await skail.TriggerAsync(batchId, size, ct);
        lock (gate) current = batches[batchId] = new Batch(batchId, size);
        Print(batchId, $"batch started: {size} invoices, in waves of {BatchLimits.WaveSize}");
    }

    public void Attempt(string batchId, int size, int number, bool down)
    {
        lock (gate)
        {
            var row = Find(batchId, size).Rows[number - 1];
            row.Attempts++;
            row.Retrying = down;
        }
        if (down) Print(batchId, $"#{number}: city hall unavailable, skail will try again");
    }

    public void PdfSent(string batchId, int size, Invoice invoice) =>
        Done(batchId, size, invoice, b => b.CustomerInbox, $"{invoice.InvoiceNumber}.pdf → {invoice.Customer}");

    public void FinanceNotified(string batchId, int size, Invoice invoice) =>
        Done(batchId, size, invoice, b => b.FinanceInbox,
            $"#{invoice.Number} {invoice.Customer}: {invoice.Status.ToString().ToLowerInvariant()}, {invoice.Reason}");

    public void Completed(string batchId, int size, BatchResult result, IReadOnlyList<Invoice> invoices)
    {
        lock (gate)
        {
            var batch = Find(batchId, size);
            foreach (var invoice in invoices) batch.Rows[invoice.Number - 1].Invoice ??= invoice;
            batch.Result = result;
        }
        Print(batchId, string.Create(CultureInfo.InvariantCulture,
            $"batch completed: {result.Authorized} authorized, {result.Rejected} rejected, {result.Pending} pending, " +
            $"R$ {result.AmountBilled:N2} billed"));
    }

    /// <summary>What the page shows: the latest batch.</summary>
    public object? View()
    {
        lock (gate)
        {
            if (current is not { } b) return null;
            return new
            {
                b.Id, b.Size, b.Result,
                done = b.Rows.Count(r => r.Invoice is not null),
                invoices = b.Rows.Select(r => new
                {
                    r.Order.Number, r.Order.Customer, r.Order.Amount, r.Attempts,
                    status = r.Invoice?.Status.ToString()
                        ?? (r.Retrying ? "Retrying" : r.Attempts > 0 ? "Issuing" : "Waiting"),
                    r.Invoice?.InvoiceNumber, r.Invoice?.Reason,
                }),
                customerInbox = b.CustomerInbox,
                financeInbox = b.FinanceInbox,
            };
        }
    }

    // a retried command reports again: the invoice number keeps it to one line in each inbox
    private void Done(string batchId, int size, Invoice invoice, Func<Batch, List<string>> inbox, string line)
    {
        lock (gate)
        {
            var batch = Find(batchId, size);
            var row = batch.Rows[invoice.Number - 1];
            if (row.Invoice is not null) return;
            row.Invoice = invoice;
            row.Retrying = false;
            inbox(batch).Add(line);
        }
        Print(batchId, line);
    }

    // a batch started before the app restarted: the page follows it from its next report
    private Batch Find(string batchId, int size)
    {
        if (!batches.TryGetValue(batchId, out var batch))
            current = batches[batchId] = batch = new Batch(batchId, size);
        return batch;
    }

    private static void Print(string batchId, string message) => Console.WriteLine($"[{batchId[..8]}] {message}");
}
