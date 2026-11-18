namespace deltalag;


/// <summary>
/// Container for the graph structure, using a compressed sparse row (CSR) format for edges.
/// </summary>
internal class Graph
{
    internal int NodeCount;
    internal readonly List<int> edges = new();
    internal readonly List<int> offsets = new();
}
