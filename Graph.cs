namespace deltalag;


/// <summary>
/// Container for the graph structure, using a compressed sparse row (CSR) format for edges.
/// </summary>
public class Graph
{
    public int NodeCount;
    public readonly List<int> edges = new();
    public readonly List<int> offsets = new();
}
