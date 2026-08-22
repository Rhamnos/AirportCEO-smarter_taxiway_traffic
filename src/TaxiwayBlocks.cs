using System.Collections.Generic;
using HarmonyLib;
using Nodes;
using UnityEngine;

namespace AdvancedTaxiways
{
    /// <summary>
    /// Single-lane segments of taxiway, derived from the graph rather than authored.
    ///
    /// A real ground controller clears an aircraft segment by segment — "via Alpha, hold
    /// short of Charlie" — and the hold points are painted on the ground. The game has no
    /// equivalent: nothing marks a stretch of taxiway as a corridor. But the adjacency
    /// graph already knows, because it is the same graph A* walks:
    ///
    ///   degree 1  dead end
    ///   degree 2  single-lane through node
    ///   degree 3+ junction
    ///
    /// That works because adjacency is 8-connected at 4 m cardinal and 6 m diagonal
    /// (TaxiwayNode.cs:114-160). A one-tile-wide taxiway has no diagonal neighbours, so
    /// its nodes come out at degree 2; a two-wide one immediately picks up a side
    /// neighbour AND a diagonal, so degree 3 or more. The distinction falls out for free.
    ///
    /// A block is a maximal run of degree-&lt;=2 nodes bounded by junctions or dead ends —
    /// the stretch between two hold-short points. Stored in walk order so an aircraft's
    /// direction through it is just whether its index rises or falls.
    /// </summary>
    internal static class TaxiwayBlocks
    {
        private static readonly Dictionary<TaxiwayNode, int> blockOf = new Dictionary<TaxiwayNode, int>();
        private static readonly Dictionary<TaxiwayNode, int> orderInBlock = new Dictionary<TaxiwayNode, int>();
        private static readonly List<List<TaxiwayNode>> blocks = new List<List<TaxiwayNode>>();

        private static readonly HashSet<TaxiwayNode> visited = new HashSet<TaxiwayNode>();
        private static readonly List<TaxiwayNode> run = new List<TaxiwayNode>();

        private static bool dirty = true;
        private static float readyAt;

        public static int Count => blocks.Count;

        /// <summary>Nodes in a block, for sizing how long it takes to traverse.</summary>
        public static int Length(int blockId)
        {
            EnsureBuilt();
            return blockId >= 0 && blockId < blocks.Count ? blocks[blockId].Count : 0;
        }

        /// <summary>
        /// Marks the derived data stale and schedules a rebuild after a quiet period.
        ///
        /// The maps are cleared immediately rather than left stale, because a demolished
        /// node stays referenced by a cached block — TaxiwayNode is a plain class, so a
        /// removed one is a dangling reference rather than a null. Running with no blocks
        /// is safe: the pairwise head-on pass skips nodes a block already owns, so with no
        /// blocks it simply covers everything itself.
        ///
        /// Time.unscaledTime, not Time.time. This is debouncing a human dragging out a
        /// taxiway, which has nothing to do with how fast the simulation is running.
        /// </summary>
        public static void Invalidate()
        {
            dirty = true;
            readyAt = Time.unscaledTime + Plugin.RebuildDelay.Value;

            blockOf.Clear();
            orderInBlock.Clear();
            blocks.Clear();
        }

        /// <summary>Block id for a node, or -1 if it is a junction or not in a corridor.</summary>
        public static int BlockAt(TaxiwayNode node)
        {
            if (node == null) return -1;
            EnsureBuilt();
            return blockOf.TryGetValue(node, out int id) ? id : -1;
        }

        /// <summary>Position of a node along its block, in walk order.</summary>
        public static int OrderAt(TaxiwayNode node)
        {
            if (node == null) return -1;
            EnsureBuilt();
            return orderInBlock.TryGetValue(node, out int i) ? i : -1;
        }

        public static List<TaxiwayNode> Nodes(int blockId)
        {
            EnsureBuilt();
            return blockId >= 0 && blockId < blocks.Count ? blocks[blockId] : null;
        }

        private static TaxiwayController controller;

