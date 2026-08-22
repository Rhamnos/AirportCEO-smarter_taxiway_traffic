using System;
using System.Collections.Generic;
using HarmonyLib;
using Nodes;
using UnityEngine;

namespace AdvancedTaxiways
{
    /// <summary>
    /// Lets a taxiing aircraft take its turn back from a blocker that is nowhere near
    /// the node they are arguing over, and settles the case where both reach a node
    /// together.
    ///
    /// Vanilla has the first half, at AircraftController.cs:2016-2027, and it barely
    /// works. <c>TaxiwayNode.SortUp</c> only swaps entries already ADJACENT in the
    /// queue, and the caller always passes the head — so it fires only when exactly one
    /// aircraft is ahead of you. Third in line and it silently does nothing, which is
    /// the busy intersection where you need it. It also reorders a single node, leaving
    /// that node's queue disagreeing with every other node the pair shares.
    ///
    /// This runs outside the movement coroutine, so it needs no transpiler against a
    /// compiler-generated iterator: reorder the queue and the coroutine finds
    /// <c>IsTravelable</c> true on its next tick.
    ///
    /// Four rules keep it safe. See the README for the reasoning behind each.
    ///
    /// 1. Distance is measured from the BLOCKER to the contested node, not between the
    ///    two aircraft, and the mover must be nearer that node than the blocker — the
    ///    same test the game uses for its runway skip at AircraftController.cs:2038.
    /// 2. The reorder is applied to EVERY node the pair shares or to none of them, and
    ///    is refused outright if any shared node lies inside the blocker's committed
    ///    window. Partial reorders are what make queues disagree, and a queue that
    ///    disagrees with its neighbour is a deadlock.
    /// 3. Two tiers are never overtaken by anybody: an emergency, and an aircraft still
    ///    standing on runway ground. Without this a departure could overtake an arrival
    ///    that has just vacated and re-create the exact stall the deferred-claim patch
    ///    exists to fix.
    /// 4. A pair that has just been reordered will not be reordered back for a cooldown.
    ///    Two aircraft approaching a double crossing from opposite ends each qualify at
    ///    their own contested node, and without this they flip every sweep.
    ///
    /// Pairwise consistency rules out two aircraft deadlocking each other, but NOT
    /// three. Precedence starts as a total order by claim time, which is transitive;
    /// flipping one pair inside a total order can produce A before B before C before A.
    /// That is a genuine hang, with no vanilla recovery — enableAircraftPathTimeout
    /// ships off. So rather than trying to prove cycles impossible, every sweep builds
    /// the wait-for graph and breaks any cycle it finds. See BreakDeadlocks.
    /// </summary>
    internal class TaxiOvertake : MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<AircraftController, SmoothPath<TaxiwayNode>> PathOf =
            AccessTools.FieldRefAccess<AircraftController, SmoothPath<TaxiwayNode>>("path");

        private struct Settled
        {
            public string Winner;
            public float Expires;
        }

        private float nextCheck;
        private float nextSummary;
        private float taxiingSeconds;
        private int sinceOvertakes, sinceRefusals, sinceBlocks, sinceBreaks, sinceGridlock;
        private int sinceOvertakesGA, sinceRefusalsGA;
        private readonly HashSet<TaxiwayNode> committed = new HashSet<TaxiwayNode>();
        private readonly List<TaxiwayNode> shared = new List<TaxiwayNode>();
        private readonly Dictionary<string, Settled> settled = new Dictionary<string, Settled>();
        private readonly List<string> expired = new List<string>();

        // Wait-for graph, rebuilt each sweep. Every blocked aircraft waits on exactly
        // one other — the head of the queue at its look-ahead node — so this is a
        // functional graph and a plain walk finds its cycles.
        private readonly Dictionary<string, AircraftController> byID = new Dictionary<string, AircraftController>();
        private readonly Dictionary<string, string> blockedBy = new Dictionary<string, string>();
        private readonly Dictionary<string, TaxiwayNode> contestedBy = new Dictionary<string, TaxiwayNode>();
        private readonly Dictionary<string, int> mark = new Dictionary<string, int>();
        private readonly List<string> walk = new List<string>();
        private readonly List<string> cycleKey = new List<string>();
        private readonly HashSet<string> triedCandidates = new HashSet<string>();
        private int lastChangedQueues;
        private readonly Dictionary<string, float> warned = new Dictionary<string, float>();

        /// <summary>
        /// Gridlock is by definition persistent, so a warning about it fires on every
        /// sweep for as long as it lasts — twice a second, forever. That buries the very
        /// lines worth reading. Warn once per condition, then stay quiet for a while.
        /// </summary>
        private bool ShouldWarn(string key)
        {
            if (warned.TryGetValue(key, out float quietUntil) && Time.time < quietUntil) return false;
            warned[key] = Time.time + Plugin.WarnRepeatSeconds.Value;
            return true;
        }

        /// <summary>
        /// Mirrors RunwayModel.SortQueue, the ladder the game already applies to its
        /// runway queue. Lower tier wins; inside the overdue tier, the more overdue
        /// aircraft wins. Tiers 0 and 1 are protected — nothing overtakes them.
        /// </summary>
        private readonly struct Priority
        {
            public readonly int Tier;
            public readonly double OverdueMinutes;

            public Priority(int tier, double overdueMinutes)
            {
                Tier = tier;
                OverdueMinutes = overdueMinutes;
            }

            public bool Protected => Tier <= 1;

            public bool Beats(Priority other)
            {
                if (Tier != other.Tier) return Tier < other.Tier;

                // Within a tier, lateness only counts when it is a real difference. Without
                // a deadband every pair has a "winner" by a fraction of a minute, which
                // turns the rank override into something that fires constantly.
                return OverdueMinutes - other.OverdueMinutes > Plugin.DelayDeadband.Value;
            }
        }

        private DateTime now;
        private readonly System.Diagnostics.Stopwatch sweepClock = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// True once this sweep has used its time budget. Every pass checks it between
        /// aircraft and stops cleanly.
        ///
        /// Nothing here is proportional to a fixed cost: each pass walks every aircraft's
        /// remaining route, and the head-on pass indexes the other aircraft's route per
        /// pair. On a big airport at high game speed — the sweep is on scaled time, so 3x
        /// speed means three times as many sweeps per real second — that could add up to
        /// something a player would feel as stutter. Stopping mid-sweep costs a little
        /// responsiveness for one tick; the next sweep starts over with current data.
        /// </summary>
        private bool OverBudget => sweepClock.Elapsed.TotalMilliseconds > Plugin.SweepBudgetMs.Value;
        private AirTrafficController airTraffic;
        private TimeController clock;

        /// <summary>
        /// Resolves a game singleton WITHOUT going through <c>Singleton&lt;T&gt;.Instance</c>.
        ///
        /// That property is not safe to touch before the object exists. Singleton.Initiate is:
        ///
        ///     instance = FindObjectOfType(typeof(T));
        ///     if (instance == null &amp;&amp; debugLog) Debug.Log(...); else initialized = true;
        ///
        /// and <c>debugLog</c> is a static bool that stays false until that singleton's own
        /// Awake has run. So an access made before the object is created takes the else
        /// branch and latches <c>initialized = true</c> with a NULL instance — pinning
        /// Instance to null for the rest of the session, for every consumer in the game,
        /// not just this mod. OnDestroy is what clears it, and it never runs for an
        /// object that was never created.
        ///
        /// This component's Update starts at chainloader time, long before the game world
        /// exists, so one early access was enough to poison AirTrafficController for the
        /// whole process. AirportCEOTweaksAircraft then read it inside its
        /// GameController.Awake patch, threw a NullReferenceException, and startup hung
        /// with Awake half-run.
        ///
        /// FindObjectOfType has no such latch. Cache the result and re-look-up only while
        /// it is null; Unity's overloaded == treats a destroyed object as null, so this
        /// self-heals across scene changes.
        /// </summary>
        private static T Resolve<T>(ref T cached) where T : UnityEngine.Object
        {
            if (cached == null) cached = UnityEngine.Object.FindObjectOfType<T>();
            return cached;
        }

        private static bool IsNodeTaxiing(Enums.AircraftAction action)
        {
            switch (action)
            {
                case Enums.AircraftAction.TaxiingToRunway:
                case Enums.AircraftAction.TaxiingToStand:
                case Enums.AircraftAction.TaxiingToHangar:
                case Enums.AircraftAction.TaxiingToDeicingStand:
                    return true;
                default:
                    return false;
            }
        }

        private enum Shared { None, Same, Opposite }

        /// <summary>
        /// Which way two aircraft traverse a node they share. Same test vanilla's unused
        /// SkipPath uses at AircraftController.cs:2168-2172: locate the node in the other
        /// aircraft's path, then see which of its neighbours matches this aircraft's next
        /// node. Forward neighbour means they are going the same way; backward neighbour
        /// means they are head-on.
        /// </summary>
        private static Shared Classify(SmoothPath<TaxiwayNode> mine, int myIndex, SmoothPath<TaxiwayNode> theirs)
        {
            if (mine?.nodes == null || theirs?.nodes == null) return Shared.None;
            if (myIndex < 0 || myIndex + 1 >= mine.nodes.Length) return Shared.None;

            var here = mine.nodes[myIndex];
            var next = mine.nodes[myIndex + 1];
            if (here == null || next == null) return Shared.None;

            for (int j = 0; j < theirs.nodes.Length; j++)
            {
                if (theirs.nodes[j] != here) continue;
                if (j + 1 < theirs.nodes.Length && theirs.nodes[j + 1] == next) return Shared.Same;
                if (j > 0 && theirs.nodes[j - 1] == next) return Shared.Opposite;
                return Shared.None;
            }
            return Shared.None;
        }

