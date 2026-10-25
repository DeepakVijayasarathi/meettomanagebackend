namespace iucs.meettomanage.application.Common.Options
{
    /// <summary>
    /// Server-side Hetzner Inference configuration for the public marketing-site assistant.
    /// The API key must come from user-secrets or HetznerAi__ApiKey, never committed settings.
    /// </summary>
    public class HetznerAiOptions
    {
        public const string SectionName = "HetznerAi";

        public string ApiKey { get; set; } = string.Empty;
        public string Endpoint { get; set; } = "https://inference.hetzner.com/api/v1";
        public string Model { get; set; } = "Qwen/Qwen3.6-35B-A3B-FP8";
        public string FallbackModel { get; set; } = string.Empty;
    }
}
