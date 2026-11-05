using System.Text.Json.Serialization;

namespace SkailBatchBilling.Contracts;

public static class BatchLimits
{
    public static readonly IReadOnlyList<int> Sizes = [10, 20, 50];
    public const int DefaultSize = 20;
    public const int MaxSize = 50;

    /// <summary>Invoices issued at the same time. The batch runs in waves of this size, so the city hall is not flooded.</summary>
    public const int WaveSize = 10;
}

/// <summary>Order to be invoiced. <see cref="Id"/> is also the idempotency key at the city hall.</summary>
public sealed record Order(string Id, int Number, string Customer, decimal Amount);

public static class Orders
{
    /// <summary>Pure arithmetic over the arguments, so it is safe to call from a function (deterministic).</summary>
    public static Order[] Generate(string batchId, int size) =>
        [.. Enumerable.Range(1, size).Select(n =>
            new Order($"{batchId}-{n}", n, $"Customer {n:000}", 100m + (n * 37 % 900) + (n * 13 % 100) / 100m))];
}

public enum InvoiceStatus
{
    Authorized,
    Rejected,
    Pending,
}

public sealed record Invoice(
    int Number,
    string Customer,
    decimal Amount,
    InvoiceStatus Status,
    string? InvoiceNumber,
    string? Protocol,
    string? Reason)
{
    [JsonIgnore]
    public bool IsAuthorized => Status == InvoiceStatus.Authorized;

    public static Invoice Pending(Order order, string reason) =>
        new(order.Number, order.Customer, order.Amount, InvoiceStatus.Pending, null, null, reason);
}

public sealed record BatchResult(string BatchId, int Total, int Authorized, int Rejected, int Pending, decimal AmountBilled)
{
    public static BatchResult Consolidate(string batchId, IReadOnlyCollection<Invoice> invoices) =>
        new(batchId,
            invoices.Count,
            invoices.Count(n => n.Status == InvoiceStatus.Authorized),
            invoices.Count(n => n.Status == InvoiceStatus.Rejected),
            invoices.Count(n => n.Status == InvoiceStatus.Pending),
            invoices.Where(n => n.IsAuthorized).Sum(n => n.Amount));
}

public enum BatchStage
{
    Started,
    Retrying,
    PdfSent,
    FinanceNotified,
    InvoiceDone,
    Completed,
    Failed,
}

/// <summary>Progress sent by the worker to the web app (ReportStatus command and city hall retries).</summary>
public sealed record BatchEvent(
    string BatchId,
    BatchStage Stage,
    int Total,
    int Number = 0,
    Invoice? Invoice = null,
    BatchResult? Result = null,
    string? Error = null)
{
    public static BatchEvent Started(string batchId, int total) => new(batchId, BatchStage.Started, total);
    public static BatchEvent Retrying(string batchId, int total, int number, string error) => new(batchId, BatchStage.Retrying, total, number, Error: error);
    public static BatchEvent InvoiceDone(string batchId, int total, Invoice invoice) => new(batchId, BatchStage.InvoiceDone, total, invoice.Number, invoice);
    /// <summary>The customer received the PDF of an authorized invoice.</summary>
    public static BatchEvent PdfSent(string batchId, int total, Invoice invoice) => new(batchId, BatchStage.PdfSent, total, invoice.Number, invoice);
    /// <summary>Finance received a ticket for a rejected or pending invoice.</summary>
    public static BatchEvent FinanceNotified(string batchId, int total, Invoice invoice) => new(batchId, BatchStage.FinanceNotified, total, invoice.Number, invoice);
    public static BatchEvent Completed(string batchId, BatchResult result) => new(batchId, BatchStage.Completed, result.Total, Result: result);
    public static BatchEvent Failed(string batchId, int total, string error) => new(batchId, BatchStage.Failed, total, Error: error);
}
