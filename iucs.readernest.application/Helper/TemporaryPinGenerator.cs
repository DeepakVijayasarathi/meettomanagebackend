using System.Security.Cryptography;

namespace iucs.readernest.application.Helper
{
    /// <summary>
    /// Generates the initial 4-digit login PIN emailed to admin-created accounts,
    /// and the replacement PIN when an admin resends credentials. There is no
    /// self-service PIN change yet — an admin resends to issue a new one.
    /// </summary>
    public static class TemporaryPinGenerator
    {
        public static string Generate() => RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
    }
}
