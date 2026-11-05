using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Worker;

public class BillingFunctions(BillingCommands commands)
{
    /// <summary>
    /// Issues the invoices of the batch in waves of 10: one child execution per invoice, and the next wave starts
    /// when the current one ends, so the city hall never gets more than 10 requests at once. When every invoice is
    /// done, it consolidates the result.
    /// </summary>
    [SkailFunction]
    public async SkailTask<BatchResult> BillBatch(string batchId, int size)
    {
        try
        {
            var orders = Orders.Generate(batchId, size);   // only arithmetic over the arguments, so it is deterministic
            await commands.ReportStatus(BatchEvent.Started(batchId, size));

            var invoices = new List<Invoice>();
            foreach (var wave in orders.Chunk(BatchLimits.WaveSize))
            {
                // Fan-out: one child execution per invoice, all created before the wait.
                var issuing = wave.Select(order => IssueInvoice(batchId, size, order)).ToArray();

                // Fan-in: a rejected invoice is a result, not an exception, so the batch goes on.
                await SkailTask.WhenAll(issuing);

                // The results are read in the order the invoices were created, not in the order they finished.
                foreach (var invoice in issuing)
                    invoices.Add(await invoice);
            }

            var result = BatchResult.Consolidate(batchId, invoices);
            await commands.ReportStatus(BatchEvent.Completed(batchId, result));
            return result;
        }
        catch (Exception ex) when (ex is not SkailNonDeterministicException)
        {
            await commands.ReportStatus(BatchEvent.Failed(batchId, size, ex.Message));
            throw;
        }
    }

    /// <summary>
    /// One invoice, as a child execution with its own history. An invoice that the city hall could not issue after
    /// every attempt becomes a pending invoice, so the batch always completes.
    /// </summary>
    [SkailFunction]
    public async SkailTask<Invoice> IssueInvoice(string batchId, int size, Order order)
    {
        Invoice invoice;
        try
        {
            invoice = await commands.IssueAtCityHall(batchId, size, order);
        }
        catch (Exception ex) when (ex is not SkailNonDeterministicException)
        {
            invoice = Invoice.Pending(order, "city hall unavailable");
        }

        if (invoice.IsAuthorized)
            await commands.SendPdfToCustomer(batchId, size, invoice);   // only an authorized invoice reaches the customer
        else
            await commands.NotifyFinance(batchId, size, invoice);       // rejected or pending: finance resolves it

        await commands.ReportStatus(BatchEvent.InvoiceDone(batchId, size, invoice));
        return invoice;
    }
}
