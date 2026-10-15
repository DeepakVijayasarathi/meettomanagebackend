namespace iucs.meettomanage.domain.Enums
{
    /// <summary>
    /// Platform roles. Academic Coordinator and Management are Sub Admin permission presets,
    /// not separate roles. Students participate through the Parent account.
    /// </summary>
    public enum UserRole
    {
        Admin,
        SubAdmin,
        AdmissionTeam,
        Teacher,
        Parent,
        /// <summary>
        /// The platform owner's marketing-site team (demo requests, blog, SEO, analytics). Separate
        /// login at /marketing/login; never listed or manageable from the academy's own portals,
        /// and Admin's permission bypass does not reach the marketing endpoints. Seeded from
        /// Seed:MarketingEmail / Seed:MarketingPin, not created in-app.
        /// </summary>
        Marketing
    }
}