        /// <summary>
        /// An aircraft in PushBack has NOT begun moving until it leads the first node of
        /// its route — that is the gate at AircraftController.cs:1145,
        /// <c>while (!firstNode.IsTravelable(ReferenceID))</c>. Until it clears that gate
        /// it is sitting at its stand while holding a reservation it has not reached.
        ///
        /// This distinction is the whole reason a lockup could go undetected. PushBack was
        /// excluded everywhere, following the game's own refusal to overtake a pushing-back
        /// aircraft (AircraftController.cs:2155) — but that refusal is about one that is
        /// physically reversing, not one parked and queued. Excluding both meant a parked
        /// aircraft could neither be passed nor appear in the wait-for graph, so any chain
        /// that reached one simply terminated and no cycle was ever found.
        /// </summary>
        private static bool IsWaitingToPushBack(AircraftController ac, SmoothPath<TaxiwayNode> path)
        {
            if (ac?.am == null || !IsPushingBack(ac.am.AircraftAction)) return false;
            if (path?.nodes == null || path.nodes.Length == 0 || path.nodes[0] == null) return false;
            return !path.nodes[0].IsTravelable(ac.ReferenceID);
        }

        /// <summary>
        /// The node an aircraft is actually waiting on. For a taxiing aircraft that is its
        /// look-ahead node; for one still queued to push back it is the first node of its
        /// route, because <c>CurrentLookAheadIndex</c> is stale from whatever path it last
        /// followed.
        /// </summary>
        private static TaxiwayNode ContestedNodeOf(AircraftController ac, SmoothPath<TaxiwayNode> path)
        {
            if (path?.nodes == null || path.nodes.Length == 0) return null;

            if (IsPushingBack(ac.am.AircraftAction)) return path.nodes[0];
            if (!IsNodeTaxiing(ac.am.AircraftAction)) return null;

            int index = ac.CurrentLookAheadIndex;
            if (index < 0 || index >= path.nodes.Length) return null;
            return path.nodes[index];
        }

        /// <summary>
        /// How many nodes behind its current one an aircraft physically covers — the
        /// ground another aircraft must not be waved into.
        ///
        /// This started as `lookBackCount` (5/6/8), then got shortened to a per-class
        /// estimate of half the airframe on the reasoning that the reservation tail is
        /// longer than the aeroplane. **Aircraft then clipped through each other**, so the
        /// estimate was wrong — most likely because it assumed the transform sits at the
        /// centre of the airframe, and because each weight class spans a wide range: a
        /// "Small" is anything from an 8 m light single to a 27 m ATR, and sizing for the
        /// former leaves the latter unprotected.
        ///
        /// Back to `lookBackCount`, which is the value that never clipped and is a
        /// reasonable physical proxy — 20/24/32 m against airframes of roughly that order.
        /// The reason it was shortened was to stop the deadlock breaker giving up on
        /// solvable cycles, and that trade no longer applies: blocks, the exit check and
        /// the loop guard prevent those cycles now, so being generous here costs almost
        /// nothing.
        ///
        /// Erring long is the right direction. Too long makes the breaker occasionally
        /// refuse a promotion it could have made; too short puts two aeroplanes in the same
        /// place, which is worse and visible.
        /// </summary>
        private static int PhysicalTail(AircraftModel am)
        {
            switch (am.weightClass)
            {
                case Enums.ThreeStepScale.Large:  return Mathf.Max(1, Plugin.TailLarge.Value);
                case Enums.ThreeStepScale.Medium: return Mathf.Max(1, Plugin.TailMedium.Value);
                default:                          return Mathf.Max(1, Plugin.TailSmall.Value);
            }
        }

        /// <summary>
        /// Compact identity for the log: operator type and weight class. Without it every
        /// line is a pair of GUIDs, and there is no way to tell whether a decision happened
        /// on the commercial apron or in the GA ramp — which matters when the two have
        /// different taxiway treatment.
        /// </summary>
        private static string Tag(AircraftController ac)
        {
            var flight = ac.FlightModel;
            string kind = "?";
            if (flight != null)
            {
                switch (flight.flightType)
                {
                    case Enums.FlightType.GeneralAviation: kind = "GA"; break;
                    case Enums.FlightType.Cargo: kind = "CGO"; break;
                    case Enums.FlightType.Commercial: kind = "COM"; break;
                }
            }

            char size = ac.am.weightClass == Enums.ThreeStepScale.Large ? 'L'
                      : ac.am.weightClass == Enums.ThreeStepScale.Medium ? 'M' : 'S';
            return kind + "-" + size;
        }

        private static bool IsGA(AircraftController ac)
        {
            var flight = ac.FlightModel;
            return flight != null && flight.flightType == Enums.FlightType.GeneralAviation;
        }

        /// <summary>
        /// Both kinds of pushback. `PushBackFromHangar` is a separate action but a
        /// structurally identical coroutine — same `OccupyFullPath`, same
        /// `pathPreOccupied`, same `while (!firstNode.IsTravelable(...))` gate, same
        /// unguarded reverse afterwards. Testing only for `PushBack` left everything
        /// leaving a repair hangar invisible to all of this, which is the same gap that
        /// once hid queued stand pushbacks from the deadlock graph.
        /// </summary>
        private static bool IsPushingBack(Enums.AircraftAction action)
        {
            return action == Enums.AircraftAction.PushBack
                || action == Enums.AircraftAction.PushBackFromHangar;
        }

        /// <summary>
        /// Roughly how long the airframe is, in metres. PhysicalTail is about half its
        /// length in 4 m nodes, so double it.
        ///
        /// This belongs in every "will it be clear in time" test. Those measure distance to
        /// a node, but an aircraft is not clear of a node when its nose reaches the far
        /// side — it is clear when its tail does. For a light single that is a rounding
        /// error; for a widebody it is another eighteen nodes of travel, so judging by
        /// distance alone lets a large aircraft commit to a corridor it will not actually
        /// be out of before the opposing one arrives.
        /// </summary>
        private static float LengthOf(AircraftModel am)
        {
            return PhysicalTail(am) * 8f;
        }

        private static bool IsDeparting(Enums.AircraftAction action)
        {
            return action == Enums.AircraftAction.TaxiingToRunway
                || action == Enums.AircraftAction.TaxiingToDeicingStand;
        }

        private void Update()
        {
            if (!Plugin.OvertakeEnabled.Value) return;
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + Plugin.OvertakeInterval.Value;

            // Resolved before the guard so a missing world is an early return, not a fault.
            var atc = Resolve(ref airTraffic);
            if (atc == null) return;

            var time = Resolve(ref clock);
            if (time == null) return;
            now = time.GetCurrentContinuousTime();

            sweepClock.Restart();

            try
            {
                Sweep(atc);
            }
            catch (Exception ex)
            {
                // An exception escaping Update is silent feature-death: Unity logs it, the
                // game carries on at full frame rate, and the mod simply stops working while
                // nothing looks wrong. That already happened once here. Containing it means a
                // player without any way to diagnose this still gets a working game and one
                // legible error rather than traffic that quietly stops behaving.
                if (ShouldWarn("sweep-fault:" + ex.GetType().Name))
                {
                    Plugin.Log.LogError(
                        "Taxiway logic faulted and was skipped for this tick. If aircraft start "
                        + "behaving oddly, please report this with your Player.log:\n" + ex);
                }
            }
        }

        private void Sweep(AirTrafficController atc)
        {
            PruneSettled();
            PushbackHolds.Prune();

            var all = atc.GetAircraftList();

            // The precedence relation has to be current before anything reorders against it.
            BuildPrecedence(all);

            // Mutual exclusion first: decide who owns a head-on corridor before anyone
            // is allowed to reorder queues inside it.
            if (Plugin.OpposingRuns.Value)
            {
                EnforceBlocks(all, atc);

                // Blocks only cover single-lane runs. Junction nodes belong to no block, so
                // a head-on forming across them — a ramp connector, most often — was never
                // something block exclusion could catch.
                EnforceOpposingPairs(all, atc);
            }

            for (int i = 0; i < all.Length && !OverBudget; i++)
            {
                TryUnblock(all[i], atc);
            }

            EmitSummary(all);

            if (Plugin.DeadlockBreaker.Value)
            {
                // An exception escaping here aborts the rest of the pass for that sweep.
                // Unity logs it and carries on, so the feature stops working while the game
                // looks fine — which is exactly how the last freeze hid. Contain it, and
                // report it once rather than twice a second.
                try
                {
                    BreakDeadlocks(all);
                }
                catch (Exception ex)
                {
                    if (ShouldWarn("breaker-fault:" + ex.GetType().Name))
                    {
                        Plugin.Log.LogError("Deadlock breaker faulted, deadlocks will not clear: " + ex);
                    }
                }
            }
        }

