using System.Security.Cryptography;

namespace iucs.meettomanage.application.Helper
{
    /// <summary>
    /// Generates the initial 4-digit login PIN emailed to admin-created accounts,
    /// and the replacement PIN when an admin resends credentials. Self-service
    /// resets (<see cref="Services.AuthService.ResetPinAsync"/>) take the PIN the
    /// user chooses instead — this generator is only for admin-issued PINs.
    /// </summary>
    public static class TemporaryPinGenerator
    {
        public static string Generate() => RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
    }
}
