namespace revolver;


// Almost DoD representation of a graph, with separate lists for nodes, edges, and offsets (CSR format).
// list<node> because nodes have tasks and mutable state.
/// <summary>
/// Container for the graph structure, using a compressed sparse row (CSR) format for edges.
/// </summary>
public class Graph
{
    public readonly List<Node> nodes = new();
    public readonly List<int> edges = new();
    public readonly List<int> offsets = new();
}