        /// <summary>
        /// Finds cycles in the wait-for graph and breaks each one by promoting a single
        /// member past its blocker. Breaking one edge is enough: the promoted aircraft
        /// moves, releases, and the rest of the chain unwinds behind it.
        ///
        /// Worth having even with overtaking disabled — the game creates the same
        /// inconsistency itself, through the single-node SortUp at
        /// AircraftController.cs:2022 and four AddFirst claims, and ships with its own
        /// recovery (enableAircraftPathTimeout) turned off.
        /// </summary>
        private void BreakDeadlocks(AircraftController[] all)
        {
            byID.Clear();
            blockedBy.Clear();
            contestedBy.Clear();
            mark.Clear();

            for (int i = 0; i < all.Length; i++)
            {
                var ac = all[i];
                if (ac == null || ac.am == null) continue;

                var path = PathOf(ac);
                if (path?.nodes == null || path.nodes.Length == 0) continue;

                string id = ac.ReferenceID;

                // Name every aircraft holding a path, even ones that never wait on anybody.
                // An aircraft on a deicing pad blocks the node it stands on but waits only
                // on a clock, so a chain reaching it correctly terminates — but if it is not
                // in this map the chain terminates *anonymously*, and the report shows a
                // shorter cycle than the one on screen. That blind spot is what hid the
                // pushback problem for several rounds.
                byID[id] = ac;

                // Queued pushbacks belong in this graph too. They hold reservations and they
                // do wait on other aircraft, so leaving them out made every chain that
                // reached one look like it terminated.
                var node = ContestedNodeOf(ac, path);
                if (node == null) continue;

                if (node.IsTravelable(id)) continue;   // free: a chain ending here cannot be a cycle
                blockedBy[id] = node.PeekFirstOccupee();
                contestedBy[id] = node;
            }

            // Two is enough now. Pairwise consistency only holds for pairs this mod has
            // ordered; a queued pushback and a taxiing aircraft can lock each other with
            // nothing having reordered anything.
            if (blockedBy.Count < 2) return;

            foreach (var start in blockedBy.Keys)
            {
                if (mark.ContainsKey(start)) continue;

                walk.Clear();
                string current = start;
                while (true)
                {
                    if (current == null) break;
                    mark[current] = 1;
                    walk.Add(current);

                    if (!blockedBy.TryGetValue(current, out string next) || next == null) break;   // not blocked: chain ends

                    if (mark.TryGetValue(next, out int seen))
                    {
                        if (seen == 1) Resolve(walk, walk.IndexOf(next));
                        break;
                    }
                    current = next;
                }

                for (int i = 0; i < walk.Count; i++) if (walk[i] != null) mark[walk[i]] = 2;
            }
        }

        /// <summary>
        /// Promotes the best-placed member of a cycle past its blocker. Candidates are
        /// tried in rank order, then by proximity to the node they are waiting on,
        /// because MoveAheadEverywhere still refuses to reorder ground a blocker is
        /// physically standing on.
        /// </summary>
        private void Resolve(List<string> chain, int from)
        {
            if (from < 0) return;
            int size = chain.Count - from;
            if (size < 2) return;

            // Stable identity for this cycle, so a persistent one warns once rather than
            // twice a second.
            cycleKey.Clear();
            for (int i = from; i < chain.Count; i++) if (chain[i] != null) cycleKey.Add(chain[i]);
            cycleKey.Sort();

            // Candidates already tried are tracked here rather than by blanking entries in
            // `chain`. `chain` is the caller's `walk` list, and BreakDeadlocks iterates it
            // afterwards to stamp `mark` — writing nulls into it meant `mark[null]`, an
            // ArgumentNullException out of Update() on the sweep, which silently aborted
            // the rest of the deadlock pass. 326 of them in one session, and the freezes
            // were simply cycles that never got broken.
            triedCandidates.Clear();

            for (int attempt = 0; attempt < size; attempt++)
            {
                string best = null;
                Priority bestPriority = default;
                float bestDistance = float.MaxValue;

                for (int i = from; i < chain.Count; i++)
                {
                    string id = chain[i];
                    if (id == null || triedCandidates.Contains(id)) continue;
                    if (!byID.TryGetValue(id, out var ac) || ac == null) continue;

                    var priority = PriorityOf(ac, PathOf(ac));
                    float distance = Utils.GetDistance2D(ac.Position, contestedBy[id].worldPosition);

                    bool better = best == null
                        || priority.Beats(bestPriority)
                        || (!bestPriority.Beats(priority) && distance < bestDistance);
                    if (!better) continue;

                    best = id;
                    bestPriority = priority;
                    bestDistance = distance;
                }

                if (best == null) break;

                var mover = byID[best];
                // lastChangedQueues matters here: MoveAheadEverywhere returns true when
                // the order already held, and treating that as success meant the breaker
                // announced a fix it had not made, returned, and left the cycle in place
                // to be rediscovered every sweep. A promotion that changes nothing is not
                // a promotion — move on to the next candidate.
                if (byID.TryGetValue(blockedBy[best], out var blocker) && blocker != null
                    && MoveAheadEverywhere(mover, blocker, PathOf(mover), guardCycles: false)
                    && lastChangedQueues > 0)
                {
                    sinceBreaks++;
                    Plugin.Log.LogWarning(
                        $"Broke a {size}-way taxi deadlock: {best} promoted ahead of {blocker.ReferenceID}.");
                    return;
                }

                // That one could not be waved through. Take it out of the running and
                // try the next.
                triedCandidates.Add(best);
            }

            if (ShouldWarn("cycle:" + string.Join(",", cycleKey.ToArray())))
            {
                sinceGridlock++;
                Plugin.Log.LogWarning(
                    $"Found a {size}-way taxi deadlock but could not break it: every candidate is blocked by "
                    + "ground another cycle member is standing on. This needs a re-route, not a reorder.\n"
                    + DescribeCycle());
            }
        }

        private struct BlockClaim
        {
            public AircraftController Aircraft;
            public SmoothPath<TaxiwayNode> Path;
            public int Direction;     // +1 or -1 along the block's stored order
            public bool Inside;       // physically standing in the block
            public bool Parked;       // queued to push back, start time unknown
            public float Distance;    // to the first block node on its route
            public TaxiwayNode Exit;  // first node beyond the block on its route, null if it ends inside
        }

        private readonly Dictionary<int, List<BlockClaim>> claimsByBlock = new Dictionary<int, List<BlockClaim>>();
        private readonly Dictionary<int, int> blockOwner = new Dictionary<int, int>();
        private readonly Dictionary<int, float> blockHeldSince = new Dictionary<int, float>();
        private readonly HashSet<int> blocksSeen = new HashSet<int>();
        private readonly List<int> staleBlocks = new List<int>();
        private readonly HashSet<string> opposingPairsSeen = new HashSet<string>();
        private readonly Dictionary<TaxiwayNode, int> otherIndex = new Dictionary<TaxiwayNode, int>();

        // Who is currently behind whom, derived from the node queues each sweep.
        private readonly Dictionary<string, List<string>> waitsFor = new Dictionary<string, List<string>>();
        private readonly Stack<List<string>> edgePool = new Stack<List<string>>();
        private readonly HashSet<string> reachSeen = new HashSet<string>();
        private readonly Stack<string> reachStack = new Stack<string>();
        private readonly Stack<List<BlockClaim>> claimPool = new Stack<List<BlockClaim>>();

        /// <summary>
        /// Segment-at-a-time clearance, which is how a real ground controller works: an
        /// aircraft is cleared along a stretch of taxiway to a hold-short point, and
        /// opposing traffic waits outside rather than meeting it half way.
        ///
        /// For every single-lane block, gather the aircraft whose routes run through it
        /// and which way each is going. If both directions want it, one direction owns it
        /// and everything coming the other way is ordered behind, at every node of the
        /// block, so it stops short of the entrance instead of nosing in.
        ///
        /// The decision is made **once per block**, not once per pair. That is the whole
        /// point of doing it this way: pairwise decisions between three aircraft can
        /// disagree and close a cycle, whereas one owning direction per block is
        /// consistent by construction for every aircraft in it.
        ///
        /// Same-direction traffic is left alone — aircraft follow each other down a
        /// taxiway perfectly happily, and ordinary occupancy already spaces them.
        /// </summary>
        private void EnforceBlocks(AircraftController[] all, AirTrafficController atc)
        {
            TaxiwayBlocks.EnsureBuilt();
            TaxiwayBridges.EnsureBuilt();
            if (TaxiwayBlocks.Count == 0) return;

            foreach (var entry in claimsByBlock)
            {
                entry.Value.Clear();
                claimPool.Push(entry.Value);
            }
            claimsByBlock.Clear();

            for (int i = 0; i < all.Length; i++)
            {
                var ac = all[i];
                if (ac == null || ac.am == null) continue;

                var path = PathOf(ac);
                if (path?.nodes == null || path.nodes.Length == 0) continue;
                if (ContestedNodeOf(ac, path) == null) continue;   // not taxiing or queued to push back

                CollectBlockClaims(ac, path);
            }

            blocksSeen.Clear();
            foreach (var entry in claimsByBlock) ResolveBlock(entry.Key, entry.Value);

            // Drop tokens for blocks nobody is heading for any more, so the next aircraft
            // to arrive gets a fresh decision rather than inheriting an old direction.
            staleBlocks.Clear();
            foreach (var entry in blockOwner)
            {
                if (!blocksSeen.Contains(entry.Key)) staleBlocks.Add(entry.Key);
            }
            for (int i = 0; i < staleBlocks.Count; i++)
            {
                blockOwner.Remove(staleBlocks[i]);
                blockHeldSince.Remove(staleBlocks[i]);
            }
        }

