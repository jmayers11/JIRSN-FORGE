using Forge.Quality.Contracts;

namespace Forge.Quality.Investigations;

public sealed record TraceTraversal(IReadOnlySet<string> Nodes,IReadOnlyList<TraceGraphLink> Edges);

/// <summary>Complete recorded forward traversal with cycle protection; no depth or candidate display limit.</summary>
public static class TraceGraphEngine
{
    public static TraceTraversal Forward(IReadOnlyList<string> roots,IReadOnlyList<TraceGraphLink> edges)
    {
        var outgoing=edges.ToLookup(edge=>edge.From,StringComparer.Ordinal);
        var nodes=new HashSet<string>(StringComparer.Ordinal);
        var queue=new Queue<string>();
        foreach(var root in roots.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            if(nodes.Add(root))queue.Enqueue(root);
        var traversed=new List<TraceGraphLink>();
        while(queue.TryDequeue(out var from))
            foreach(var edge in outgoing[from].OrderBy(edge=>edge.To,StringComparer.Ordinal).ThenBy(edge=>edge.Evidence.Id,StringComparer.Ordinal))
            {
                traversed.Add(edge);
                if(nodes.Add(edge.To))queue.Enqueue(edge.To);
            }
        return new(nodes,traversed);
    }
}
