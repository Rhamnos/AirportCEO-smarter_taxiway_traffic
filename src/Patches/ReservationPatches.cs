using HarmonyLib;
using Nodes;

namespace AdvancedTaxiways.Patches
{
    /// <summary>
    /// Vanilla stakes a claim on an aircraft's entire departure route the moment that
    /// route is plotted. <c>AircraftController.OccupyFullPath</c> is called from
    /// PushBack and appends the aircraft to the occupancy queue of every node between
    /// its stand and the runway. Only then does it wait for the first node to clear,
    /// wait up to 180 seconds for a pushback truck, perform the physical pushback and
    /// sit through the engine-start delays. GoToPath — the thing that actually moves
    /// it along those nodes — does not run until TaxiToRunway, minutes later.
    ///
    /// For all of that time a parked aircraft owns ground it will not reach for
    /// minutes, and anything that wants to cross it queues up behind. The symptom that
    /// prompted this is an arrival stopping dead just after landing: VacateRunway
    /// releases every node the arrival held and reports the runway clear, TaxiToStand
    /// re-plots and re-queues it at the BACK of every node's list, and it loses the
    /// runway exit to an aeroplane that has been parked in the ramp the whole time.
    ///
    /// The fix is to claim only what the aircraft needs to stand on and push back
    /// into, and let the game's own claim inside GoToPath take the rest at the moment
    /// it starts rolling.
    ///
    /// Nothing here changes how far an aircraft looks ahead, or how long it keeps
    /// nodes behind it. The separation counts are read to size the reservation; they
    /// are never written.
    /// </summary>
    [HarmonyPatch(typeof(AircraftController), "OccupyFullPath")]
    internal static class DeferRouteClaimPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(AircraftController __instance, TaxiwayNode[] taxiwayPath)
        {
            if (!Plugin.DeferRouteClaim.Value) return true;
            if (taxiwayPath == null || taxiwayPath.Length == 0) return true;

            var am = __instance.am;
            if (am == null) return true;

            // The window an aircraft occupies once it is taxiing: everything it still
            // holds behind it, plus everything it checks in front. Reusing the game's
            // own per-weight-class counts means a Large reserves more standing ground
            // than a Small, in the same proportion it does while moving, and means
            // this stays correct if those counts are ever retuned.
            int window = am.lookAheadCount + am.lookBackCount;

            if (Plugin.LogClaims.Value)
            {
                Plugin.Log.LogInfo(
                    $"Deferring claim: {__instance.ReferenceID} {am.weightClass} "
                    + $"lookAhead={am.lookAheadCount} lookBack={am.lookBackCount} window={window} "
                    + $"pathLen={taxiwayPath.Length} action={am.AircraftAction}");
            }

            if (window < 1) return true;

            // Short route: there is nothing to defer, so leave vanilla alone rather
            // than reimplementing it.
            if (taxiwayPath.Length <= window) return true;

            string referenceID = __instance.ReferenceID;
            int claimed = 0;
            for (int i = 0; i < window; i++)
            {
                if (taxiwayPath[i] == null) continue;
                taxiwayPath[i].AddToOccupeeList(referenceID, priority: false);
                claimed++;
            }

            if (Plugin.LogClaims.Value)
            {
                Plugin.Log.LogInfo($"Deferred claim: {__instance.ReferenceID} reserved {claimed} of {window} nodes");
            }

            return false;
        }
    }

    /// <summary>
    /// Hands the rest of the claim back to the game.
    ///
    /// <c>pathPreOccupied</c> is the flag PushBack sets to tell GoToPath "this route
    /// is already claimed, do not claim it again". Clearing it as the aircraft enters
    /// a taxi state means GoToPath runs its own
    /// <c>OccupyPathTo(path.nodes, path.nodes.Length, priority: false, TargetIndex)</c>
    /// instead — the full remaining route, claimed in one go, at the moment movement
    /// begins. Doing it with the game's own call rather than a reimplementation keeps
    /// the queue semantics identical, and claiming the whole remainder atomically is
    /// what keeps node queues consistently ordered between any two moving aircraft.
    ///
    /// This is safe to apply unconditionally. <c>AddToOccupeeList</c> ignores an
    /// aircraft already present, so a route that really was fully claimed — a save
    /// restored mid-taxi sets the same flag — keeps every queue position it had and
    /// merely re-asserts them.
    ///
    /// It must be a PREFIX, not a postfix. <c>SetNextActivity</c>'s body ends in
    /// <c>StartCoroutine</c>, and Unity runs a coroutine synchronously up to its first
    /// yield. TaxiToRunway has no yield before <c>GoToPath</c> when the aircraft has
    /// been deiced and already holds a path — <c>PlotPath</c> returns early on
    /// <c>HasPath</c>, leaving <c>pathRequestInProgress</c> false — so GoToPath would
    /// read the flag while it was still true and skip the claim entirely. A postfix
    /// runs too late to matter in that case.
    /// </summary>
    [HarmonyPatch(typeof(AircraftController), "SetNextActivity")]
    internal static class ClaimRouteOnMovementPatch
    {
        private static readonly AccessTools.FieldRef<AircraftController, bool> PathPreOccupied =
            AccessTools.FieldRefAccess<AircraftController, bool>("pathPreOccupied");

        [HarmonyPrefix]
        private static void Prefix(AircraftController __instance, Enums.AircraftAction newAction)
        {
            if (!Plugin.DeferRouteClaim.Value) return;

            switch (newAction)
            {
                // Every state that goes on to follow a node path via GoToPath.
                case Enums.AircraftAction.TaxiingToRunway:
                case Enums.AircraftAction.TaxiingToHangar:
                case Enums.AircraftAction.TaxiingToDeicingStand:
                case Enums.AircraftAction.TaxiingToStand:
                    // Only a handback that actually releases a pre-occupied route is an
                    // event. Clearing an already-false flag is a no-op and logging it just
                    // pads the log with lines that mean nothing happened.
                    if (PathPreOccupied(__instance))
                    {
                        if (Plugin.LogClaims.Value)
                        {
                            Plugin.Log.LogInfo($"Claim handback: {__instance.ReferenceID} -> {newAction}");
                        }
                        PathPreOccupied(__instance) = false;
                    }
                    break;
            }
        }
    }
}
