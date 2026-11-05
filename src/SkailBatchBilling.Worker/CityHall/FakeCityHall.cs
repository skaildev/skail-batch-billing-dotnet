using System.Collections.Concurrent;
using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Worker.CityHall;

/// <summary>Offline and deterministic: fails only the first <paramref name="failFirstAttempts"/> attempts of each order.</summary>
public sealed class FakeCityHall(int failFirstAttempts = 0) : ICityHall
{
    private readonly ConcurrentDictionary<string, int> _attempts = new();

    public Task<Invoice> IssueAsync(Order order, CancellationToken ct = default)
    {
        var attempt = _attempts.AddOrUpdate(order.Id, 1, (_, n) => n + 1);
        if (attempt <= failFirstAttempts) throw new CityHallUnavailableException();
        return Task.FromResult(CityHallRules.Decide(order));
    }
}
