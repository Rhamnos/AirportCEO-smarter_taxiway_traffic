# Advanced Taxiways

A BepInEx/Harmony mod for Airport CEO that fixes how aircraft reserve and contend for
taxiway nodes. Every rule here is built out of machinery the game already has and either
never calls or calls too narrowly.

**Status: in play testing.** The behaviour described below has been observed working, and
most of it was corrected in response to what testing showed. Standalone — no Mod Loader
dependency.

## Features

**Deferred route claims.** Vanilla reserves an aircraft's whole route to the runway the
moment it is plotted, then leaves it parked for up to three minutes waiting for a pushback
truck. Everything crossing that route queues behind an aeroplane that has not moved — most
visibly an arrival that stops dead at the runway exit just after landing. A waiting
aircraft now reserves only the ground it stands on and pushes back into, and claims the
rest when it starts rolling.

**Overtaking.** An aircraft blocked by one that reserved a node first can take its turn
back wherever there is physically room. Vanilla has this and it only fires when exactly one
aircraft is ahead of you, because `TaxiwayNode.SortUp` swaps adjacent queue entries only.

**Single-lane blocks.** Stretches of one-tile-wide taxiway are derived from the graph and
cleared one direction at a time, the way a ground controller clears a segment to a
hold-short point. Opposing traffic waits outside rather than meeting half way — the only
thing that prevents a head-on, since once two aircraft have met, no reordering frees
them.

**Tie-breaking.** When two aircraft contend for a node, vanilla resolves it by whichever
coroutine claimed it first. Precedence instead follows the ladder the game already applies
to its runway queue in `RunwayModel.SortQueue`.

**Deadlock breaking.** The wait-for graph is checked each sweep and any cycle is broken.

## Where it hooks the game

| What | Where | Why there |
|---|---|---|
| Deferred claim | `AircraftController.OccupyFullPath` prefix | The single place the whole route is reserved before the aircraft moves. Called only from the two pushback coroutines. |
| Claim on movement | `AircraftController.SetNextActivity` **prefix** | Clears `pathPreOccupied` so the game's own `OccupyPathTo` in `GoToPath` claims the remainder. Must be a prefix: the method body ends in `StartCoroutine`, which runs synchronously to the first yield, and `TaxiToRunway` has no yield before `GoToPath` for a deiced aircraft that already holds a path. Also how a restored save resumes (`Launch()`), which closes the load path for free. |
| Block rebuild | `TaxiwayController.UpdateAllTaxiwayNodes` postfix | Blocks are derived state, so they are invalidated wherever the game rebuilds the taxiway graph. |
| Overtaking and blocks | `TaxiOvertake`, a component, no patch | The blocking wait lives inside a compiler-generated iterator. Reordering queues from outside needs no transpiler: the coroutine simply finds `IsTravelable` true on its next tick. |

## Avoiding deadlock rather than recovering from it

Two rules, on top of blocks, and both come straight from how ground control works.

**Do not enter unless you can exit.** The yellow-box rule. An aircraft that takes a block
it cannot leave stops inside it, and the block is then held by traffic that is not moving —
which starves the opposite direction indefinitely, because the token only changes hands
once the owning side is done. So a direction that can actually get through the far end is
preferred when granting, and a block held by traffic that cannot clear it is released,
provided none of that traffic has entered yet. A route that simply *ends* inside the block
counts as leaving: it is parking, and a parked aircraft releases the taxiway.

**Refuse any reorder that would close a loop.** Before putting one aircraft ahead of
another, check whether the second already waits for the first by some other route — if it
does, the flip closes a cycle. The wait-for relation is rebuilt from the node queues each
sweep, and the direct edge between the pair is skipped during the search, because that is
precisely the edge the flip removes.

Two things stop that check strangling traffic, and both matter:

*The relation only extends `Precedence horizon` nodes ahead.* Built over a whole route it
records "will eventually queue behind" rather than "is waiting for", and a loop 200 m away
is not a deadlock — everyone involved will have moved, been re-ordered or passed long
before reaching it. Treating it as one is maximally conservative and leaves aircraft
sitting at crossings they could safely have taken. Anything past the horizon that does
materialise is the breaker's job.

