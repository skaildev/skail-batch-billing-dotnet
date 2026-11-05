using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Worker;

/// <summary>
/// The I/O of the functions, one operation per command. A retry runs the whole command again, so every command is
/// idempotent. The commands delegate to <see cref="BillingOperations"/>, so their logic is tested without skail.
/// </summary>
public class BillingCommands(BillingOperations operations)
{
    /// <summary>A rejection is a result; an outage throws, so skail retries only this invoice.</summary>
    [SkailCommand(retryCount: 5)]
    public async SkailTask<Invoice> IssueAtCityHall(string batchId, int size, Order order)
        => await operations.IssueAsync(batchId, size, order);

    [SkailCommand(retryCount: 3)]
    public async SkailTask SendPdfToCustomer(string batchId, int size, Invoice invoice)
        => await operations.SendPdfAsync(batchId, size, invoice);

    [SkailCommand(retryCount: 3)]
    public async SkailTask NotifyFinance(string batchId, int size, Invoice invoice)
        => await operations.NotifyFinanceAsync(batchId, size, invoice);

    /// <summary>Idempotent: the web app ignores a repeated event (same batch, stage and invoice).</summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask ReportStatus(BatchEvent evt)
        => await operations.ReportStatusAsync(evt);
}
