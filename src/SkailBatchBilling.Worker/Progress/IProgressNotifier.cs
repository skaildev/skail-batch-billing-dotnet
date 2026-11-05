using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Worker.Progress;

public interface IProgressNotifier
{
    Task NotifyAsync(BatchEvent evt, CancellationToken ct = default);
}