*An aircraft that will be through before the other arrives skips the check entirely.* If it
clears the contested node first, the wait never forms, so no loop can close whatever the
graph currently says — the judgement a controller makes when they let you cross in front of
distant traffic. It needs both halves: being nearer is not enough, because an aircraft that
crosses and then stops *on* the crossing has made things worse, so the node past it has to
be available too.

The deadlock breaker opts out of this check. It exists to resolve a loop that already
exists, and the test would refuse the very promotion that clears it — prevention and
recovery want opposite answers to the same question.

## Why blocks, and why deadlock is the shape it is

Aircraft needing exclusive use of a sequence of nodes is textbook resource allocation, and
deadlock needs four conditions at once. Three of them are welded in by the simulation:
**mutual exclusion** (one aircraft per node), **hold and wait** (it sits somewhere while
waiting), and **no preemption** — *the game cannot reverse a taxiing aircraft*. Only
**circular wait** can be removed, so that is the entire problem.

This is also why detection-and-recovery can never be the answer. Recovery needs
preemption, and there is none. A real ground controller can call a tug and have an
aircraft towed backwards; we cannot, so prevention has to carry weight it does not have to
carry in real life.

**Vanilla was deadlock-free almost by accident.** Every queue was ordered by claim time —
one number per aircraft, the same everywhere. A total order is transitive, and a
transitive order has no cycles. Every reordering rule this mod adds replaces that with
*pairwise* comparisons ("who is nearer *this* node"), and pairwise comparisons form a
tournament, which is not transitive: A nearer than B at X, B nearer than C at Y, C nearer
than A at Z is three individually correct decisions forming a cycle. Making pairs
consistent across the nodes they share kills 2-cycles and does nothing about 3-cycles.
That is not a bug to patch — it is what pairwise ordering is.

**Blocks are the way out.** A block is a maximal run of degree-&lt;=2 nodes between
junctions, computed from `adjacentTaxiwayNodes`. Degree works as the test because
adjacency is 8-connected at 4 m cardinal and 6 m diagonal: a one-tile-wide taxiway has no
diagonal neighbours so its nodes come out at degree 2, while a two-wide one picks up a
side neighbour *and* a diagonal and lands at 3 or more.

Two properties follow:

- **Nobody commits partway into ground they cannot clear**, because the block is taken as
  a unit or not at all. That is what makes the remaining conflicts resolvable by ordering.
- **The decision is made once per block, not once per pair.** One owning direction is
  consistent by construction for every aircraft in that block, so the three-way
  disagreement that produced cycles cannot form there.

Ownership goes to whichever direction is already occupying the block — those aircraft
cannot back out — and otherwise to the nearest aircraft, with the protected tiers able to
override. Same-direction traffic is left alone; aircraft follow each other down a taxiway
perfectly happily.

## Rank settles a contest; it does not reserve ground in advance

The same misreading as lateness, in two more places, and it took a screenshot to see both.

`RunwayModel.SortQueue` rotates general aviation to the back of the runway queue. Turned
into a standing rank, that became an absolute veto: `blockerPriority.Beats(moverPriority)`
refused the overtake outright, so a commercial aircraft **anywhere** on a GA aircraft's
route blocked it for as long as it took to arrive, however far away it was. A higher rank
now only holds a node while it is within `Priority reach` of it — the same bound already
applied to the mover.

The head-on rule had it too. When neither aircraft had entered the shared stretch it
picked by rank first, so a distant commercial arrival won a crossing against a GA aircraft
sitting right on top of it. Approach is a question of who gets there first, not who
outranks whom.

## Lateness is a tie-break, not a tier

Worth recording, because getting this wrong was not obvious and the symptom was nowhere
near the cause. `RunwayModel.SortQueue` promotes "the most overdue non-GA arrival" to the
front, so the first version of the ladder made that its own tier.

That is a misreading. The game uses lateness to pick **one** aircraft out of a queue it is
already sorting — a relative test over a handful of entries. As a standing tier it
behaves completely differently, because an arrival taxiing to a stand landed at or after
its scheduled time and then spent minutes taxiing: `ScheduledTime < now` is true for
very nearly every one of them. So every arrival outranked every departure, and with an
80 m priority reach each aircraft turning off towards a stand halted the main taxiway
behind it.

