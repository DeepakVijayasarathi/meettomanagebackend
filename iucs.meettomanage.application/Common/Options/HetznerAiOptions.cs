namespace iucs.meettomanage.application.Common.Options
{
    /// <summary>
    /// Server-side AI provider configuration (section "HetznerAi"). Every value comes from
    /// configuration (appsettings / environment, e.g. HetznerAi__Endpoint); nothing is hardcoded.
    /// The API key must come from user-secrets or HetznerAi__ApiKey, never committed settings.
    /// </summary>
    public class HetznerAiOptions
    {
        public const string SectionName = "HetznerAi";

        /// <summary>Master switch for the server-configured assistants (marketing site, Mito).</summary>
        public bool Enabled { get; set; }
        public string ApiKey { get; set; } = string.Empty;
        /// <summary>Provider base URL. Also the default endpoint offered for the portal chatbot.</summary>
        public string Endpoint { get; set; } = string.Empty;
        /// <summary>Primary model. Also the default model offered for the portal chatbot.</summary>
        public string Model { get; set; } = string.Empty;
        public string FallbackModel { get; set; } = string.Empty;
        /// <summary>Default system prompt offered for the portal chatbot.</summary>
        public string DefaultSystemPrompt { get; set; } = string.Empty;
        /// <summary>Optional rewrite applied to every outgoing endpoint (e.g. a provider URL migration).</summary>
        public EndpointRewriteOptions EndpointRewrite { get; set; } = new();
        /// <summary>
        /// The platform marketing site's own provider settings (section HetznerAi:Marketing). Each blank value falls
        /// back to the main setting, so the marketing site can run on a separate key without repeating the rest.
        /// </summary>
        public MarketingAiOptions Marketing { get; set; } = new();

        /// <summary>Settings for the marketing site: its own values where set, otherwise the main ones.</summary>
        public HetznerAiOptions ForMarketing()
        {
            static string Pick(string own, string main) => string.IsNullOrWhiteSpace(own) ? main : own.Trim();
            return new HetznerAiOptions
            {
                Enabled = Enabled && Marketing.Enabled,
                ApiKey = Pick(Marketing.ApiKey, ApiKey),
                Endpoint = Pick(Marketing.Endpoint, Endpoint),
                Model = Pick(Marketing.Model, Model),
                FallbackModel = Pick(Marketing.FallbackModel, FallbackModel),
                DefaultSystemPrompt = DefaultSystemPrompt,
                EndpointRewrite = EndpointRewrite,
            };
        }

        /// <summary>True only when enabled and the endpoint, model and key are all configured.</summary>
        public bool IsConfigured => Enabled
            && !string.IsNullOrWhiteSpace(ApiKey)
            && !string.IsNullOrWhiteSpace(Endpoint)
            && !string.IsNullOrWhiteSpace(Model);

        /// <summary>Applies <see cref="EndpointRewrite"/> when it is enabled and fully configured.</summary>
        public string ResolveEndpoint(string? endpoint)
        {
            var value = endpoint?.Trim() ?? string.Empty;
            var rewrite = EndpointRewrite;
            if (!rewrite.Enabled || string.IsNullOrWhiteSpace(rewrite.From) || string.IsNullOrWhiteSpace(rewrite.To)) return value;
            return value.Replace(rewrite.From.Trim(), rewrite.To.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public class MarketingAiOptions
    {
        /// <summary>Switches the marketing site's assistants off without affecting academies.</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Never committed: HetznerAi__Marketing__ApiKey in production.</summary>
        public string ApiKey { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string FallbackModel { get; set; } = string.Empty;
    }

    public class EndpointRewriteOptions
    {
        public bool Enabled { get; set; }
        /// <summary>URL prefix to replace, e.g. a retired provider base URL.</summary>
        public string From { get; set; } = string.Empty;
        /// <summary>Replacement URL prefix.</summary>
        public string To { get; set; } = string.Empty;
    }
}