        /// <summary>
        /// Rebuilds the wait-for relation: an edge X -> Y means X is queued behind Y
        /// somewhere and cannot pass until Y has gone. Only the immediate predecessor at
        /// each node is recorded; reachability recovers the rest.
        ///
        /// This is what makes deadlock avoidable rather than merely recoverable. Vanilla
        /// ordered every queue by claim time — one number per aircraft, the same
        /// everywhere — and a total order is transitive, so no cycle could exist. Every
        /// reordering rule here replaced that with pairwise comparisons, which form a
        /// tournament, and tournaments are not transitive: three individually correct
        /// decisions can close a loop. Checking reachability before each reorder restores
        /// the guarantee without giving up the local rules that make traffic flow.
        /// </summary>
        private void BuildPrecedence(AircraftController[] all)
        {
            foreach (var entry in waitsFor)
            {
                entry.Value.Clear();
                edgePool.Push(entry.Value);
            }
            waitsFor.Clear();

            for (int i = 0; i < all.Length; i++)
            {
                var ac = all[i];
                if (ac == null || ac.am == null) continue;

                var path = PathOf(ac);
                if (path?.nodes == null || path.nodes.Length == 0) continue;
                if (ContestedNodeOf(ac, path) == null) continue;

                // Only ground the aircraft has actually ASKED for counts as a wait.
                //
                // A database builds its wait-for graph from locks that were requested and
                // denied; a lock a transaction will need later contributes no edge, because
                // it has not asked yet. The equivalent here is the look-ahead window — the
                // aircraft stops when its look-ahead node is taken, and nothing past that
                // has been requested. Everything beyond is a reservation.
                //
                // Recording every reserved node instead produced a graph so dense that
                // almost any reorder appeared to close a loop: 218 refusals against 1
                // overtake in a single minute. That is not deadlock avoidance, it is
                // deadlock avoidance's failure mode — Banker's algorithm refusing grants
                // that were perfectly safe. Loops that form beyond this window are the
                // breaker's job, which is the same division of labour a real DBMS uses.
                string id = ac.ReferenceID;
                int start = Mathf.Clamp(ac.TargetIndex, 0, path.nodes.Length - 1);
                int requested = Mathf.Clamp(ac.CurrentLookAheadIndex, start, path.nodes.Length - 1);
                int stop = Mathf.Min(path.nodes.Length, requested + 1 + Plugin.PrecedenceHorizon.Value);
                for (int k = start; k < stop; k++)
                {
                    var node = path.nodes[k];
                    if (node == null) continue;
                    string ahead = PredecessorOf(node, id);
                    if (ahead != null) AddEdge(id, ahead);
                }
            }
        }

        private void AddEdge(string from, string to)
        {
            if (!waitsFor.TryGetValue(from, out var list))
            {
                list = edgePool.Count > 0 ? edgePool.Pop() : new List<string>();
                waitsFor[from] = list;
            }
            if (!list.Contains(to)) list.Add(to);
        }

        /// <summary>
        /// Would putting <paramref name="mover"/> ahead of <paramref name="blocker"/> close
        /// a loop? After the flip the blocker waits for the mover, so a cycle exists if the
        /// mover already waits for the blocker by some *other* route. The direct edge is
        /// skipped, because that is exactly the one the flip removes.
        /// </summary>
        private bool WouldCloseCycle(string mover, string blocker)
        {
            reachSeen.Clear();
            reachStack.Clear();
            reachSeen.Add(mover);

            if (waitsFor.TryGetValue(mover, out var first))
            {
                for (int i = 0; i < first.Count; i++)
                {
                    if (first[i] == blocker) continue;   // the edge being replaced
                    if (reachSeen.Add(first[i])) reachStack.Push(first[i]);
                }
            }

            while (reachStack.Count > 0)
            {
                var current = reachStack.Pop();
                if (current == blocker) return true;
                if (!waitsFor.TryGetValue(current, out var next)) continue;
                for (int i = 0; i < next.Count; i++)
                {
                    if (reachSeen.Add(next[i])) reachStack.Push(next[i]);
                }
            }
            return false;
        }

        /// <summary>
        /// Head-on conflicts that are not inside a block.
        ///
        /// Blocks were limited to runs of degree-&lt;=2 nodes on the theory that aircraft can
        /// pass one another elsewhere. That is wrong: the game allows **one aircraft per
        /// node**, so two routes sharing nodes in opposite order conflict whatever the
        /// degree. Passing happens by taking *different* nodes, which means no shared run
        /// at all. A ramp connector built of junction nodes is exactly as unpassable as a
        /// corridor, and that is where the gridlock formed.
        ///
        /// Blocks keep their token machinery for corridors, where hold time has to scale
        /// with length. This handles everything else, pair by pair.
        /// </summary>
        private void EnforceOpposingPairs(AircraftController[] all, AirTrafficController atc)
        {
            opposingPairsSeen.Clear();

            for (int i = 0; i < all.Length; i++)
            {
                if (OverBudget) break;

                var m = all[i];
                if (m == null || m.am == null) continue;

                var mPath = PathOf(m);
                if (mPath?.nodes == null || mPath.nodes.Length == 0) continue;
                if (ContestedNodeOf(m, mPath) == null) continue;

                string mID = m.ReferenceID;
                int from = Mathf.Clamp(m.TargetIndex, 0, mPath.nodes.Length - 1);
                int to = Mathf.Min(mPath.nodes.Length, from + Plugin.OpposingScanNodes.Value);

                for (int k = from; k < to; k++)
                {
                    var node = mPath.nodes[k];
                    if (node == null) continue;
                    if (TaxiwayBlocks.BlockAt(node) >= 0) continue;   // a block owns this one

                    for (var cur = node.occupeeRefs.First; cur != null; cur = cur.Next)
                    {
                        if (cur.Value == mID) continue;
                        if (!opposingPairsSeen.Add(PairKey(mID, cur.Value))) continue;
                        ResolveOpposingPair(m, mPath, from, to, atc.GetAircraftByReferenceID(cur.Value));
                    }
                }
            }
        }

