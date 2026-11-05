using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Worker.CityHall;

/// <summary>Business answer of the simulated city hall, deterministic per order.</summary>
internal static class CityHallRules
{
    public static Invoice Decide(Order p)
    {
        if (p.Number % 5 == 0)
            return new(p.Number, p.Customer, p.Amount, InvoiceStatus.Rejected, null, null, "invalid customer tax id (CNPJ)");
        if (p.Number % 7 == 0)
            return new(p.Number, p.Customer, p.Amount, InvoiceStatus.Pending, null, null, "waiting for the city hall to process it");

        return new(p.Number, p.Customer, p.Amount, InvoiceStatus.Authorized, $"NF-{p.Number:000000}", $"PROT-{Hash8(p.Id)}", null);
    }

    // FNV-1a: a short hash that is always the same for the same order id
    private static string Hash8(string text)
    {
        var h = 0x811c9dc5u;
        foreach (var c in text) h = (h ^ c) * 0x01000193u;
        return h.ToString("X8");
    }
}
