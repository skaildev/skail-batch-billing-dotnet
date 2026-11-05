using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Worker.CityHall;

/// <summary>Default city hall: random outages and a little latency. Randomness is fine here: it is I/O inside a command.</summary>
public sealed class SimulatedCityHall(int outagePercent, Func<double>? random = null, bool latency = true) : ICityHall
{
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    public async Task<Invoice> IssueAsync(Order order, CancellationToken ct = default)
    {
        if (latency) await Task.Delay(Random.Shared.Next(200, 800), ct);
        if (_random() * 100 < outagePercent) throw new CityHallUnavailableException();
        return CityHallRules.Decide(order);
    }
}
