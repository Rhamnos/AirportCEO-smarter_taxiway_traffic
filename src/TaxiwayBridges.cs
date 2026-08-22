using System.Collections.Generic;
using Nodes;
using UnityEngine;

namespace AdvancedTaxiways
{
    /// <summary>
    /// Bottlenecks a centralised ground controller would know about: stretches of taxiway
    /// that are the *only* way between one part of the airport and another.
    ///
    /// Blocks answer a local question — "is it narrow here?" — by node degree. This answers
    /// a global one: remove this connection and does the airport fall into two pieces? In
    /// graph terms that is a **bridge** (cut edge), found by Tarjan's algorithm in one pass.
    ///
    /// The distinction matters because it is what has been biting. A ramp connector built
    /// of degree-3 junction nodes is not a block, but it is a bridge — nothing can route
    /// around it, so a head-on there is unavoidable rather than unlucky. Meanwhile a ramp
    /// with two connectors produces no bridge at all, because an alternative exists.
    ///
    /// **Only mutual edges count.** One-way removes a neighbour from one node's list and
    /// not the other's (TaxiwayNode.cs:164-167, and SetOneWayValues), so the graph is
    /// genuinely directed where the player has used one-ways. A one-way link cannot produce
    /// a head-on, so counting it would report bottlenecks that are not. A bridge here means
    /// "the only two-way link", which is the thing worth being strict about.
    /// </summary>
    internal static class TaxiwayBridges
    {
        private static readonly Dictionary<TaxiwayNode, int> index = new Dictionary<TaxiwayNode, int>();
        private static readonly HashSet<long> bridges = new HashSet<long>();
        private static readonly Dictionary<long, int> culDeSac = new Dictionary<long, int>();

        private static TaxiwayController controller;
        private static bool dirty = true;
        private static float readyAt;

        public static int Count => bridges.Count;

        /// <summary>See TaxiwayBlocks.Invalidate — same debounce, same reason.</summary>
        public static void Invalidate()
        {
            dirty = true;
            readyAt = Time.unscaledTime + Plugin.RebuildDelay.Value;

            index.Clear();
            bridges.Clear();
            culDeSac.Clear();
        }

        /// <summary>Is the step between these two nodes the only two-way link across it?</summary>
        public static bool IsBridge(TaxiwayNode a, TaxiwayNode b)
        {
            if (a == null || b == null) return false;
            EnsureBuilt();
            return index.TryGetValue(a, out int ia) && index.TryGetValue(b, out int ib)
                   && bridges.Contains(Key(ia, ib));
        }

        /// <summary>Nodes on the smaller side of this bridge — the size of the cul-de-sac it serves.</summary>
        public static int CulDeSacSize(TaxiwayNode a, TaxiwayNode b)
        {
            if (a == null || b == null) return 0;
            EnsureBuilt();
            if (!index.TryGetValue(a, out int ia) || !index.TryGetValue(b, out int ib)) return 0;
            return culDeSac.TryGetValue(Key(ia, ib), out int size) ? size : 0;
        }

        private static long Key(int a, int b)
        {
            int lo = a < b ? a : b, hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        public static void EnsureBuilt()
        {
            if (!dirty) return;
            if (Time.unscaledTime < readyAt) return;   // let a burst of edits settle first

            // Not Singleton<TaxiwayController>.Instance — an access before the object exists
            // latches it to null for the whole session. See the MODDING-NOTES entry.
            if (controller == null) controller = Object.FindObjectOfType<TaxiwayController>();
            var all = controller == null ? null : controller.Taxiways;
            if (all == null || all.Count == 0) return;   // stay dirty until there is a graph

            dirty = false;

            // Built taxiway only. Nodes drawn in planning mode sit in the same list and in
            // each other's neighbour lists — SearchForNode never checks `activated`. A* does
            // (PathFindingAircraft.cs:73), so counting planned taxiway here would report a
            // bottleneck as having an alternative route that aircraft cannot actually use.
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].activated && !index.ContainsKey(all[i])) index[all[i]] = index.Count;
            }