        public static void EnsureBuilt()
        {
            if (!dirty) return;

            // NOT Singleton<TaxiwayController>.Instance. Singleton.Initiate latches
            // initialized = true even when FindObjectOfType returned null, because
            // debugLog stays false until that singleton's own Awake has run — so one
            // access before the object exists pins Instance to null for the whole
            // session, for every consumer in the game. See the MODDING-NOTES entry; this
            // mod hung startup that way once already.
            if (controller == null) controller = UnityEngine.Object.FindObjectOfType<TaxiwayController>();

            var all = controller == null ? null : controller.Taxiways;

            // Stay dirty until there is actually a graph to read. Clearing the flag first
            // meant a single call made before the world was populated left blocks
            // permanently empty, and the whole feature silently off, unless something
            // happened to invalidate later.
            if (all == null || all.Count == 0) return;

            dirty = false;

            visited.Clear();

            for (int i = 0; i < all.Count; i++)
            {
                var seed = all[i];
                if (seed == null || visited.Contains(seed)) continue;
                if (!IsCorridor(seed)) continue;

                // Walk to one end of the run, then walk back across it so the stored order
                // is a genuine traversal rather than discovery order.
                var end = WalkToEnd(seed);
                CollectFrom(end);

                if (run.Count < Plugin.MinBlockNodes.Value) continue;

                int id = blocks.Count;
                var nodes = new List<TaxiwayNode>(run);
                blocks.Add(nodes);
                for (int k = 0; k < nodes.Count; k++)
                {
                    blockOf[nodes[k]] = id;
                    orderInBlock[nodes[k]] = k;
                }
            }

            LogFingerprint(all);
        }

        /// <summary>
        /// A one-line description of the layout's shape. Every counter this mod reports is
        /// conditional on topology — a corridor-heavy airport and a wide-taxiway one put
        /// completely different parts of the system under load, and an airport with no
        /// single-lane runs at all leaves the whole block layer inert. Without this in the
        /// log there is no way to tell afterwards which regime a number came from.
        /// </summary>
        private static void LogFingerprint(System.Collections.Generic.List<TaxiwayNode> all)
        {
            int deadEnds = 0, corridor = 0, junctions = 0, planned = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null) continue;
                if (!all[i].activated) { planned++; continue; }

                int degree = ActiveDegree(all[i]);
                if (degree <= 1) deadEnds++;
                else if (degree == 2) corridor++;
                else junctions++;
            }