Lateness now compares only within a tier, and only when the gap exceeds `Delay deadband`
(15 minutes). Without the deadband every pair still has a winner by a fraction of a
minute, and the override fires just as constantly.

## Why three aircraft need a deadlock breaker and two do not

Precedence starts as a total order — claim time — which is transitive, so no cycle can
exist. Applying a reorder to every node a pair shares keeps that pair's precedence
single-valued, which is what rules out two aircraft deadlocking each other. It does not
rule out three, because **flipping one pair inside a total order can create a
three-cycle**: from A < B < C, flipping A against C leaves C < A < B < C.

That materialises on a triangular junction or a ring taxiway. A's route uses N1 then N2,
B's uses N2 then N3, C's uses N3 then N1; each pair shares exactly one node, so pairwise
consistency is trivially satisfied and constrains nothing. With claim order alone the
cycle is unreachable — it would require t_B < t_A < t_C < t_B — which is why vanilla is
safe here. One overtake supplies the missing edge, and then A waits on B waits on C
waits on A, with no recovery, because `enableAircraftPathTimeout` ships off.

Checking for a cycle before allowing an overtake does not work: in the case above, at
the moment of the flip A is not yet blocked by B, and only becomes so once it advances
and its look-ahead window reaches N2. The cycle forms later than the decision that
enables it.

So the sweep builds the wait-for graph instead. Every blocked aircraft waits on exactly
one other — the head of the queue at its look-ahead node — so the graph is functional
and a plain walk finds its cycles. Each cycle is broken by promoting a single member
past its blocker; breaking one edge is enough, since that aircraft then moves, releases,
and the chain unwinds behind it. Candidates are tried in rank order and then by
proximity, because the committed-window rule can still refuse a promotion. If every
candidate is refused, the aircraft are physically gridlocked and no reordering can help;
that is logged rather than papered over.

This is worth running even with overtaking disabled. The game creates the same
inconsistency itself, through the single-node `SortUp` at `AircraftController.cs:2022`
and four `priority: true` claims.

## Known gaps

- Not runtime tested.
- Deferring the claim splits it into two events — the prefix at plot time, the
  remainder at movement start — where vanilla made one. Two aircraft can therefore end
  up ordered differently on different shared nodes, which vanilla's single atomic claim
  ruled out. The geometry limits it: an aircraft's look-ahead at rest lies entirely
  inside its own prefix, so it always gets clear of the stand before it can be blocked.
  Vanilla already breaks the same ordering in five other places (`SortUp`, and four
  `priority: true` claims), so this is one more of a kind the game survives rather than
  a new class of failure. `TaxiwayController.enableAircraftPathTimeout` is the recovery
  hatch and ships off; turn it on if aircraft wedge.
- Deadlock is detected and broken, not prevented. A cycle can exist for up to one sweep
  before it is cleared.
- Head-on exclusion is pairwise. Three aircraft meeting in a Y of single-lane corridors
  would be decided by three independent pairwise calls, which need not agree. The
  wait-for cycle breaker is the backstop, but it can only promote — it cannot move an
  aircraft that has already committed to a corridor.
- The exclusion pass scans a fixed node distance ahead. A corridor longer than the scan
  window is only noticed once an aircraft is inside it.
- The sweep runs every half second of real time, so an aircraft can stay blocked that
  long after it becomes eligible to pass.
- A parked aircraft no longer blocks demolition of its whole unstarted route, only of
  its reservation window (`DemolitionController` gates on `GetOccupiedCount`). Removing
  a node out from under a plotted path is already handled — `TaxiwayNodeExist` fires
  `OnPathAborted` and the aircraft re-plots.
- `PathRequestManagerAircraft.ValidateThatPathExists` is a separate BFS that applies
  none of the neighbour filters — not size, direction, or ramp rules. It backs the
  hangar/deicing connectivity checks, so those report physical connectivity rather
  than what an aircraft would actually be allowed to do.

## Building

Needs the .NET SDK. The path to the game is set in
`src/AdvancedTaxiways.csproj`.

    cd src && dotnet build

Output: `src/bin/Debug/AirportCEOAdvancedTaxiways.dll`
