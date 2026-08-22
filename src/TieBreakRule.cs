namespace AdvancedTaxiways
{
    /// <summary>How a tie for the same node is settled.</summary>
    internal enum TieBreakRule
    {
        /// <summary>Mirror RunwayModel.SortQueue: emergency, runway crossing, GA last, most overdue arrival first.</summary>
        GameLadder,

        /// <summary>Departures beat routine arrivals. Emergencies and runway clearing still outrank them.</summary>
        DeparturesFirst,
    }
}
