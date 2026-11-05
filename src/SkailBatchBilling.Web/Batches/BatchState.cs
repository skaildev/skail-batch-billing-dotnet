using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Web.Batches;

public enum BatchPhase
{
    Starting,
    Issuing,
    Completed,
    Failed,
}

public enum InvoiceState
{
    Issuing,
    Authorized,
    Rejected,
    Pending,
}

public sealed record InvoiceView(int Number, string Customer, decimal Amount, InvoiceState State, int Attempts, string? Reason, string? InvoiceNumber);

/// <summary>A PDF that reached the customer (only authorized invoices).</summary>
public sealed record CustomerMail(int Number, string Customer, decimal Amount, string? InvoiceNumber, string? Protocol, string FileName);

/// <summary>A ticket that reached finance (only rejected or pending invoices).</summary>
public sealed record FinanceTicket(int Number, string Customer, decimal Amount, InvoiceStatus Status, string? Reason);

public sealed record BatchState(
    string BatchId,
    int Size,
    BatchPhase Phase,
    IReadOnlyList<InvoiceView> Invoices,
    BatchResult? Result,
    string? Error,
    IReadOnlyList<CustomerMail> CustomerInbox,
    IReadOnlyList<FinanceTicket> FinanceInbox)
{
    public bool IsTerminal => Phase is BatchPhase.Completed or BatchPhase.Failed;
    public bool Active => !IsTerminal;
    public int Done => Invoices.Count(n => n.State != InvoiceState.Issuing);
}
