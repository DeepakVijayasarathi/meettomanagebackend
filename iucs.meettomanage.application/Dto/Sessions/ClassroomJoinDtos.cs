namespace iucs.meettomanage.application.Dto.Sessions
{
    /// <summary>Everything the live-classroom screen needs to embed the Jitsi room for one caller.</summary>
    public class JitsiJoinDto
    {
        public string Room { get; set; } = null!;

        public string Domain { get; set; } = null!;

        /// <summary>Null when the deployment hasn't been configured for token-verified joins yet.</summary>
        public string? Token { get; set; }
    }

    /// <summary>Non-secret Jitsi settings the classroom screen needs before it joins.</summary>
    public class ClassroomSettingsDto
    {
        public string Domain { get; set; } = null!;

        public bool AutoRecordEnabled { get; set; }
    }
}
