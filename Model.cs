namespace SkailBatchBilling;

public static class BatchLimits
{
    public static readonly IReadOnlyList<int> Sizes = [10, 20, 50];

    /// <summary>Invoices issued at the same time: the batch runs in waves of this size, not to flood the city hall.</summary>
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
    string? Reason)
{
    public bool IsAuthorized => Status == InvoiceStatus.Authorized;

    public static Invoice Pending(Order order, string reason) =>
        new(order.Number, order.Customer, order.Amount, InvoiceStatus.Pending, null, reason);
}

public sealed record BatchResult(string BatchId, int Total, int Authorized, int Rejected, int Pending, decimal AmountBilled)
{
    public static BatchResult Consolidate(string batchId, IReadOnlyCollection<Invoice> invoices) =>
        new(batchId,
            invoices.Count,
            invoices.Count(i => i.Status == InvoiceStatus.Authorized),
            invoices.Count(i => i.Status == InvoiceStatus.Rejected),
            invoices.Count(i => i.Status == InvoiceStatus.Pending),
            invoices.Where(i => i.IsAuthorized).Sum(i => i.Amount));
}