            int shortest = int.MaxValue, longest = 0, total = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                int n = blocks[i].Count;
                total += n;
                if (n < shortest) shortest = n;
                if (n > longest) longest = n;
            }
            if (blocks.Count == 0) shortest = 0;

            float mean = blocks.Count == 0 ? 0f : (float)total / blocks.Count;
            float corridorShare = all.Count == 0 ? 0f : 100f * corridor / all.Count;

            // Branches ending in a degree-1 node with nothing on them. Stand and hangar
            // connectors are normally a single node off the lane, so anything longer is
            // usually taxiway left behind when something was demolished. Harmless to
            // traffic — nothing routes into them — but every edge in a stub is a bridge
            // and a block, so they inflate those counts and make the airport look more
            // tree-like than it is.
            int stubs = 0, longestStub = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                var run = blocks[i];
                bool deadEnd = ActiveDegree(run[0]) <= 1 || ActiveDegree(run[run.Count - 1]) <= 1;
                if (!deadEnd || run.Count < 2) continue;
                stubs++;
                if (run.Count > longestStub) longestStub = run.Count;
            }

            Plugin.Log.LogInfo(
                $"Layout: {all.Count} nodes ({planned} planned/unbuilt, {deadEnds} dead-end, {corridor} single-lane, {junctions} junction, "
                + $"{corridorShare:F0}% corridor) -> {blocks.Count} block(s), "
                + $"length min {shortest} / mean {mean:F1} / max {longest}; "
                + $"{stubs} dead-end branch(es) of 2+ nodes, longest {longestStub}.");

            if (blocks.Count == 0)
            {
                Plugin.Log.LogWarning(
                    "No single-lane blocks on this layout, so block exclusion does nothing here and "
                    + "head-on prevention rests entirely on the loop guard. That combination is untested.");
            }
        }

        /// <summary>
        /// Built taxiway only. Nodes drawn in planning mode but not yet constructed sit in
        /// the same list and in each other's neighbour lists — `SearchForNode` returns
        /// whatever is at a position and never checks `activated`. A* does check it
        /// (PathFindingAircraft.cs:73), so aircraft never route over planned taxiway, and a
        /// structural view that counts it diverges from where aircraft can actually go.
        ///
        /// Both errors run the wrong way. A planned spur touching a real corridor node
        /// raises its degree to 3, so a genuine single-lane stretch stops being a block and
        /// loses its exclusion; a planned second connector makes a real bottleneck look like
        /// it has an alternative. Each makes the mod *less* careful exactly where capacity
        /// has been drawn but not built.
        /// </summary>
        private static bool Usable(TaxiwayNode node) => node != null && node.activated;

        private static int ActiveDegree(TaxiwayNode node)
        {
            var adjacent = node.adjacentTaxiwayNodes;
            if (adjacent == null) return 0;

            int n = 0;
            for (int i = 0; i < adjacent.Count; i++)
            {
                if (Usable(adjacent[i])) n++;
            }
            return n;
        }

        private static bool IsCorridor(TaxiwayNode node)
        {
            if (!Usable(node)) return false;
            int degree = ActiveDegree(node);
            return degree > 0 && degree <= 2;
        }

        private static TaxiwayNode WalkToEnd(TaxiwayNode from)
        {
            var current = from;
            TaxiwayNode previous = null;
            var seen = new HashSet<TaxiwayNode> { current };

            while (true)
            {
                TaxiwayNode next = null;
                var adjacent = current.adjacentTaxiwayNodes;
                for (int i = 0; i < adjacent.Count; i++)
                {
                    var candidate = adjacent[i];
                    if (!Usable(candidate) || candidate == previous) continue;
                    if (!IsCorridor(candidate) || seen.Contains(candidate)) continue;
                    next = candidate;
                    break;
                }
                if (next == null) return current;
                seen.Add(next);
                previous = current;
                current = next;
            }
        }

        private static void CollectFrom(TaxiwayNode start)
        {
            run.Clear();
            var current = start;
            TaxiwayNode previous = null;

            while (current != null)
            {
                run.Add(current);
                visited.Add(current);

                TaxiwayNode next = null;
                var adjacent = current.adjacentTaxiwayNodes;
                for (int i = 0; i < adjacent.Count; i++)
                {
                    var candidate = adjacent[i];
                    if (!Usable(candidate) || candidate == previous) continue;
                    if (!IsCorridor(candidate) || visited.Contains(candidate)) continue;
                    next = candidate;
                    break;
                }
                previous = current;
                current = next;
            }
        }
    }

    /// <summary>
    /// Blocks and bridges are derived from the adjacency graph, so they have to be
    /// invalidated wherever that graph changes. Marking dirty rather than recomputing keeps
    /// a burst of edits — dragging out a taxiway — to a single rebuild.
    ///
    /// <c>UpdateAllTaxiwayNodes</c> covers building and demolishing: `TaxiwayNodeModel`
    /// calls it from both `UpdateBuilders` and `Remove`, and stands, runways and runway
    /// entrances all call it too.
    ///
    /// It does **not** cover the one-way toggle. `SetOneWay` and `SetCurrentOneWay`
    /// (TaxiwayNodeModel.cs:330-348) call `UpdateOneWayStatus` and then
    /// `UpdateAllRunwayConnections` — never `UpdateAllTaxiwayNodes`. That path edits
    /// adjacency on one node only, and since bridges are computed from *mutual* adjacency
    /// it is precisely the edit that changes the answer. Hence the two node-level hooks
    /// below, which between them catch every adjacency change whatever caused it.
    /// </summary>
    internal static class GraphInvalidation
    {
        internal static void Mark()
        {
            TaxiwayBlocks.Invalidate();
            TaxiwayBridges.Invalidate();
        }
    }

    [HarmonyPatch(typeof(TaxiwayController), nameof(TaxiwayController.UpdateAllTaxiwayNodes))]
    internal static class BlockRebuildPatch
    {
        [HarmonyPostfix]
        private static void Postfix() => GraphInvalidation.Mark();
    }

    /// <summary>Any single node rebuilding its neighbours — the catch-all.</summary>
    [HarmonyPatch(typeof(TaxiwayNode), nameof(TaxiwayNode.UpdateTaxiwayNode))]
    internal static class NodeRebuildPatch
    {
        [HarmonyPostfix]
        private static void Postfix() => GraphInvalidation.Mark();
    }

    /// <summary>
    /// One-way edge removal. Runs *after* UpdateTaxiwayNode in `UpdateOneWayStatus`, so the
    /// earlier hook would have marked the graph dirty before these edges were taken out.
    /// </summary>
    [HarmonyPatch(typeof(TaxiwayNode), nameof(TaxiwayNode.SetOneWayValues), new[] { typeof(int) })]
    internal static class OneWayPatch
    {
        [HarmonyPostfix]
        private static void Postfix() => GraphInvalidation.Mark();
    }
}
