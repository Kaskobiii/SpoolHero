using System.Collections.Concurrent;

namespace SpoolManager.Server.Services;

// Counts live Moonraker sockets; connection URLs and secrets are never retained here.
public class SpoolmanConnections
{
    private readonly ConcurrentDictionary<Guid, int> _connections = new();
    public void Add(Guid projectId) => _connections.AddOrUpdate(projectId, 1, (_, value) => value + 1);
    public void Remove(Guid projectId) => _connections.AddOrUpdate(projectId, 0, (_, value) => Math.Max(0, value - 1));
    public int Count(Guid projectId) => _connections.GetValueOrDefault(projectId);
}
