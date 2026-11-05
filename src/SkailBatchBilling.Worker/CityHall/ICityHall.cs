using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Worker.CityHall;

/// <summary>Technical failure (city hall system down): the command throws it and skail retries.</summary>
public sealed class CityHallUnavailableException() : Exception("city hall unavailable");

public interface ICityHall
{
    /// <summary>
    /// Issues the invoice. A rejection or a pending invoice is a <b>result</b>; only an outage throws.
    /// <see cref="Order.Id"/> is the idempotency key: the same order always yields the same invoice.
    /// </summary>
    Task<Invoice> IssueAsync(Order order, CancellationToken ct = default);
}
