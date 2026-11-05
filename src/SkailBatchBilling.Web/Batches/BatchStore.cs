using System.Threading.Channels;
using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Web.Batches;

/// <summary>
/// In-memory state of the batches. The durable state lives in skail; this is only the UI view.
/// Events come from many child executions, so their order is not guaranteed: the most advanced state wins.
/// </summary>
public sealed class BatchStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BatchState> _batches = [];
    private readonly Dictionary<string, List<Channel<BatchState>>> _subscribers = [];

    public BatchState Create(string batchId, int size)
    {
        lock (_gate)
        {
            var invoices = Orders.Generate(batchId, size)
                .Select(p => new InvoiceView(p.Number, p.Customer, p.Amount, InvoiceState.Issuing, 1, null, null))
                .ToList();
            var batch = new BatchState(batchId, size, BatchPhase.Starting, invoices, null, null, [], []);
            _batches[batchId] = batch;
            return batch;
        }
    }

    public BatchState? Get(string batchId)
    {
        lock (_gate) return _batches.GetValueOrDefault(batchId);
    }

    public void Remove(string batchId)
    {
        lock (_gate)
        {
            _batches.Remove(batchId);
            Complete(batchId);
        }
    }

    /// <returns>true when the event changed the batch; false when ignored (unknown or finished batch, duplicate).</returns>
    public bool Apply(BatchEvent evt)
    {
        lock (_gate)
        {
            if (!_batches.TryGetValue(evt.BatchId, out var batch) || batch.IsTerminal) return false;

            var next = evt.Stage switch
            {
                BatchStage.Started => batch.Phase == BatchPhase.Starting ? batch with { Phase = BatchPhase.Issuing } : null,
                BatchStage.Retrying => WithInvoice(batch, evt.Number, n => n.State == InvoiceState.Issuing ? n with { Attempts = n.Attempts + 1, Reason = evt.Error } : null),
                BatchStage.InvoiceDone when evt.Invoice is { } invoice => WithInvoice(batch, invoice.Number, n => n.State == InvoiceState.Issuing
                    ? n with { State = StateOf(invoice.Status), Reason = invoice.Reason, InvoiceNumber = invoice.InvoiceNumber }
                    : null),
                BatchStage.PdfSent when evt.Invoice is { IsAuthorized: true } invoice && batch.CustomerInbox.All(m => m.Number != invoice.Number) =>
                    batch with { CustomerInbox = [.. batch.CustomerInbox, new CustomerMail(invoice.Number, invoice.Customer, invoice.Amount, invoice.InvoiceNumber, invoice.Protocol, $"{invoice.InvoiceNumber}.pdf")] },
                BatchStage.FinanceNotified when evt.Invoice is { IsAuthorized: false } invoice && batch.FinanceInbox.All(t => t.Number != invoice.Number) =>
                    batch with { FinanceInbox = [.. batch.FinanceInbox, new FinanceTicket(invoice.Number, invoice.Customer, invoice.Amount, invoice.Status, invoice.Reason)] },
                BatchStage.Completed when evt.Result is { } r => batch with { Phase = BatchPhase.Completed, Result = r },
                BatchStage.Failed => batch with { Phase = BatchPhase.Failed, Error = evt.Error ?? "unknown error" },
                _ => null,
            };
            if (next is null) return false;

            Publish(next with { Phase = next.Phase == BatchPhase.Starting ? BatchPhase.Issuing : next.Phase });
            return true;
        }
    }

    public Subscription Subscribe(string batchId)
    {
        var channel = Channel.CreateUnbounded<BatchState>();
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(batchId, out var list)) _subscribers[batchId] = list = [];
            list.Add(channel);
        }
        return new Subscription(channel.Reader, () =>
        {
            lock (_gate)
            {
                if (_subscribers.TryGetValue(batchId, out var list)) list.Remove(channel);
            }
        });
    }

    private static InvoiceState StateOf(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Authorized => InvoiceState.Authorized,
        InvoiceStatus.Rejected => InvoiceState.Rejected,
        _ => InvoiceState.Pending,
    };

    private static BatchState? WithInvoice(BatchState batch, int number, Func<InvoiceView, InvoiceView?> change)
    {
        var index = batch.Invoices.ToList().FindIndex(n => n.Number == number);
        if (index < 0) return null;
        if (change(batch.Invoices[index]) is not { } updated) return null;

        var invoices = batch.Invoices.ToList();
        invoices[index] = updated;
        return batch with { Invoices = invoices };
    }

    // must be called with _gate held
    private void Publish(BatchState state)
    {
        _batches[state.BatchId] = state;
        if (_subscribers.TryGetValue(state.BatchId, out var list))
            foreach (var channel in list) channel.Writer.TryWrite(state);
        if (state.IsTerminal) Complete(state.BatchId);
    }

    private void Complete(string batchId)
    {
        if (!_subscribers.TryGetValue(batchId, out var list)) return;
        foreach (var channel in list) channel.Writer.TryComplete();
    }

    public sealed class Subscription(ChannelReader<BatchState> reader, Action onDispose) : IDisposable
    {
        public ChannelReader<BatchState> Reader { get; } = reader;
        public void Dispose() => onDispose();
    }
}