            int n = index.Count;
            var adjacency = BuildMutualAdjacency(all, n, out int mutualEdges, out int onewayEdges);
            FindBridges(adjacency, n);
            LogFingerprint(all.Count, n, mutualEdges, onewayEdges, adjacency);
        }

        private static List<int>[] BuildMutualAdjacency(List<TaxiwayNode> all, int n, out int mutual, out int oneway)
        {
            var adjacency = new List<int>[n];
            for (int i = 0; i < n; i++) adjacency[i] = new List<int>();
            mutual = 0;
            oneway = 0;

            foreach (var pair in index)
            {
                var node = pair.Key;
                var neighbours = node.adjacentTaxiwayNodes;
                if (neighbours == null) continue;

                for (int i = 0; i < neighbours.Count; i++)
                {
                    var other = neighbours[i];
                    if (other == null || !other.activated) continue;          // planned, not built
                    if (!index.TryGetValue(other, out int j)) continue;

                    bool reciprocal = other.adjacentTaxiwayNodes != null
                                      && other.adjacentTaxiwayNodes.Contains(node);
                    if (!reciprocal) { oneway++; continue; }

                    if (pair.Value < j)
                    {
                        mutual++;
                        adjacency[pair.Value].Add(j);
                        adjacency[j].Add(pair.Value);
                    }
                }
            }
            return adjacency;
        }

        /// <summary>
        /// Tarjan, iteratively. Recursion would nest as deep as the longest taxiway chain,
        /// and subtree sizes come out of the same walk, which is what gives each bridge the
        /// size of the region behind it.
        /// </summary>
        private static void FindBridges(List<int>[] adjacency, int n)
        {
            var disc = new int[n];
            var low = new int[n];
            var size = new int[n];
            for (int i = 0; i < n; i++) { disc[i] = -1; size[i] = 1; }

            var stackNode = new int[n + 1];
            var stackParent = new int[n + 1];
            var stackNext = new int[n + 1];
            var found = new List<long>();
            var foundSub = new List<int>();

            int timer = 0;
            for (int root = 0; root < n; root++)
            {
                if (disc[root] != -1) continue;

                int componentStart = timer;
                found.Clear();
                foundSub.Clear();

                int top = 0;
                stackNode[0] = root; stackParent[0] = -1; stackNext[0] = 0;
                disc[root] = low[root] = timer++;

                while (top >= 0)
                {
                    int u = stackNode[top];
                    var neighbours = adjacency[u];

                    if (stackNext[top] < neighbours.Count)
                    {
                        int v = neighbours[stackNext[top]++];
                        if (v == stackParent[top]) continue;

                        if (disc[v] == -1)
                        {
                            disc[v] = low[v] = timer++;
                            top++;
                            stackNode[top] = v; stackParent[top] = u; stackNext[top] = 0;
                        }
                        else if (disc[v] < low[u])
                        {
                            low[u] = disc[v];
                        }
                    }
                    else
                    {
                        int parent = stackParent[top];
                        top--;
                        if (parent < 0) continue;

                        if (low[u] < low[parent]) low[parent] = low[u];
                        size[parent] += size[u];

                        if (low[u] > disc[parent])
                        {
                            found.Add(Key(parent, u));
                            foundSub.Add(size[u]);
                        }
                    }
                }

                int componentSize = timer - componentStart;
                for (int i = 0; i < found.Count; i++)
                {
                    bridges.Add(found[i]);
                    int behind = foundSub[i];
                    culDeSac[found[i]] = Mathf.Min(behind, componentSize - behind);
                }
            }
        }

        private static void LogFingerprint(int rawNodes, int n, int mutual, int oneway, List<int>[] adjacency)
        {
            int components = 0, isolated = 0;
            var seen = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (adjacency[i].Count == 0) { isolated++; continue; }
                if (seen[i]) continue;
                components++;
                var queue = new Queue<int>();
                queue.Enqueue(i); seen[i] = true;
                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    for (int k = 0; k < adjacency[u].Count; k++)
                    {
                        int v = adjacency[u][k];
                        if (!seen[v]) { seen[v] = true; queue.Enqueue(v); }
                    }
                }
            }

            // Bucketed, not a top-six list. Along a chain of bridges each successive one
            // has one fewer node behind it, so a raw list reads "168, 167, 166, 165..." —
            // six numbers describing one dead-end corridor rather than six areas.
            int tiny = 0, small = 0, medium = 0, large = 0;
            foreach (int size in culDeSac.Values)
            {
                if (size <= 3) tiny++;
                else if (size <= 20) small++;
                else if (size <= 80) medium++;
                else large++;
            }
            string biggest = $"{tiny} stand spurs (<=3), {small} small (<=20), {medium} ramp-sized (<=80), {large} large (>80)";

            Plugin.Log.LogInfo(
                $"Bottlenecks: {rawNodes} nodes, {mutual} two-way links, {oneway} one-way, "
                + $"{components} two-way region(s), {isolated} node(s) reachable only one-way -> "
                + $"{bridges.Count} bridge(s). Largest areas served by a single two-way link: {biggest}.");

            if (bridges.Count == 0)
            {
                Plugin.Log.LogInfo(
                    "No two-way bottlenecks on this layout — every area has an alternative route or is "
                    + "one-way throughout, so no head-on is structurally unavoidable.");
            }
        }
    }
}
