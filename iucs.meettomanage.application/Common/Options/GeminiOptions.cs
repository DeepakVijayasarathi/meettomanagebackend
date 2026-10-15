namespace iucs.meettomanage.application.Common.Options
{
    /// <summary>
    /// Binds the "Gemini" config section, used by the public marketing-site sales assistant
    /// (POST /api/marketing/chat). ApiKey must never be set in appsettings.json (it's committed
    /// to git) — it comes from user-secrets locally and the Gemini__ApiKey environment variable
    /// in prod. Left blank, the chat endpoint answers 502 and the site falls back to its
    /// WhatsApp/email contact options.
    /// </summary>
    public class GeminiOptions
    {
        public const string SectionName = "Gemini";

        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Gemini model id, e.g. "gemini-flash-latest" (Google's rolling alias for its current Flash model).</summary>
        public string Model { get; set; } = "gemini-flash-latest";

        /// <summary>Tried once when <see cref="Model"/> is overloaded or rate-limited (503/429/500), so a
        /// demand spike on Google's side doesn't leave a visitor without an answer. Blank disables it.</summary>
        public string FallbackModel { get; set; } = "gemini-flash-lite-latest";
    }
}
