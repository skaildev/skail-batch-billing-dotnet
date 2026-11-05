using Microsoft.Extensions.Logging;
using SkailBatchBilling.Contracts;
using SkailBatchBilling.Worker.CityHall;
using SkailBatchBilling.Worker.Progress;

namespace SkailBatchBilling.Worker;

/// <summary>
/// The I/O behind the commands, as plain tasks. Commands only run inside the skail runtime, so keeping the
/// logic here lets it be unit tested without skail.
/// </summary>
public class BillingOperations(ICityHall cityHall, IProgressNotifier notifier, ILogger<BillingOperations> log)
{
    /// <summary>
    /// An outage throws, so skail retries only this invoice. The page is told about the retry on a best-effort basis:
    /// if that fails, the outage is still the error the command throws.
    /// </summary>
    public async Task<Invoice> IssueAsync(string batchId, int size, Order order)
    {
        try
        {
            return await cityHall.IssueAsync(order);
        }
        catch (CityHallUnavailableException e)
        {
            log.LogWarning("City hall unavailable for invoice {Number}: retrying", order.Number);
            try { await notifier.NotifyAsync(BatchEvent.Retrying(batchId, size, order.Number, e.Message)); }
            catch (Exception) { /* only a hint for the page */ }
            throw;
        }
    }

    /// <summary>
    /// The web app plays the finance team's inbox: the delivery is the event it receives. If it cannot be
    /// delivered the command throws and skail retries it.
    /// </summary>
    public async Task NotifyFinanceAsync(string batchId, int size, Invoice invoice)
    {
        if (invoice.IsAuthorized)
            throw new InvalidOperationException($"invoice {invoice.Number} is authorized: finance has nothing to resolve");

        log.LogInformation("Finance notified: invoice {Number} {Status}, {Reason}", invoice.Number, invoice.Status, invoice.Reason);
        await notifier.NotifyAsync(BatchEvent.FinanceNotified(batchId, size, invoice));
    }

    /// <summary>The web app plays the customer's mailbox: the PDF "arrives" as the event it receives.</summary>
    public async Task SendPdfAsync(string batchId, int size, Invoice invoice)
    {
        if (!invoice.IsAuthorized)
            throw new InvalidOperationException($"invoice {invoice.Number} is not authorized: the PDF cannot be sent");

        log.LogInformation("PDF sent: {InvoiceNumber} to {Customer}", invoice.InvoiceNumber, invoice.Customer);
        await notifier.NotifyAsync(BatchEvent.PdfSent(batchId, size, invoice));
    }

    public Task ReportStatusAsync(BatchEvent evt) => notifier.NotifyAsync(evt);
}
