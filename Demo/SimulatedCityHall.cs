namespace SkailBatchBilling.Demo;

/// <summary>Technical failure (the city hall system is down): the command throws it and skail retries.</summary>
public sealed class CityHallUnavailableException() : Exception("city hall unavailable");

/// <summary>
/// The city hall that issues the service invoices, simulated: a little latency, and unavailable on about 1 call in
/// 10. Its answer is the same for the same order, so a retry gets the same invoice: orders whose number is a multiple
/// of 5 are rejected, multiples of 7 stay pending, and the rest are authorized.
/// </summary>
public sealed class SimulatedCityHall(BillingDesk desk)
{
    private const int OutagePercent = 10;

    public async Task<Invoice> IssueAsync(string batchId, int size, Order order)
    {
        await Task.Delay(Random.Shared.Next(200, 800));
        var down = Random.Shared.Next(100) < OutagePercent;
        desk.Attempt(batchId, size, order.Number, down);
        if (down) throw new CityHallUnavailableException();

        if (order.Number % 5 == 0)
            return new(order.Number, order.Customer, order.Amount, InvoiceStatus.Rejected, null, "invalid customer tax id");
        if (order.Number % 7 == 0)
            return new(order.Number, order.Customer, order.Amount, InvoiceStatus.Pending, null, "waiting for the city hall");
        return new(order.Number, order.Customer, order.Amount, InvoiceStatus.Authorized, $"NF-{order.Number:000000}", null);
    }
}
