using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailBatchBilling.Demo;

namespace SkailBatchBilling;

public class BillingFunctions(SimulatedCityHall cityHall, BillingDesk desk)
{
    /// <summary>
    /// Issues the invoices of the batch in waves of 10: one child run per invoice, and the next wave starts when the
    /// current one ends, so the city hall never gets more than 10 requests at once. When every invoice is done, it
    /// consolidates the result. The name and the parameters are the trigger contract: ["{batchId}", {size}].
    /// </summary>
    [SkailFunction]
    public async SkailTask<BatchResult> BillBatch(string batchId, int size)
    {
        var orders = Orders.Generate(batchId, size); // only arithmetic over the arguments, so it is deterministic

        var invoices = new List<Invoice>();
        foreach (var wave in orders.Chunk(BatchLimits.WaveSize))
        {
            // fan-out: one child run per invoice, all created before the wait
            var issuing = wave.Select(order => IssueInvoice(batchId, size, order)).ToArray();

            // fan-in: a rejected invoice is a result, not an exception, so the batch goes on
            await SkailTask.WhenAll(issuing);

            // the results are read in the order the invoices were created, not in the order they finished
            foreach (var invoice in issuing)
                invoices.Add(await invoice);
        }

        var result = BatchResult.Consolidate(batchId, invoices);
        await ReportResult(batchId, size, result, invoices);
        return result;
    }

    /// <summary>
    /// One invoice, as a child run with its own history. An invoice that the city hall could not issue after every
    /// attempt becomes a pending invoice, so the batch always completes.
    /// </summary>
    [SkailFunction]
    public async SkailTask<Invoice> IssueInvoice(string batchId, int size, Order order)
    {
        Invoice invoice;
        try
        {
            invoice = await IssueAtCityHall(batchId, size, order);
        }
        catch (Exception ex) when (ex is not SkailNonDeterministicException)
        {
            invoice = Invoice.Pending(order, "city hall unavailable");
        }

        if (invoice.IsAuthorized)
            await SendPdfToCustomer(batchId, size, invoice); // only an authorized invoice reaches the customer
        else
            await NotifyFinance(batchId, size, invoice);     // rejected or pending: finance resolves it

        return invoice;
    }

    // The commands are the I/O, one operation each. A retry runs the whole command again, so every command is
    // idempotent: the order id at the city hall, the invoice number in the inboxes.

    /// <summary>A rejection is a result; an outage throws, so skail retries only this invoice, up to 5 times.</summary>
    [SkailCommand(retryCount: 5)]
    public async SkailTask<Invoice> IssueAtCityHall(string batchId, int size, Order order) =>
        await cityHall.IssueAsync(batchId, size, order);

    [SkailCommand(retryCount: 3)]
    public async SkailTask SendPdfToCustomer(string batchId, int size, Invoice invoice)
    {
        desk.PdfSent(batchId, size, invoice);
        await Task.CompletedTask;
    }

    [SkailCommand(retryCount: 3)]
    public async SkailTask NotifyFinance(string batchId, int size, Invoice invoice)
    {
        desk.FinanceNotified(batchId, size, invoice);
        await Task.CompletedTask;
    }

    [SkailCommand]
    public async SkailTask ReportResult(string batchId, int size, BatchResult result, List<Invoice> invoices)
    {
        desk.Completed(batchId, size, result, invoices);
        await Task.CompletedTask;
    }
}
