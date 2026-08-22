using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace AdvancedTaxiways
{
    [BepInPlugin(Guid, "AirportCEO Advanced Taxiways", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "org.airportceoadvancedtaxiways.farber";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> DeferRouteClaim;
        internal static ConfigEntry<bool> LogClaims;
        internal static ConfigEntry<bool> OvertakeEnabled;
        internal static ConfigEntry<float> PriorityReach;
        internal static ConfigEntry<int> PrecedenceHorizon;
        internal static ConfigEntry<float> CrossAheadMargin;
        internal static ConfigEntry<float> DelayDeadband;
        internal static ConfigEntry<float> OvertakeInterval;
        internal static ConfigEntry<bool> OvertakeLogging;
        internal static ConfigEntry<TieBreakRule> TieBreak;
        internal static ConfigEntry<float> OvertakeCooldown;
        internal static ConfigEntry<bool> DeadlockBreaker;
        internal static ConfigEntry<float> WarnRepeatSeconds;
        internal static ConfigEntry<bool> OpposingRuns;
        internal static ConfigEntry<int> OpposingScanNodes;
        internal static ConfigEntry<int> MinBlockNodes;
        internal static ConfigEntry<float> RebuildDelay;
        internal static ConfigEntry<float> SweepBudgetMs;
        internal static ConfigEntry<float> BlockHoldMargin;
        internal static ConfigEntry<float> MaxPushbackHold;
        internal static ConfigEntry<float> SummaryInterval;
        internal static ConfigEntry<int> TailSmall;
        internal static ConfigEntry<int> TailMedium;
        internal static ConfigEntry<int> TailLarge;

        private void Awake()
        {
            Log = Logger;
            BindConfig();

            var harmony = new Harmony(Guid);
            harmony.PatchAll();
            gameObject.AddComponent<TaxiOvertake>();

            Log.LogInfo("Advanced Taxiways loaded.");
        }

        private void BindConfig()
        {
            DeferRouteClaim = Config.Bind("Reservations", "Claim the route only when moving", true,
                "Vanilla reserves an aircraft's whole route to the runway the moment it is plotted, " +
                "then leaves it parked for up to three minutes waiting for a pushback truck. Anything " +
                "crossing that route queues behind an aeroplane that has not moved — most visibly an " +
                "arrival that stops at the runway exit right after landing. With this on, a waiting " +
                "aircraft reserves only the ground it is standing on and pushing back into, and claims " +
                "the rest when it actually starts rolling. Separation while taxiing is unchanged.");

            OvertakeEnabled = Config.Bind("Overtaking", "Enabled", true,
                "Let a taxiing aircraft take its turn back from an aircraft that reserved the node " +
                "first but is nowhere near it. The game has this and it only works when exactly one " +
                "aircraft is ahead of you, because it swaps adjacent queue entries only.");

            DelayDeadband = Config.Bind("Overtaking", "Delay deadband", 15f,
                "How many minutes later than the other aircraft a flight must be running before its " +
                "lateness counts for anything. Nearly every arrival is technically past its scheduled " +
                "time by the point it is taxiing, so without a deadband the rank override fires " +
                "constantly and every aircraft heading for a stand halts the main taxiway.");

            PrecedenceHorizon = Config.Bind("Overtaking", "Precedence horizon", 0,
                new ConfigDescription(
                    "Extra nodes past an aircraft's look-ahead to include in the loop check. Zero means " +
                    "only ground it has actually asked for counts as a wait, which is how a database " +
                    "builds a wait-for graph — a lock not yet requested contributes no edge. Counting " +
                    "reserved-but-unrequested nodes makes almost every reorder look like it closes a " +
                    "loop, and traffic stops. Raise it to catch loops earlier at the cost of refusing " +
                    "safe passes; the deadlock breaker handles whatever forms beyond it.",
                    new AcceptableValueRange<int>(0, 100)));

            CrossAheadMargin = Config.Bind("Overtaking", "Cross-ahead margin", 2f,
                new ConfigDescription(
                    "How many times further away the blocker must be before an aircraft is allowed to " +
                    "cross in front of it regardless of the loop check — it will be through and gone " +
                    "before the other arrives, so no wait ever forms. It must also have somewhere to go " +
                    "past the crossing, or it would stop on it and make things worse.",
                    new AcceptableValueRange<float>(1.2f, 10f)));

            PriorityReach = Config.Bind("Overtaking", "Priority reach", 80f,
                new ConfigDescription(
                    "How close a higher-priority aircraft must be to a contested node, in metres, " +
                    "before a lower-priority one holds for it. Beyond this the nearer aircraft goes " +
                    "first regardless of rank, so nobody waits at a junction for traffic on the far " +
                    "side of the airport. 80 m is about 20 nodes, or 8 seconds of taxi. Emergencies " +
                    "and aircraft clearing a runway ignore this and take precedence at any distance.",
                    new AcceptableValueRange<float>(10f, 400f)));

            OvertakeInterval = Config.Bind("Overtaking", "Check interval", 0.5f,
                new ConfigDescription(
                    "Seconds between sweeps for blocked aircraft. Lower reacts sooner and costs more.",
                    new AcceptableValueRange<float>(0.1f, 5f)));

            LogClaims = Config.Bind("Reservations", "Log claims", false,
                "Write a line whenever a route claim is deferred or handed back. Diagnostic; " +
                "defaults on while this is being tested, turn it off once behaviour is confirmed.");

            TieBreak = Config.Bind("Overtaking", "Tie-break rule", TieBreakRule.GameLadder,
                "How to settle two aircraft reaching a node together, which vanilla decides by " +
                "whichever coroutine claimed the node first. GameLadder mirrors RunwayModel.SortQueue, " +
                "the ladder the game already applies to its runway queue: emergency, then anything " +
                "crossing or clearing a runway, then general aviation demoted to the back, then the " +
                "most overdue non-GA arrival. Departures get no standing advantage there — the game " +
                "gives them an alternating turn, not priority. DeparturesFirst is the simpler " +
                "alternative: departures beat routine arrivals outright.");

            OvertakeCooldown = Config.Bind("Overtaking", "Cooldown", 5f,
                new ConfigDescription(
                    "Seconds during which a pair that has just been reordered will not be reordered " +
                    "back. Stops two aircraft crossing at two separate nodes from flipping precedence " +
                    "on every sweep.",
                    new AcceptableValueRange<float>(0f, 60f)));

            // Separate per class on purpose. A single multiplier moves all three together,
            // which flattens the differences between weight classes rather than tuning one.
            // Vanilla's look-back is 5 / 6 / 8 nodes; nodes are 4 m apart.
            TailSmall = Config.Bind("Blocks", "Tail nodes, small", 5,
                new ConfigDescription(
                    "Ground behind a small aircraft treated as physically occupied, in 4 m nodes. This " +
                    "is what stops another aircraft being cleared into space one is standing in. 5 matches " +
                    "the game's own figure. Lower it if small aircraft leave more room behind them than " +
                    "you like; raise it if they ever clip.",
                    new AcceptableValueRange<int>(1, 20)));

            TailMedium = Config.Bind("Blocks", "Tail nodes, medium", 6,
                new ConfigDescription("As above for medium aircraft. Vanilla's look-back is 6.",
                    new AcceptableValueRange<int>(1, 20)));

            TailLarge = Config.Bind("Blocks", "Tail nodes, large", 8,
                new ConfigDescription("As above for large aircraft. Vanilla's look-back is 8.",
                    new AcceptableValueRange<int>(1, 20)));

            MaxPushbackHold = Config.Bind("Blocks", "Pushback hold warning", 90f,
                new ConfigDescription(
                    "Seconds a pushback may be held at its stand before a warning is logged. It keeps " +
                    "holding after that — releasing it into oncoming traffic would create the head-on " +
                    "the hold exists to prevent — but a ramp starved of departures is worth knowing about.",
                    new AcceptableValueRange<float>(10f, 600f)));

            BlockHoldMargin = Config.Bind("Blocks", "Hold margin", 2f,
                new ConfigDescription(
                    "How long one direction may keep a block, as a multiple of the time needed to " +
                    "traverse it. Derived from the block's own length rather than fixed, so it stays " +
                    "correct on a 5-node segment and a 100-node one alike — a flat number either hands " +
                    "the token over mid-traverse on long corridors or starves the other side on short " +
                    "ones. Nodes are 4 m apart at 10 m/s, so a block costs 0.4 s per node to clear.",
                    new AcceptableValueRange<float>(1f, 20f)));

            SummaryInterval = Config.Bind("Blocks", "Summary interval", 60f,
                new ConfigDescription(
                    "Seconds between summary lines reporting activity per aircraft-hour of taxiing. " +
                    "Raw counts cannot be compared between sessions or airports — two deadlocks in a " +
                    "quiet hour and two in a saturated one mean opposite things. 0 disables it.",
                    new AcceptableValueRange<float>(0f, 600f)));

            SweepBudgetMs = Config.Bind("Overtaking", "Time budget", 4f,
                new ConfigDescription(
                    "Milliseconds this mod may spend per pass before stopping and finishing next time. " +
                    "A safety valve for very large airports, where the work grows with both aircraft " +
                    "count and route length. Lower it if you see stutter that tracks traffic volume.",
                    new AcceptableValueRange<float>(1f, 50f)));

            RebuildDelay = Config.Bind("Blocks", "Rebuild delay", 3f,
                new ConfigDescription(
                    "Seconds of quiet after the last taxiway edit before the layout is re-analysed. " +
                    "Real seconds, not game time - this is waiting for you to stop building, which has " +
                    "nothing to do with game speed. A whole drag collapses into one rebuild whatever " +
                    "this is set to; a longer value only delays when single-lane handling comes back, " +
                    "so there is little reason to raise it.",
                    new AcceptableValueRange<float>(0.5f, 60f)));

            MinBlockNodes = Config.Bind("Blocks", "Minimum block length", 2,
                new ConfigDescription(
                    "Shortest run of single-lane taxiway treated as a block. A single node between two " +
                    "junctions needs no special handling — whoever reaches it first passes straight " +
                    "through — so the default ignores those.",
                    new AcceptableValueRange<int>(1, 20)));

            OpposingScanNodes = Config.Bind("Blocks", "Junction scan distance", 40,
                new ConfigDescription(
                    "How many nodes ahead to look for oncoming traffic outside single-lane blocks. " +
                    "Junction nodes belong to no block, but the game still allows only one aircraft " +
                    "per node — so a ramp connector built of junctions is exactly as unpassable as a " +
                    "corridor. 40 nodes is 160 m. Raise it if head-on gridlock still forms at junctions.",
                    new AcceptableValueRange<int>(10, 200)));

            OpposingRuns = Config.Bind("Blocks", "Single-lane exclusion", true,
                "Clear aircraft through a single-lane block one direction at a time, the way a ground " +
                "controller clears a taxiway segment to a hold-short point. Whichever direction is " +
                "already occupying the block owns it — an aircraft in there cannot back out, since the " +
                "game has no tug — otherwise the nearest aircraft decides. Opposing traffic waits " +
                "outside the block rather than meeting it half way, which is the only thing that can " +
                "prevent a head-on: once two aircraft have met, no reordering frees them.");

            WarnRepeatSeconds = Config.Bind("Overtaking", "Warning repeat interval", 30f,
                new ConfigDescription(
                    "Seconds before the same gridlock warning is allowed to repeat. A gridlock persists, " +
                    "so an unthrottled warning fires on every sweep and buries the lines worth reading.",
                    new AcceptableValueRange<float>(1f, 600f)));

            DeadlockBreaker = Config.Bind("Overtaking", "Break deadlocks", true,
                "Each sweep, build the wait-for graph and break any cycle found by promoting one " +
                "member past its blocker. Pairwise ordering rules out two aircraft deadlocking each " +
                "other but not three, and the game has no recovery of its own — its path timeout " +
                "ships disabled. Worth leaving on even with overtaking off, since the game creates " +
                "the same inconsistency through its own single-node SortUp. Logs a warning when it fires.");

            OvertakeLogging = Config.Bind("Overtaking", "Log overtakes", false,
                "Write a line to the log for every routine decision - who went first at a junction, who was held " +
                "at a stand. Off by default because it is thousands of lines an hour. Turn it on before " +
                "reproducing a problem you want to report. Warnings about aircraft that genuinely could " +
                "not be freed are always logged either way.");

        }
    }
}
