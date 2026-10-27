namespace PoFightJudge.Shared.Configuration;

/// <summary>
/// Every configuration key the solution reads, in one place. Keys under <c>PoFightJudge:</c> map to Key Vault
/// secrets by replacing <c>:</c> with <c>--</c> (<c>PoFightJudge:GeminiApiKey</c> → <c>PoFightJudge--GeminiApiKey</c>).
/// A literal key string anywhere else is a defect.
/// </summary>
public static class ConfigKeys
{
    public const string Root = "PoFightJudge";

    /// <summary>Supplied by <c>appsettings.Development.json</c> locally and an App Service setting in Azure — never by source.</summary>
    public static class KeyVault
    {
        public const string Uri = "KeyVault:Uri";
    }

    public static class AzureAd
    {
        public const string Section = $"{Root}:AzureAd";
        public const string TenantId = $"{Section}:TenantId";
        public const string ClientId = $"{Section}:ClientId";
        // No Audience key: ValidAudiences is derived from ClientId ([clientId, api://clientId]) in the host.
    }

    public static class Auth
    {
        /// <summary>Explicit opt-in for FakeAuth + guest sign-in; honoured only outside Production.</summary>
        public const string AllowFakeAuth = $"{Root}:Auth:AllowFakeAuth";

    }

    /// <summary>Who may run <c>POST /api/seed/profiles</c> with a real login: a list of emails (Admin role always qualifies).</summary>
    public static class Seed
    {
        public const string AdminEmails = $"{Root}:Seed:AdminEmails";
    }

    /// <summary>AI provider credentials and the model ids per operation (config-driven: the published ids move on their own).</summary>
    public static class Ai
    {
        public const string Section = $"{Root}:Ai";

        public const string GeminiApiKey = $"{Root}:GeminiApiKey";
        public const string GeminiApiKeyEnvVar = "GEMINI_API_KEY";

        /// <summary>Fish Audio, the cloned-voice provider. Its presence is the whole switch: no key, no Fish.</summary>
        public const string FishAudioApiKey = $"{Root}:FishAudioApiKey";

        public const string FishAudioApiKeyEnvVar = "FISH_API_KEY";

        /// <summary>An optional shared voice for personas that carry no reference id of their own.</summary>
        public const string FishDefaultReferenceId = $"{Root}:Fish:DefaultReferenceId";

        /// <summary>
        /// The Azure OpenAI resource (e.g. <c>https://po-aiservices-shared2.cognitiveservices.azure.com/</c>). Set, it is
        /// the AI provider and the model ids below are its deployment names; it is reached with the app's identity, so
        /// there is no key. Empty means Gemini.
        /// </summary>
        public const string AzureOpenAiEndpoint = $"{Section}:AzureOpenAiEndpoint";

        public const string RoundModel = $"{Section}:RoundModel";
        public const string JudgeModel = $"{Section}:JudgeModel";
        public const string ProfileModel = $"{Section}:ProfileModel";
        public const string TtsModel = $"{Section}:TtsModel";
        public const string LiveModel = $"{Section}:LiveModel";
        public const string TranscribeModel = $"{Section}:TranscribeModel";

        /// <summary>
        /// The live human turn, which goes through <c>generateContent</c> rather than the diarizing
        /// <c>interactions</c> API. It is a separate key because the two surfaces do not accept the same
        /// model: see <c>GeminiTranscriptionService</c> for what the transcribe tier does to a turn.
        /// </summary>
        public const string TurnTranscribeModel = $"{Section}:TurnTranscribeModel";
        public const string Voice = $"{Section}:Voice";
        public const string JudgeThinkingLevel = $"{Section}:JudgeThinkingLevel";
        public const string JudgeServiceTier = $"{Section}:JudgeServiceTier";

        /// <summary>The tier a persona rewrite asks for; nobody is waiting on one, so it defaults to the discount.</summary>
        public const string ProfileServiceTier = $"{Section}:ProfileServiceTier";

        /// <summary>
        /// Prompt plus output tokens one caller may spend per UTC day before AI routes answer 429. Zero or absent
        /// switches the allowance off.
        /// </summary>
        public const string DailyTokenBudget = $"{Section}:DailyTokenBudget";

        /// <summary>Wire format the voice asks for: <c>mp3</c> (default) or <c>pcm</c>.</summary>
        public const string TtsWireFormat = $"{Section}:TtsWireFormat";
    }

    /// <summary>
    /// Storage endpoints and container names. Endpoints only — the solution authenticates exclusively with
    /// <c>DefaultAzureCredential</c>, so there is deliberately no connection-string key (and BannedSymbols.txt
    /// makes the connection-string constructors a build error).
    /// </summary>
    public static class Storage
    {
        public const string TableEndpoint = $"{Root}:TableStorageEndpoint";
        public const string BlobEndpoint = $"{Root}:BlobStorageEndpoint";
        public const string AudioContainer = $"{Root}:Storage:AudioContainer";
        public const string FacesContainer = $"{Root}:Storage:FacesContainer";
        public const string TtsCacheContainer = $"{Root}:Storage:TtsCacheContainer";
    }

    /// <summary>
    /// Local Azurite endpoints, applied over the Key Vault values in the host when <see cref="Toggles.UseAzurite"/> is on
    /// (Development only). Still endpoints: Azurite runs <c>--oauth basic</c> over HTTPS.
    /// </summary>
    public static class Azurite
    {
        public const string Section = $"{Root}:Azurite";
        public const string TableEndpoint = $"{Section}:TableStorageEndpoint";
        public const string BlobEndpoint = $"{Section}:BlobStorageEndpoint";
    }

    public static class Telemetry
    {
        /// <summary>Env var App Service injects automatically; the only accepted source.</summary>
        public const string AppInsightsEnvVar = "APPLICATIONINSIGHTS_CONNECTION_STRING";
        public const string SamplingRatio = $"{Root}:Telemetry:SamplingRatio";
        public const string SeqServerUrl = "Serilog:Seq:ServerUrl";
    }

    public static class Debate
    {
        public const string Section = $"{Root}:Debate";
    }

    public static class Analysis
    {
        public const string Section = $"{Root}:Analysis";
    }
}

/// <summary>
/// The three switches that are genuinely settable: everything else the app used to call a "feature flag" was really a
/// question about the environment or the configuration, and is answered where it is asked instead.
/// </summary>
public static class Toggles
{
    public const string Section = $"{ConfigKeys.Root}:Toggles";

    /// <summary>Force the deterministic fakes even with a key present (Development/Test only; Production ignores it).</summary>
    public const string UseFakes = $"{Section}:UseFakes";

    /// <summary>Development-only: point storage at the local Azurite container.</summary>
    public const string UseAzurite = $"{Section}:UseAzurite";

    /// <summary>Content-addressed blob cache of synthesized speech. Off is what a prompt-tuning session wants.</summary>
    public const string TtsCache = $"{Section}:TtsCache";

    /// <summary>Reported by <c>/api/diag</c> and <c>/api/health/details</c>, so a running app can say what it is set to.</summary>
    public static readonly IReadOnlyList<string> All = [UseFakes, UseAzurite, TtsCache];
}
