namespace SkailBatchBilling.Web.Skail;

public sealed class SkailGatewayException(string message, Exception? inner = null) : Exception(message, inner);

public interface ISkailGateway
{
    /// <summary>Starts the BillBatch function; <paramref name="batchId"/> is the execution id.</summary>
    Task TriggerAsync(string batchId, int size, CancellationToken ct = default);
}
