using System.Collections.Concurrent;

namespace AtmMachine.WebUI.Realtime;

public sealed record RealtimeEnvelope(string GroupName, string EventName, object Payload);

public sealed class BankingRealtimeQueue
{
    private readonly ConcurrentQueue<RealtimeEnvelope> _queue = new();

    public void EnqueueUser(Guid userId, string eventName, object payload)
    {
        _queue.Enqueue(new RealtimeEnvelope(BankingHub.UserGroup(userId), eventName, payload));
    }

    public void EnqueueAdmins(string eventName, object payload)
    {
        _queue.Enqueue(new RealtimeEnvelope(BankingHub.AdminGroupName, eventName, payload));
    }

    public List<RealtimeEnvelope> Drain(int maxItems)
    {
        List<RealtimeEnvelope> items = new(capacity: Math.Max(1, maxItems));
        while (items.Count < maxItems && _queue.TryDequeue(out RealtimeEnvelope? item))
        {
            items.Add(item);
        }

        return items;
    }
}
