using System.Collections.Generic;
using HarmonyLib;
using Nodes;
using UnityEngine;

namespace AdvancedTaxiways
{
    /// <summary>
    /// Gate holds: keeping an aircraft parked at its stand while something is coming the
    /// other way down the only route it has.
    ///
    /// This exists because the physical pushback is **unguarded**.
    /// <c>AircraftController.GoToPositionDirect</c> reverses the aircraft to its push
    /// position without consulting occupancy at all, so once a pushback starts, nothing
    /// downstream can arbitrate it — the aeroplane is simply in the connector. The only
    /// enforcement point is the gate before it,
    /// <c>while (!firstNode.IsTravelable(ReferenceID))</c> at AircraftController.cs:1145.
    ///
    /// That gate asks about exactly one node: the pushback's own first node, beside its own
    /// stand. An arrival heading for a *different* stand need not use that node at all, so
    /// the gate passes, the aircraft reverses out, and the head-on is physical before any
    /// rule gets a say. Extending what the gate knows is the whole fix.
    ///
    /// Holding here is uniquely cheap. An aircraft waiting at a stand blocks nothing;
    /// anywhere else, holding one occupies taxiway and creates the next conflict. That
    /// asymmetry is why real ramp control does gate holds, and why being conservative at
    /// this one point costs almost nothing.
    ///
    /// Implemented as a postfix rather than by inserting fake occupees: no queue mutation,
    /// nothing written into saves, and it only ever affects the one aircraft at the one
    /// node it names.
    /// </summary>
    internal static class PushbackHolds
    {
        private struct Hold
        {
            public TaxiwayNode Node;
            public float Expires;
            public float Since;
        }

        private static readonly Dictionary<string, Hold> holds = new Dictionary<string, Hold>();
        private static readonly List<string> expired = new List<string>();

        public static int Count => holds.Count;

        /// <summary>
        /// Hold this aircraft at this node. Refreshed every sweep while the conflict lasts,
        /// so it lapses on its own about a second after the conflict clears — no explicit
        /// release, and nothing left behind if the aircraft disappears.
        /// </summary>
        public static void Apply(string referenceID, TaxiwayNode node)
        {
            if (string.IsNullOrEmpty(referenceID) || node == null) return;

            float now = Time.time;
            if (!holds.TryGetValue(referenceID, out var hold) || hold.Node != node)
            {
                hold = new Hold { Node = node, Since = now };
            }

            hold.Expires = now + Plugin.OvertakeInterval.Value * 3f;
            holds[referenceID] = hold;

            if (now - hold.Since > Plugin.MaxPushbackHold.Value
                && ShouldReport(referenceID))
            {
                Plugin.Log.LogWarning(
                    $"Pushback held at the stand for over {Plugin.MaxPushbackHold.Value:F0}s: {referenceID}. "
                    + "Something keeps coming the other way down its only route. Still holding — releasing it "
                    + "into oncoming traffic would create the head-on this prevents — but the ramp may be "
                    + "starved of departures.");
            }
        }

        /// <summary>
        /// Has this aircraft yielded for longer than anyone should be asked to?
        ///
        /// The rule that a parked pushback always gives way is right almost always, but on
        /// a ramp with a steady stream of arrivals it means a stand is never released. Once
        /// a departure has waited this long it stops automatically losing and is judged on
        /// the normal ladder instead. It still cannot beat an aircraft that is physically
        /// committed — that check comes first and is not affected — so this frees a starved
        /// stand without ever creating the head-on the yield rule exists to prevent.
        /// </summary>
        public static bool HeldTooLong(string referenceID)
        {
            return holds.TryGetValue(referenceID, out var hold)
                   && Time.time - hold.Since > Plugin.MaxPushbackHold.Value;
        }

        public static bool IsHeld(string referenceID, TaxiwayNode node)
        {
            return holds.TryGetValue(referenceID, out var hold)
                   && hold.Node == node
                   && Time.time < hold.Expires;
        }

        public static void Prune()
        {
            if (holds.Count == 0) return;

            expired.Clear();
            float now = Time.time;
            foreach (var entry in holds)
            {
                if (entry.Value.Expires <= now) expired.Add(entry.Key);
            }
            for (int i = 0; i < expired.Count; i++) holds.Remove(expired[i]);
        }

        private static readonly Dictionary<string, float> reported = new Dictionary<string, float>();

        private static bool ShouldReport(string referenceID)
        {
            if (reported.TryGetValue(referenceID, out float quiet) && Time.time < quiet) return false;
            reported[referenceID] = Time.time + Plugin.WarnRepeatSeconds.Value;
            return true;
        }
    }

    /// <summary>
    /// Makes vanilla's own pushback gate honour a hold. Costs a dictionary miss per call
    /// when nothing is held, which is the normal case.
    /// </summary>
    [HarmonyPatch(typeof(TaxiwayNode), nameof(TaxiwayNode.IsTravelable))]
    internal static class PushbackGatePatch
    {
        [HarmonyPostfix]
        private static void Postfix(TaxiwayNode __instance, string referenceID, ref bool __result)
        {
            if (!__result || PushbackHolds.Count == 0) return;
            if (PushbackHolds.IsHeld(referenceID, __instance)) __result = false;
        }
    }
}
