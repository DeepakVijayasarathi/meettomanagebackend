namespace iucs.meettomanage.domain.Enums
{
    /// <summary>
    /// Where a platform lead is in the buying journey, from first enquiry to paying customer. Stored by name, so values
    /// must never be renamed. The older <see cref="StoreInquiryStatus"/> is kept in step for existing screens.
    /// </summary>
    public enum LeadStage
    {
        New = 0,
        Contacted = 1,
        DemoBooked = 2,
        DemoDone = 3,
        /// <summary>Signed up for a trial (matched to an academy account by e-mail).</summary>
        Trial = 4,
        /// <summary>Trial academy that has added students and run a live class.</summary>
        ActiveTrial = 5,
        /// <summary>On a paid plan.</summary>
        Customer = 6,
        Lost = 7,
    }
}
