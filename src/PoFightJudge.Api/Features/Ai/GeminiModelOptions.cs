using PoFightJudge.Shared.Configuration;

namespace PoFightJudge.Api.Features.Ai;

/// <summary>
/// Which Gemini model serves each operation (SPEC §2, ids verified 2026-09-06). Tiered deliberately: dialogue lines
/// are short, frequent and forgiving, so they run on the cheapest tier; the judges read a whole transcript once and
/// get the larger model. Config-driven rather than <c>const</c> because these are the only real cost knob and the
/// published ids move independently of this codebase — repointing one should be a Key Vault edit, not a redeploy.
/// </summary>
public sealed record GeminiModelOptions(
    string Round,
    string Judge,
    string Profile,
    string Tts,
    string Live,
    string Transcribe,
    string TurnTranscribe,
    string Voice,
    string JudgeThinkingLevel,
    string JudgeServiceTier,
    string ProfileServiceTier)
{
    public static GeminiModelOptions Defaults { get; } = new(
        Round: "gemini-3.1-flash-lite",
        Judge: "gemini-3.7-flash",
        Profile: "gemini-3.7-flash",
        Tts: "gemini-3.1-flash-tts-preview",
        Live: "gemini-3.1-flash-live-preview",
        Transcribe: "gemini-3.5-transcribe",

        // Not the transcribe tier: on generateContent it answers 200 and returns an empty part every
        // time, which reaches a player as a turn that heard nothing. Flash-lite reads the same clip.
        TurnTranscribe: "gemini-3.1-flash-lite",
        Voice: "Puck",
        JudgeThinkingLevel: "low",
        JudgeServiceTier: "flex",
        ProfileServiceTier: "flex");

    public static GeminiModelOptions FromConfiguration(IConfiguration configuration) => new(
        Round: Pick(configuration, ConfigKeys.Ai.RoundModel, Defaults.Round),
        Judge: Pick(configuration, ConfigKeys.Ai.JudgeModel, Defaults.Judge),
        Profile: Pick(configuration, ConfigKeys.Ai.ProfileModel, Defaults.Profile),
        Tts: Pick(configuration, ConfigKeys.Ai.TtsModel, Defaults.Tts),
        Live: Pick(configuration, ConfigKeys.Ai.LiveModel, Defaults.Live),
        Transcribe: Pick(configuration, ConfigKeys.Ai.TranscribeModel, Defaults.Transcribe),
        TurnTranscribe: Pick(configuration, ConfigKeys.Ai.TurnTranscribeModel, Defaults.TurnTranscribe),
        Voice: Pick(configuration, ConfigKeys.Ai.Voice, Defaults.Voice),
        JudgeThinkingLevel: Pick(configuration, ConfigKeys.Ai.JudgeThinkingLevel, Defaults.JudgeThinkingLevel),
        JudgeServiceTier: Pick(configuration, ConfigKeys.Ai.JudgeServiceTier, Defaults.JudgeServiceTier),
        ProfileServiceTier: Pick(configuration, ConfigKeys.Ai.ProfileServiceTier, Defaults.ProfileServiceTier));

    private static string Pick(IConfiguration configuration, string key, string fallback) =>
        configuration[key]?.Trim() is { Length: > 0 } configured ? configured : fallback;
}
