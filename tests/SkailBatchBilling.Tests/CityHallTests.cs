using SkailBatchBilling.Contracts;
using SkailBatchBilling.Worker.CityHall;

namespace SkailBatchBilling.Tests;

public class CityHallTests
{
    private static Order Order(int number) => new($"l-{number}", number, $"Customer {number:000}", 100m);

    [Theory]
    [InlineData(1, InvoiceStatus.Authorized)]
    [InlineData(5, InvoiceStatus.Rejected)]
    [InlineData(10, InvoiceStatus.Rejected)]
    [InlineData(7, InvoiceStatus.Pending)]
    [InlineData(14, InvoiceStatus.Pending)]
    [InlineData(35, InvoiceStatus.Rejected)] // multiple of 5 and 7: rejection wins
    public async Task Rules_reject_multiples_of_5_and_leave_multiples_of_7_pending(int number, InvoiceStatus expected)
    {
        var cityHall = new SimulatedCityHall(outagePercent: 0, random: () => 0.99, latency: false);

        var invoice = await cityHall.IssueAsync(Order(number));

        Assert.Equal(expected, invoice.Status);
        Assert.Equal(expected == InvoiceStatus.Authorized, invoice.InvoiceNumber is not null && invoice.Protocol is not null);
        Assert.Equal(expected != InvoiceStatus.Authorized, invoice.Reason is not null);
    }

    [Fact]
    public async Task Authorized_invoice_is_identified_by_the_order_so_a_retry_gets_the_same_numbers()
    {
        var cityHall = new SimulatedCityHall(0, () => 0.99, false);

        var first = await cityHall.IssueAsync(Order(3));
        var again = await cityHall.IssueAsync(Order(3));

        Assert.Equal(first, again);
    }

    [Fact]
    public async Task Outage_happens_when_the_draw_is_below_the_failure_percent()
    {
        var cityHall = new SimulatedCityHall(outagePercent: 25, random: () => 0.24, latency: false);

        await Assert.ThrowsAsync<CityHallUnavailableException>(() => cityHall.IssueAsync(Order(1)));
    }

    [Fact]
    public async Task No_outage_when_the_draw_is_at_or_above_the_failure_percent()
    {
        var cityHall = new SimulatedCityHall(outagePercent: 25, random: () => 0.25, latency: false);

        Assert.Equal(InvoiceStatus.Authorized, (await cityHall.IssueAsync(Order(1))).Status);
    }

    [Theory]
    [InlineData(0, 0.0, false)]
    [InlineData(100, 0.999, true)]
    public async Task Failure_percent_extremes(int percent, double draw, bool fails)
    {
        var cityHall = new SimulatedCityHall(percent, () => draw, false);

        if (fails) await Assert.ThrowsAsync<CityHallUnavailableException>(() => cityHall.IssueAsync(Order(1)));
        else Assert.NotNull(await cityHall.IssueAsync(Order(1)));
    }

    [Fact]
    public async Task Fake_fails_only_the_first_attempts_of_each_order()
    {
        var cityHall = new FakeCityHall(failFirstAttempts: 2);

        await Assert.ThrowsAsync<CityHallUnavailableException>(() => cityHall.IssueAsync(Order(1)));
        await Assert.ThrowsAsync<CityHallUnavailableException>(() => cityHall.IssueAsync(Order(1)));
        Assert.Equal(InvoiceStatus.Authorized, (await cityHall.IssueAsync(Order(1))).Status);
        // other orders have their own counter
        await Assert.ThrowsAsync<CityHallUnavailableException>(() => cityHall.IssueAsync(Order(2)));
    }
}
