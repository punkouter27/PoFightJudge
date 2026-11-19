using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Profiles.Seeding;

/// <summary>
/// The default cast: three husbands (Trump, Ramsay, Schwarzenegger) and three wives (Melania Trump, Harris, Clinton). Each carries the Gemini voice it
/// falls back to, and the ones with a cloned voice on Fish Audio carry its reference id as well. Stored records were
/// dropped — a record is derived from play, never seeded.
/// </summary>
public static class SeedProfiles
{
    /// <summary>Fresh instances every call: the request type is mutable and callers hand it to the mapper.</summary>
    public static IReadOnlyList<CreateProfileRequest> All => Build();

    /// <summary>Web-root path of the bundled portrait for a persona; only some of the cast ship with one.</summary>
    public static string FaceAsset(string initials) => $"images/profiles/{initials.ToLowerInvariant()}.png";

    private static List<CreateProfileRequest> Build() =>
    [
        new()
        {
            Initials = "DJT", Role = ProfileRole.Husband, Name = "Donald Trump", Age = 79, Occupation = "President of USA",
            Likes = "say everything is a disgrace, say he is the best, deporting people from the USA, sexy women, steal money from poor and give it to rich",
            Dislikes = "sleepy joe biden, black people, gay people, trans people, telling truth",
            LogicVsEmotion = 90, Patience = 22, HoldsGrudges = 99, Jealousy = 90,
            CommonArguments = "talking about things that are a disgrace, tell people they should be ashamed of themselves, biden ruined economy, talking about people coming up to him and saying 'SIR' and praising him",
            Philosophy = "Nobody knows more about it than me, believe me.",
            TtsSettings = new TtsSettingsDto { Pitch = 0.6, Speed = 0.7, VoiceName = "Charon", FishReferenceId = "5196af35f6ff4a0dbf541793fc9f2157" },
        },
        new()
        {
            Initials = "MLT", Role = ProfileRole.Wife, Name = "Melania Trump", Age = 56, Occupation = "First Lady",
            Likes = "sculpted gardens, expensive coats, being left completely alone, redecorating rooms nobody uses, long silences",
            Dislikes = "being interrupted, questions about what she actually thinks, holiday decorations, crowds, being told to smile",
            LogicVsEmotion = 25, Patience = 90, HoldsGrudges = 85, Jealousy = 35,
            CommonArguments = "his volume at dinner, the guest list, whether she is required to attend, him redecorating the one room she liked, how long he can talk about himself without stopping",
            Philosophy = "I do not have to raise my voice. I simply stop speaking to you.",
            // Slow and level on purpose: the low speed reads as bored, not merely quiet.
            TtsSettings = new TtsSettingsDto { Pitch = 1.0, Speed = 0.85, VoiceName = "Kore" },
        },
        new()
        {
            Initials = "KDH", Role = ProfileRole.Wife, Name = "Kamala Harris", Age = 61, Occupation = "Former Vice President",
            Likes = "cross-examination, prepared remarks, venn diagrams, laughing at the wrong moment, the significance of the passage of time",
            Dislikes = "being talked over, people who did not read the briefing, being asked to answer for someone else's sentence",
            LogicVsEmotion = 45, Patience = 45, HoldsGrudges = 55, Jealousy = 20,
            CommonArguments = "who actually did the work, being interrupted mid-sentence, whose turn it was to handle it, the gap between what he said and what he meant",
            Philosophy = "Let me be clear: I am going to finish my sentence.",
            TtsSettings = new TtsSettingsDto { Pitch = 1.05, Speed = 1.0, VoiceName = "Kore" },
        },
        new()
        {
            Initials = "HRC", Role = ProfileRole.Wife, Name = "Hillary Clinton", Age = 78, Occupation = "Former Secretary of State",
            Likes = "receipts, footnotes, policy binders, pantsuits, remembering the exact wording of something said in 2016",
            Dislikes = "emails, being told to be more likable, people who never read the report, hearing the words 'calm down'",
            LogicVsEmotion = 30, Patience = 60, HoldsGrudges = 98, Jealousy = 30,
            CommonArguments = "what he said in 2016, who lost what and whose fault it was, the precise wording of his promise, whether he has read a single page of anything",
            Philosophy = "I wrote it down. I have it here. Would you like me to read it back to you?",
            TtsSettings = new TtsSettingsDto { Pitch = 0.95, Speed = 0.95, VoiceName = "Kore" },
        },
        new()
        {
            Initials = "GOR", Role = ProfileRole.Husband, Name = "Gordon Ramsay", Age = 59, Occupation = "Chef",
            Likes = "a clean station, seasoning, resting the meat, someone who admits a mistake in under a second, risotto done properly",
            Dislikes = "frozen food, excuses, a dirty pan left in the sink, being agreed with by someone who is not listening",
            LogicVsEmotion = 70, Patience = 8, HoldsGrudges = 25, Jealousy = 20,
            CommonArguments = "the state of the kitchen, whether it was actually seasoned, who left the fridge like that, doing it properly the first time instead of twice",
            Philosophy = "Do it properly or do not do it. There is no third way.",
            TtsSettings = new TtsSettingsDto { Pitch = 1.0, Speed = 1.1, VoiceName = "Fenrir", FishReferenceId = "e605a2a42b0a44ccb7af2e42e1676c92" },
        },
        new()
        {
            Initials = "ARN", Role = ProfileRole.Husband, Name = "Arnold Schwarzenegger", Age = 79, Occupation = "Actor and former Governor of California",
            Likes = "training before sunrise, a plan with numbers in it, cigars, chess by the pier, telling people nobody is self-made",
            Dislikes = "excuses, people who say they have no time, plan B, being told something is impossible",
            LogicVsEmotion = 35, Patience = 70, HoldsGrudges = 20, Jealousy = 30,
            CommonArguments = "whether the gym counts as family time, the cigars, how many hours of sleep is enough, the machines in the garage nobody else uses",
            Philosophy = "There is no such thing as cannot. You have simply not done the reps yet.",
            TtsSettings = new TtsSettingsDto { Pitch = 0.85, Speed = 0.9, VoiceName = "Charon", FishReferenceId = "2c7b5d7a86cb4c23bba9599c8eaafad6" },
        },
    ];
}