        private void ResolveOpposingPair(AircraftController m, SmoothPath<TaxiwayNode> mPath, int from, int to, AircraftController o)
        {
            if (o == null || o == m || o.am == null) return;

            var oPath = PathOf(o);
            if (oPath?.nodes == null || oPath.nodes.Length == 0) return;
            if (ContestedNodeOf(o, oPath) == null) return;

            // Search the whole window for a point where the two run head-on, rather than
            // judging by the first node they happen to share.
            //
            // Two aircraft share many nodes and the relationship differs along them: same
            // direction down the main taxiway, opposing on the ramp connector. Classifying
            // only the first shared node and then marking the pair handled meant the node
            // where they genuinely conflict was never looked at — zero head-on resolutions
            // in a session that deadlocked on exactly that connector.
            int index = FindOpposingIndex(mPath, from, to, oPath);
            if (index < 0) return;

            bool mInTheWay = StandsInTheWayOf(m, mPath, o, oPath);
            bool oInTheWay = StandsInTheWayOf(o, oPath, m, mPath);
            bool mParked = IsParkedForPushback(m, mPath);
            bool oParked = IsParkedForPushback(o, oPath);

            AircraftController winner, loser;
            SmoothPath<TaxiwayNode> winnerPath, loserPath;
            string why;

            if (mInTheWay && oInTheWay)
            {
                if (ShouldWarn("pair:" + PairKey(m.ReferenceID, o.ReferenceID)))
                {
                    Plugin.Log.LogWarning(
                        $"Head-on outside a block, both already committed: {Tag(m)} {m.ReferenceID} "
                        + $"({m.am.AircraftAction}, idx {m.TargetIndex}) and {Tag(o)} {o.ReferenceID} "
                        + $"({o.am.AircraftAction}, idx {o.TargetIndex}). Neither can back out and the game "
                        + "has no tug, so nothing can be reordered — they entered before the scan saw them. "
                        + "Raise Blocks / Junction scan distance for this layout.");
                }
                return;
            }

            if (mInTheWay || oInTheWay)
            {
                bool mWins = mInTheWay;
                winner = mWins ? m : o;
                loser = mWins ? o : m;
                winnerPath = mWins ? mPath : oPath;
                loserPath = mWins ? oPath : mPath;
                why = "already committed";
            }
            else if (mParked != oParked
                     && !(mParked ? PriorityOf(m, mPath) : PriorityOf(o, oPath)).Protected
                     && !PushbackHolds.HeldTooLong(mParked ? m.ReferenceID : o.ReferenceID))
            {
                // A pushback still at its stand yields, and does not get to win on timing.
                //
                // Every "clears first" estimate divides distance by taxi speed, which
                // assumes the aircraft is about to move. A queued pushback is not: it may
                // sit for minutes waiting for a truck. So it looks like it will be through
                // the connector in seconds, wins the contest, gets released, and is still
                // standing there when the other aircraft arrives — which is exactly how
                // this deadlock kept forming.
                //
                // Yielding costs it nothing either way. Waiting at a stand blocks nobody;
                // waiting in a corridor blocks everybody.
                bool mWins = oParked;
                winner = mWins ? m : o;
                loser = mWins ? o : m;
                winnerPath = mWins ? mPath : oPath;
                loserPath = mWins ? oPath : mPath;
                why = "other side is still at its stand";
            }
            else
            {
                var node = mPath.nodes[index];
                float run = SharedRunLength(mPath, index, oPath) * 4f;
                float dM = Utils.GetDistance2D(m.Position, node.worldPosition);
                float dO = Utils.GetDistance2D(o.Position, node.worldPosition);

                bool mWins;
                if (dM + run + LengthOf(m.am) < dO) { mWins = true; why = "clears first"; }
                else if (dO + run + LengthOf(o.am) < dM) { mWins = false; why = "clears first"; }
                else
                {
                    var pm = PriorityOf(m, mPath);
                    var po = PriorityOf(o, oPath);
                    if (pm.Protected != po.Protected) { mWins = pm.Protected; why = "priority"; }
                    else { mWins = dM <= dO; why = "nearest"; }
                }

                winner = mWins ? m : o;
                loser = mWins ? o : m;
                winnerPath = mWins ? mPath : oPath;
                loserPath = mWins ? oPath : mPath;
            }

            // If the loser has not left its stand yet, hold it there. Queue order alone
            // cannot stop a pushback: GoToPositionDirect reverses without consulting
            // occupancy, so by the time any rule applies the aeroplane is already in the
            // connector. The gate before it is the only point where this is still
            // preventable, and waiting at a stand costs nothing.
            if (IsParkedForPushback(loser, loserPath))
            {
                PushbackHolds.Apply(loser.ReferenceID, loserPath.nodes[0]);
                if (Plugin.OvertakeLogging.Value)
                {
                    Plugin.Log.LogInfo(
                        $"Gate hold: {Tag(loser)} {loser.ReferenceID} kept at its stand for "
                        + $"{Tag(winner)} {winner.ReferenceID} ({why}).");
                }
            }

            // Same reasoning as ResolveBlock: preventing an unrecoverable head-on outranks
            // avoiding a recoverable ordering loop.
            if (MoveAheadEverywhere(winner, loser, winnerPath, guardCycles: false)
                && lastChangedQueues > 0 && Plugin.OvertakeLogging.Value)
            {
                bool onlyWay = TaxiwayBridges.IsBridge(mPath.nodes[index],
                                                       index + 1 < mPath.nodes.Length ? mPath.nodes[index + 1] : null);
                Plugin.Log.LogInfo(
                    $"Head-on: {Tag(winner)} {winner.ReferenceID} goes, {Tag(loser)} {loser.ReferenceID} "
                    + $"holds off ({why}{(onlyWay ? ", only way through" : "")}).");
            }
        }

        /// <summary>
        /// First index in the window where the two routes run head-on, or -1. Their route is
        /// indexed once up front so each step is a dictionary lookup rather than a rescan.
        /// </summary>
        private int FindOpposingIndex(SmoothPath<TaxiwayNode> mine, int from, int to, SmoothPath<TaxiwayNode> theirs)
        {
            otherIndex.Clear();
            for (int j = 0; j < theirs.nodes.Length; j++)
            {
                var node = theirs.nodes[j];
                if (node != null && !otherIndex.ContainsKey(node)) otherIndex[node] = j;
            }

            int last = Mathf.Min(to, mine.nodes.Length - 1);
            for (int k = from; k < last; k++)
            {
                var here = mine.nodes[k];
                var next = mine.nodes[k + 1];
                if (here == null || next == null) continue;
                if (!otherIndex.TryGetValue(here, out int j) || j == 0) continue;
                if (theirs.nodes[j - 1] == next) return k;
            }
            return -1;
        }

        /// <summary>Do any of these path nodes lie on the other aircraft's remaining route?</summary>
        private static bool SharesRemainingGround(SmoothPath<TaxiwayNode> xPath, int from, int count,
                                                  AircraftController y, SmoothPath<TaxiwayNode> yPath)
        {
            int last = Mathf.Min(xPath.nodes.Length - 1, from + count);
            int start = Mathf.Clamp(y.TargetIndex, 0, yPath.nodes.Length - 1);

            for (int i = from; i <= last; i++)
            {
                var node = xPath.nodes[i];
                if (node == null) continue;
                for (int j = start; j < yPath.nodes.Length; j++)
                {
                    if (yPath.nodes[j] == node) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Still at the stand, not yet reversing. A pushback never advances TargetIndex —
        /// GoToPositionDirect moves it without following the node path — so position is the
        /// only way to tell the two apart, and only the former can still be held.
        /// </summary>
        private static bool IsParkedForPushback(AircraftController ac, SmoothPath<TaxiwayNode> path)
        {
            // Holdable exactly while it is still in the gate loop, which is the same thing
            // as not yet leading its first node. Nothing else can be held: past the gate,
            // GoToPositionDirect moves it regardless.
            //
            // This used to test distance from nodes[0] against PhysicalTail * 4 — 16 m for a
            // Small but 32 m for a Large. A large stand within 32 m of its connector would
            // have read as "already moving", so widebodies would never have been gate-held
            // at all while small aircraft were. A gate test has no business scaling with
            // wingspan.
            return IsWaitingToPushBack(ac, path);
        }

        /// <summary>How many nodes the two routes share continuously from this point.</summary>
        private static int SharedRunLength(SmoothPath<TaxiwayNode> mine, int index, SmoothPath<TaxiwayNode> theirs)
        {
            int run = 0;
            for (int i = index; i < mine.nodes.Length && run < 200; i++)
            {
                var node = mine.nodes[i];
                if (node == null) break;

                bool onTheirs = false;
                for (int j = 0; j < theirs.nodes.Length && !onTheirs; j++) onTheirs = theirs.nodes[j] == node;
                if (!onTheirs) break;

                run++;
            }
            return Mathf.Max(1, run);
        }

        /// <summary>
        /// True when <paramref name="x"/> is physically standing on ground <paramref name="y"/>
        /// still has to drive over — the thing that decides a head-on, because an aircraft in
        /// that position cannot back out and the game has no tug.
        /// </summary>
        private static bool StandsInTheWayOf(AircraftController x, SmoothPath<TaxiwayNode> xPath,
                                             AircraftController y, SmoothPath<TaxiwayNode> yPath)
        {
            // A pushback is committed the moment it leads its first node, not when it
            // physically moves.
            //
            // Everything hinges on what can still be stopped. While it is queued at the
            // gate — not yet leading nodes[0] — it is holdable, so it should yield and the
            // other aircraft may proceed. Once it leads that node it has passed the gate,
            // and GoToPositionDirect will reverse it into the connector without consulting
            // occupancy, occupee lists or anything else. Nothing downstream can prevent
            // that, so it is committed in the only sense that matters and the OTHER
            // aircraft is the one that has to hold.
            //
            // Judging this by physical position instead — "it is still parked, so it is not
            // in the way" — is true and useless: it let arrivals into a connector that a
            // departure was about to reverse into unstoppably.
            if (IsPushingBack(x.am.AircraftAction))
            {
                if (IsWaitingToPushBack(x, xPath)) return false;   // still at the gate, can be held
                return SharesRemainingGround(xPath, 0, PhysicalTail(x.am), y, yPath);
            }

            int reached = Mathf.Clamp(x.TargetIndex, 0, xPath.nodes.Length - 1);
            int tail = Mathf.Max(0, reached - PhysicalTail(x.am));
            int from = Mathf.Clamp(y.TargetIndex, 0, yPath.nodes.Length - 1);

            // Only ground it is genuinely NEAR. Path index alone is not position: an
            // aircraft queued to push back sits at index 0 while still parked at its stand,
            // tens of metres from that node, and counting it as standing there made a
            // parked aeroplane look immovable. Both sides then read as committed and the
            // head-on became unresolvable — when in truth one of them had not moved.
            //
            // For an aircraft that really is taxiing this changes nothing: every node from
            // its tail to its current one is within this radius by construction.
            float radius = PhysicalTail(x.am) * 4f;

            for (int i = tail; i <= reached; i++)
            {
                var node = xPath.nodes[i];
                if (node == null) continue;
                if (Utils.GetDistance2D(x.Position, node.worldPosition) > radius) continue;

                for (int j = from; j < yPath.nodes.Length; j++)
                {
                    if (yPath.nodes[j] == node) return true;
                }
            }
            return false;
        }

        private void CollectBlockClaims(AircraftController ac, SmoothPath<TaxiwayNode> path)
        {
            int from = Mathf.Clamp(ac.TargetIndex, 0, path.nodes.Length - 1);
            int tail = Mathf.Max(0, from - PhysicalTail(ac.am));

            int currentBlock = -1;
            int firstOrder = -1;
            int firstIndex = -1;
            int direction = 0;

            for (int i = from; i < path.nodes.Length; i++)
            {
                var node = path.nodes[i];
                int block = TaxiwayBlocks.BlockAt(node);

                if (block != currentBlock)
                {
                    // Left the block we were tracking, so this node is its exit.
                    if (currentBlock >= 0 && direction != 0)
                    {
                        Emit(ac, path, currentBlock, direction, firstIndex, tail, from, node);
                    }

                    currentBlock = block;
                    firstOrder = block >= 0 ? TaxiwayBlocks.OrderAt(node) : -1;
                    firstIndex = i;
                    direction = 0;
                    continue;
                }

                if (block >= 0 && direction == 0)
                {
                    int order = TaxiwayBlocks.OrderAt(node);
                    if (order != firstOrder) direction = order > firstOrder ? 1 : -1;
                }
            }

            // Route ends inside the block — it is parking, which counts as leaving.
            if (currentBlock >= 0 && direction != 0)
            {
                Emit(ac, path, currentBlock, direction, firstIndex, tail, from, null);
            }
        }

        private void Emit(AircraftController ac, SmoothPath<TaxiwayNode> path, int block,
                          int direction, int firstIndex, int tail, int from, TaxiwayNode exit)
        {
            bool inside = false;
            for (int k = tail; k <= from && !inside; k++)
            {
                inside = TaxiwayBlocks.BlockAt(path.nodes[k]) == block;
            }

            Add(block, new BlockClaim
            {
                Aircraft = ac,
                Path = path,
                Direction = direction,
                Inside = inside,
                Parked = IsParkedForPushback(ac, path),
                Distance = Utils.GetDistance2D(ac.Position, path.nodes[firstIndex].worldPosition),
                Exit = exit,
            });
        }

        /// <summary>
        /// The yellow-box rule, and how a ground controller thinks: do not go in unless you
        /// can come out the other side. An aircraft that enters a block it cannot leave
        /// stops inside it, and then the block is held by traffic that is not moving —
        /// which starves the opposite direction indefinitely, because the token is only
        /// handed over when the owning side is done.
        ///
        /// A route that simply ends inside the block counts as being able to leave: it is
        /// parking, and a parked aircraft releases the taxiway.
        /// </summary>
        private static bool CanExit(BlockClaim claim)
        {
            if (claim.Exit == null) return true;
            if (!claim.Exit.IsOccupied()) return true;
            return claim.Exit.IsTravelable(claim.Aircraft.ReferenceID);
        }

        private void Add(int block, BlockClaim claim)
        {
            if (!claimsByBlock.TryGetValue(block, out var list))
            {
                list = claimPool.Count > 0 ? claimPool.Pop() : new List<BlockClaim>();
                claimsByBlock[block] = list;
            }
            list.Add(claim);
        }

        private void ResolveBlock(int block, List<BlockClaim> claims)
        {
            if (claims.Count < 2) return;

            blocksSeen.Add(block);

            bool hasForward = false, hasBackward = false;
            for (int i = 0; i < claims.Count; i++)
            {
                if (claims[i].Direction > 0) hasForward = true; else hasBackward = true;
            }
            if (!hasForward || !hasBackward) return;   // one-way traffic: nothing to arbitrate

            // A block behaves like a single-line token: once a direction has it, it keeps
            // it until its traffic is through. Re-deciding every sweep let ownership flip
            // between sweeps as "nearest" changed, which stalls both directions instead of
            // clearing either — a controller does not withdraw a clearance mid-taxi.
            bool held = blockOwner.TryGetValue(block, out int owning);
            if (held)
            {
                bool stillClaiming = false, insideOwning = false, owningCanExit = false;
                for (int i = 0; i < claims.Count; i++)
                {
                    if (claims[i].Direction != owning) continue;
                    stillClaiming = true;
                    if (claims[i].Inside) insideOwning = true;
                    if (CanExit(claims[i])) owningCanExit = true;
                }

                // Hand over once its traffic is done, or if it has held long enough to be
                // starving the other side — but never while one of its aircraft is still
                // physically in there, or the handover creates the head-on it exists to
                // prevent.
                // How long a direction may keep a block has to scale with the block: a
                // 100-node corridor takes far longer to clear than a 5-node one, and a flat
                // constant either hands the token over mid-traverse on the long one or lets
                // the short one starve the other side. Nodes are 4 m apart and taxi speed is
                // 10 m/s, so traversing costs 0.4 s per node; allow a multiple of that.
                float traverse = TaxiwayBlocks.Length(block) * 0.4f;
                float allowed = Mathf.Max(MinimumBlockHold, traverse * Plugin.BlockHoldMargin.Value);
                bool starving = !insideOwning && Time.time - blockHeldSince[block] > allowed;

                // Holding a block for traffic that cannot get out the far end starves the
                // other direction for nothing. Safe to hand over only while none of that
                // traffic has actually entered.
                bool stuckOutside = !insideOwning && !owningCanExit;

                // Nobody of ours has entered, and the other side could be in and out before
                // our nearest even gets here. Holding the token through that is waiting for
                // nothing.
                bool overtakenByTiming = !insideOwning && OpposingWouldClearFirst(block, claims, owning);

                if (stuckOutside && Plugin.OvertakeLogging.Value)
                {
                    Plugin.Log.LogInfo($"Block {block}: direction {owning} cannot clear the far end, releasing.");
                }

                if (!stillClaiming || starving || stuckOutside || overtakenByTiming)
                {
                    held = false;
                    blockOwner.Remove(block);
                    blockHeldSince.Remove(block);
                }
            }

            if (!held)
            {
                owning = DecideBlockOwner(block, claims, out bool ownerInsideNow, out string why);
                blockOwner[block] = owning;
                blockHeldSince[block] = Time.time;
                sinceBlocks++;
                lastOwnerWasInside = ownerInsideNow;
                lastOwnerReason = why;
            }

            int changedPairs = 0;
            for (int i = 0; i < claims.Count; i++)
            {
                if (claims[i].Direction != owning) continue;
                for (int j = 0; j < claims.Count; j++)
                {
                    if (claims[j].Direction == owning) continue;
                    // guardCycles: false deliberately. Head-on exclusion is a safety
                    // mechanism, not a throughput one, and the loop check was refusing it —
                    // 211 refusals against a single successful head-on resolution in one
                    // session. The trade is the wrong way round: an ordering loop is
                    // recoverable, because the breaker can promote someone out of it, while
                    // a head-on is not, because neither aircraft can reverse. Refusing to
                    // prevent the unrecoverable failure in order to avoid a recoverable one
                    // is backwards.
                    if (MoveAheadEverywhere(claims[i].Aircraft, claims[j].Aircraft, claims[i].Path, guardCycles: false)
                        && lastChangedQueues > 0)
                    {
                        changedPairs++;
                    }
                }
            }

            if (changedPairs > 0 && Plugin.OvertakeLogging.Value)
            {
                Plugin.Log.LogInfo(
                    $"Block {block}: direction {owning} owns it ({lastOwnerReason}), "
                    + $"{changedPairs} opposing aircraft held off.");
            }
        }

        /// <summary>
        /// Everything prevention needs to be judged against, for a cycle it failed to stop:
        /// what each aircraft is, what it is doing, and — critically — whether the node it
        /// is stuck on is inside a single-lane block or out at a junction. Block exclusion
        /// only covers blocks, so a head-on that formed across junction nodes was never
        /// something it could have caught, and that is a different fix from one it should
        /// have caught and did not.
        /// </summary>
        private string DescribeCycle()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < cycleKey.Count; i++)
            {
                string id = cycleKey[i];
                if (!byID.TryGetValue(id, out var ac) || ac == null) { sb.Append("    ").Append(id).Append(" (gone)\n"); continue; }

                contestedBy.TryGetValue(id, out var node);
                int block = node == null ? -1 : TaxiwayBlocks.BlockAt(node);
                int degree = node?.adjacentTaxiwayNodes == null ? -1 : node.adjacentTaxiwayNodes.Count;
                string waiting = blockedBy.TryGetValue(id, out var on)
                    ? on.Substring(0, 8)
                    : "nobody (holds ground but waits on no aircraft)";

                sb.Append("    ").Append(id.Substring(0, 8)).Append(' ').Append(Tag(ac))
                  .Append(' ').Append(ac.am.AircraftAction)
                  .Append(" idx ").Append(ac.TargetIndex)
                  .Append(" waiting on ").Append(waiting)
                  .Append(" at node degree ").Append(degree)
                  .Append(block >= 0 ? $" in block {block} (len {TaxiwayBlocks.Length(block)})" : " at a JUNCTION, no block")
                  .Append('\n');
            }
            return sb.ToString();
        }

        private bool lastOwnerWasInside;
        private string lastOwnerReason = "nearest";

        /// <summary>Floor on block hold time, so a very short block is not handed over the instant it is taken.</summary>
        private const float MinimumBlockHold = 10f;

        /// <summary>
        /// Anyone already standing in the block decides it — they cannot back out and the
        /// game has no tug. Otherwise the nearest aircraft wins, with a protected tier
        /// able to override.
        /// </summary>
        /// <summary>
        /// Which direction gets the block. Distances are in metres and taxi speed is
        /// common to every aircraft, so comparing distances compares times.
        ///
        /// The rule that was missing: an aircraft that can enter the block AND be out the
        /// far side before the opposing aircraft even reaches it should simply go. Waiting
        /// costs the other side nothing — it has not arrived yet — and a block averaging
        /// six nodes takes about two and a half seconds to cross. Without this the token
        /// could be granted to a distant aircraft and then *held* by the hysteresis for the
        /// whole time it took to taxi over, with one sitting at the entrance throughout.
        /// That is the "it could have gone in front" case.
        /// </summary>
        private int DecideBlockOwner(int block, List<BlockClaim> claims, out bool ownerInside, out string reason)
        {
            float traverse = TaxiwayBlocks.Length(block) * 4f;

            float nearA = float.MaxValue, nearB = float.MaxValue;
            float lengthA = 0f, lengthB = 0f;
            bool insideA = false, insideB = false, protA = false, protB = false, exitA = false, exitB = false;
            bool movingA = false, movingB = false;

            for (int i = 0; i < claims.Count; i++)
            {
                var c = claims[i];
                bool prot = PriorityOf(c.Aircraft, c.Path).Protected;
                bool exits = CanExit(c);

                if (c.Direction > 0)
                {
                    if (c.Distance < nearA) nearA = c.Distance;
                    if (LengthOf(c.Aircraft.am) > lengthA) lengthA = LengthOf(c.Aircraft.am);
                    insideA |= c.Inside; protA |= prot; exitA |= exits; movingA |= !c.Parked;
                }
                else
                {
                    if (c.Distance < nearB) nearB = c.Distance;
                    if (LengthOf(c.Aircraft.am) > lengthB) lengthB = LengthOf(c.Aircraft.am);
                    insideB |= c.Inside; protB |= prot; exitB |= exits; movingB |= !c.Parked;
                }
            }

            int owner;
            if (insideA != insideB)                                { owner = insideA ? 1 : -1; reason = "occupied"; }
            else if (protA != protB)                              { owner = protA ? 1 : -1; reason = "priority"; }
            // A direction with nothing but parked pushbacks in it does not get the block on
            // a timing estimate: none of them has a known start time.
            else if (movingA != movingB)                          { owner = movingA ? 1 : -1; reason = "other side still at a stand"; }
            else if (nearA + traverse + lengthA < nearB)           { owner = 1;  reason = "clears first"; }
            else if (nearB + traverse + lengthB < nearA)           { owner = -1; reason = "clears first"; }
            else if (protA != protB)                      { owner = protA ? 1 : -1; reason = "priority"; }
            else if (exitA != exitB)                      { owner = exitA ? 1 : -1; reason = "can clear far end"; }
            else                                          { owner = nearA <= nearB ? 1 : -1; reason = "nearest"; }

            ownerInside = owner > 0 ? insideA : insideB;
            return owner;
        }

        /// <summary>
        /// True when the direction opposing <paramref name="owning"/> could be in and out
        /// of the block before the owner's nearest aircraft even arrives. Holding the token
        /// through that is pure waiting for nothing.
        /// </summary>
        private static bool OpposingWouldClearFirst(int block, List<BlockClaim> claims, int owning)
        {
            float traverse = TaxiwayBlocks.Length(block) * 4f;
            float nearOwner = float.MaxValue, nearOther = float.MaxValue, otherLength = 0f;

            for (int i = 0; i < claims.Count; i++)
            {
                if (claims[i].Direction == owning)
                {
                    if (claims[i].Distance < nearOwner) nearOwner = claims[i].Distance;
                }
                else
                {
                    if (claims[i].Distance < nearOther) nearOther = claims[i].Distance;
                    if (LengthOf(claims[i].Aircraft.am) > otherLength) otherLength = LengthOf(claims[i].Aircraft.am);
                }
            }

            return nearOther < float.MaxValue
                && nearOwner < float.MaxValue
                && nearOther + traverse + otherLength < nearOwner;
        }

        /// <summary>Order-independent key so a pair is the same entry whichever way round it is seen.</summary>
        private static string PairKey(string a, string b)
        {
            if (string.CompareOrdinal(a, b) >= 0)
            {
                return b + "|" + a;
            }
            return a + "|" + b;
        }

        private static string PredecessorOf(TaxiwayNode node, string referenceID)
        {
            string result = null;
            for (LinkedListNode<string> linkedListNode = node.occupeeRefs.First; linkedListNode != null; linkedListNode = linkedListNode.Next)
            {
                if (linkedListNode.Value == referenceID)
                {
                    return result;
                }
                result = linkedListNode.Value;
            }
            return null;
        }

        private static bool StandingOnRunway(AircraftController ac, SmoothPath<TaxiwayNode> path)
        {
            if (path?.nodes == null || path.nodes.Length == 0)
            {
                return false;
            }
            int num = Mathf.Clamp(ac.TargetIndex, 0, path.nodes.Length - 1);
            for (int i = Mathf.Max(0, num - PhysicalTail(ac.am)); i <= num; i++)
            {
                TaxiwayNode taxiwayNode = path.nodes[i];
                if (taxiwayNode != null && taxiwayNode.structureType == Enums.StructureType.Runway)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The game's own ladder, from RunwayModel.SortQueue: emergency, then anything
        /// crossing or clearing a runway, then everything else, with general aviation
        /// demoted. Lateness compares only WITHIN a tier and only past the deadband —
        /// nearly every arrival is technically overdue by the time it is taxiing, so as a
        /// tier of its own it put every arrival above every departure.
        /// </summary>
        private Priority PriorityOf(AircraftController ac, SmoothPath<TaxiwayNode> path)
        {
            FlightModel flightModel = ac.FlightModel;
            if (flightModel != null && flightModel.isEmergency)
            {
                return new Priority(0, 0.0);
            }
            if (StandingOnRunway(ac, path))
            {
                return new Priority(1, 0.0);
            }
            bool flag = IsDeparting(ac.am.AircraftAction);
            bool flag2 = flightModel != null && flightModel.flightType == Enums.FlightType.GeneralAviation;
            double overdueMinutes = 0.0;
            if (flightModel != null)
            {
                double totalMinutes = (now - flightModel.ScheduledTime).TotalMinutes;
                if (totalMinutes > 0.0)
                {
                    overdueMinutes = totalMinutes;
                }
            }
            if (Plugin.TieBreak.Value == TieBreakRule.DeparturesFirst)
            {
                if (flag2)
                {
                    return new Priority(4, overdueMinutes);
                }
                if (!flag)
                {
                    return new Priority(3, overdueMinutes);
                }
                return new Priority(2, overdueMinutes);
            }
            if (!flag2)
            {
                return new Priority(2, overdueMinutes);
            }
            return new Priority(3, overdueMinutes);
        }

        /// <summary>
        /// Can the mover cross and be gone before the blocker arrives? If so the wait-for
        /// edge never materialises, so the reorder cannot deadlock whatever the precedence
        /// graph says. Needs both halves: being nearer is not enough, because an aircraft
        /// that crosses and then stops ON the crossing has made things worse.
        /// </summary>
        private static bool ClearsBeforeArrival(AircraftController mover, SmoothPath<TaxiwayNode> path, float moverToNode, float blockerToNode)
        {
            if (moverToNode * Plugin.CrossAheadMargin.Value >= blockerToNode)
            {
                return false;
            }
            int num = mover.CurrentLookAheadIndex + 1;
            if (num >= path.nodes.Length)
            {
                return true;
            }
            TaxiwayNode taxiwayNode = path.nodes[num];
            if (taxiwayNode == null)
            {
                return true;
            }
            if (taxiwayNode.IsOccupied())
            {
                return taxiwayNode.IsTravelable(mover.ReferenceID);
            }
            return true;
        }

        private bool CanOvertake(AircraftController mover, Priority moverPriority, SmoothPath<TaxiwayNode> moverPath, AircraftController blocker, TaxiwayNode contested, out bool clearsFirst)
        {
            clearsFirst = false;
            if (blocker == null || blocker == mover || blocker.am == null)
            {
                return false;
            }
            SmoothPath<TaxiwayNode> smoothPath = PathOf.Invoke(blocker);
            if (smoothPath?.nodes == null || smoothPath.nodes.Length == 0)
            {
                return false;
            }
            bool flag = IsWaitingToPushBack(blocker, smoothPath);
            if (!flag && !IsNodeTaxiing(blocker.am.AircraftAction))
            {
                return false;
            }
            if (!flag && Classify(moverPath, mover.CurrentLookAheadIndex, smoothPath) == Shared.Opposite)
            {
                return false;
            }
            Priority other = PriorityOf(blocker, smoothPath);
            if (other.Protected)
            {
                return false;
            }
            if (settled.TryGetValue(PairKey(mover.ReferenceID, blocker.ReferenceID), out var value) && value.Winner != mover.ReferenceID)
            {
                return false;
            }
            if (flag)
            {
                return true;
            }
            float distance2D = Utils.GetDistance2D(blocker.Position, contested.worldPosition);
            float distance2D2 = Utils.GetDistance2D(mover.Position, contested.worldPosition);
            clearsFirst = ClearsBeforeArrival(mover, moverPath, distance2D2, distance2D);
            if (moverPriority.Beats(other))
            {
                if (!moverPriority.Protected)
                {
                    return distance2D2 <= Plugin.PriorityReach.Value;
                }
                return true;
            }
            if (other.Beats(moverPriority))
            {
                if (other.Protected)
                {
                    return false;
                }
                if (distance2D <= Plugin.PriorityReach.Value)
                {
                    return false;
                }
            }
            return distance2D2 < distance2D;
        }

        private void TryUnblock(AircraftController mover, AirTrafficController atc)
        {
            if (mover == null || mover.am == null || !IsNodeTaxiing(mover.am.AircraftAction))
            {
                return;
            }
            SmoothPath<TaxiwayNode> smoothPath = PathOf.Invoke(mover);
            if (smoothPath?.nodes == null || smoothPath.nodes.Length == 0)
            {
                return;
            }
            int currentLookAheadIndex = mover.CurrentLookAheadIndex;
            if (currentLookAheadIndex < 0 || currentLookAheadIndex >= smoothPath.nodes.Length)
            {
                return;
            }
            TaxiwayNode taxiwayNode = smoothPath.nodes[currentLookAheadIndex];
            if (taxiwayNode == null)
            {
                return;
            }
            string referenceID = mover.ReferenceID;
            if (taxiwayNode.IsTravelable(referenceID))
            {
                return;
            }
            Priority moverPriority = PriorityOf(mover, smoothPath);
            int count = taxiwayNode.occupeeRefs.Count;
            while (count-- > 0 && !taxiwayNode.IsTravelable(referenceID))
            {
                string text = PredecessorOf(taxiwayNode, referenceID);
                if (text == null)
                {
                    break;
                }
                AircraftController aircraftByReferenceID = atc.GetAircraftByReferenceID(text);
                if (!CanOvertake(mover, moverPriority, smoothPath, aircraftByReferenceID, taxiwayNode, out var clearsFirst) || !MoveAheadEverywhere(mover, aircraftByReferenceID, smoothPath, !clearsFirst) || lastChangedQueues == 0)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Raw counts are not comparable between sessions or airports — two deadlocks in a
        /// quiet hour and two in a saturated one mean opposite things. Reported per
        /// aircraft-hour of taxiing so numbers can be held against a different layout.
        /// </summary>
        private void EmitSummary(AircraftController[] all)
        {
            if (Plugin.SummaryInterval.Value <= 0f)
            {
                return;
            }
            int num = 0;
            foreach (AircraftController aircraftController in all)
            {
                if (!(aircraftController == null) && aircraftController.am != null && IsNodeTaxiing(aircraftController.am.AircraftAction))
                {
                    num++;
                }
            }
            taxiingSeconds += (float)num * Plugin.OvertakeInterval.Value;
            if (nextSummary == 0f)
            {
                nextSummary = Time.time + Plugin.SummaryInterval.Value;
            }
            else if (!(Time.time < nextSummary))
            {
                nextSummary = Time.time + Plugin.SummaryInterval.Value;
                float num2 = taxiingSeconds / 3600f;
                string arg = ((num2 > 0.001f) ? ($"per aircraft-hour: {(float)sinceOvertakes / num2:F1} overtakes, " + $"{(float)sinceRefusals / num2:F1} loop refusals, {(float)sinceBlocks / num2:F1} block grants, " + $"{(float)sinceBreaks / num2:F2} deadlocks broken, {(float)sinceGridlock / num2:F2} gridlock") : "no taxiing traffic");
                Plugin.Log.LogInfo((object)($"Summary: {num2 * 60f:F1} aircraft-minutes taxiing | {arg} | " + $"raw {sinceOvertakes}/{sinceRefusals}/{sinceBlocks}/{sinceBreaks}/{sinceGridlock} | " + $"GA share: overtakes {sinceOvertakesGA}/{sinceOvertakes}, refusals {sinceRefusalsGA}/{sinceRefusals}"));
                taxiingSeconds = 0f;
                sinceOvertakes = (sinceRefusals = (sinceBlocks = (sinceBreaks = (sinceGridlock = 0))));
                sinceOvertakesGA = (sinceRefusalsGA = 0);
            }
        }

        /// <summary>Drops expired pair cooldowns and warning throttles.</summary>
        private void PruneSettled()
        {
            float time = Time.time;
            if (settled.Count > 0)
            {
                expired.Clear();
                foreach (KeyValuePair<string, Settled> item in settled)
                {
                    if (item.Value.Expires <= time)
                    {
                        expired.Add(item.Key);
                    }
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    settled.Remove(expired[i]);
                }
            }
            PruneStamps(warned, time);
        }

        private void PruneStamps(Dictionary<string, float> map, float stamp)
        {
            if (map.Count == 0)
            {
                return;
            }
            expired.Clear();
            foreach (KeyValuePair<string, float> item in map)
            {
                if (item.Value <= stamp)
                {
                    expired.Add(item.Key);
                }
            }
            for (int i = 0; i < expired.Count; i++)
            {
                map.Remove(expired[i]);
            }
        }

        private bool MoveAheadEverywhere(AircraftController mover, AircraftController blocker, SmoothPath<TaxiwayNode> path)
        {
            return MoveAheadEverywhere(mover, blocker, path, guardCycles: true);
        }

        private bool MoveAheadEverywhere(AircraftController mover, AircraftController blocker,
                                         SmoothPath<TaxiwayNode> path, bool guardCycles)
        {
            var blockerPath = PathOf(blocker);
            if (blockerPath?.nodes == null) return false;

            // Refuse any reorder that would close a wait-for loop. The deadlock breaker
            // opts out: it is resolving a loop that already exists, and this test would
            // refuse the very promotion that clears it.
            if (guardCycles && WouldCloseCycle(mover.ReferenceID, blocker.ReferenceID))
            {
                if (Plugin.OvertakeLogging.Value)
                {
                    Plugin.Log.LogInfo(
                        $"Refused reorder {Tag(mover)} {mover.ReferenceID} ahead of {Tag(blocker)} "
                        + $"{blocker.ReferenceID}: would close a wait-for loop.");
                }
                sinceRefusals++;
                if (IsGA(mover)) sinceRefusalsGA++;
                return false;
            }

            // The ground the blocker is physically standing on: the node it has most
            // recently reached, back through the tail it has not yet released.
            committed.Clear();
            int target = Mathf.Clamp(blocker.TargetIndex, 0, blockerPath.nodes.Length - 1);
            int tail = Mathf.Max(0, target - PhysicalTail(blocker.am));
            for (int i = tail; i <= target; i++)
            {
                var node = blockerPath.nodes[i];
                if (node != null) committed.Add(node);
            }

            shared.Clear();
            string blockerID = blocker.ReferenceID;
            for (int i = Mathf.Max(0, mover.TargetIndex); i < path.nodes.Length; i++)
            {
                var node = path.nodes[i];
                if (node == null || !node.ContainsReference(blockerID)) continue;

                // Never wave the mover through ground the blocker is standing on, and
                // never apply the reorder to only part of what they share.
                if (committed.Contains(node)) return false;

                shared.Add(node);
            }

            if (shared.Count == 0) return false;

            string moverID = mover.ReferenceID;
            int changed = 0;
            for (int i = 0; i < shared.Count; i++)
            {
                if (InsertBefore(shared[i], moverID, blockerID)) changed++;
            }

            // Re-asserting an order that already holds is not an event. Without this the
            // same pair logs every sweep and the log becomes unreadable — 1563 head-on
            // lines for three aircraft in one session.
            lastChangedQueues = changed;
            if (changed == 0) return true;

            sinceOvertakes++;
            if (IsGA(mover)) sinceOvertakesGA++;
            if (waitsFor.TryGetValue(moverID, out var moverEdges)) moverEdges.Remove(blockerID);
            AddEdge(blockerID, moverID);

            settled[PairKey(moverID, blockerID)] = new Settled
            {
                Winner = moverID,
                Expires = Time.time + Plugin.OvertakeCooldown.Value
            };

            if (Plugin.OvertakeLogging.Value)
            {
                Plugin.Log.LogInfo(
                    $"Overtake: {Tag(mover)} {moverID} ahead of {Tag(blocker)} {blockerID} "
                    + $"across {shared.Count} shared node(s).");
            }

            return true;
        }

        private static bool InsertBefore(TaxiwayNode node, string mover, string target)
        {
            var queue = node.occupeeRefs;

            int moverAt = -1, targetAt = -1, i = 0;
            for (var cur = queue.First; cur != null; cur = cur.Next, i++)
            {
                if (moverAt < 0 && cur.Value == mover) moverAt = i;
                if (targetAt < 0 && cur.Value == target) targetAt = i;
            }

            // Both must be present, and there is nothing to do if the mover leads.
            if (moverAt < 0 || targetAt < 0 || moverAt < targetAt) return false;

            var moverNode = queue.Find(mover);
            var targetNode = queue.Find(target);
            if (moverNode == null || targetNode == null) return false;

            queue.Remove(moverNode);
            queue.AddBefore(targetNode, mover);
            return true;
        }
    }
}
