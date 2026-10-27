using System.Text.Json.Nodes;

namespace PoFightJudge.Api.Features.Live;

/// <summary>
/// The functions the host must call to drive the show; the orchestrator handles each one. The argument types are as
/// tight as the schema allows on purpose: a rejected tool call costs a whole model round trip, and the room hears
/// that as the host going quiet mid-show.
/// </summary>
public static class ToolDeclarations
{
    public const string SetPlayers = "set_players";
    public const string StartTurn = "start_turn";
    public const string EndDebate = "end_debate";
    public const string AskProbe = "ask_probe";
    public const string DeliverVerdict = "deliver_verdict";
    public const string FlagFallacy = "flag_fallacy";

    /// <summary>The only two values any player argument may take: there is nobody else in the room.</summary>
    private static readonly string[] Players = ["player1", "player2"];

    /// <summary>
    /// Built fresh on each call. A JSON node belongs to one document, and the setup builder attaches this to one, so
    /// a shared instance would tie two sessions' setup frames together.
    /// </summary>
    public static JsonArray FunctionDeclarations() =>
    [
        Declare(SetPlayers, "Record the topic and both names once they have been confirmed aloud.",
            ("topic", Str("What they are arguing about, in a few words.")),
            ("player1", Str("First person's name.")),
            ("player2", Str("Second person's name."))),
        Declare(StartTurn, "Call immediately whenever you hand the floor to someone, before they start speaking.",
            ("player", OneOf("Who now has the floor.", Players))),
        Declare(EndDebate, "Call when the open argument ends and you start the questions."),
        Declare(AskProbe, "Call right before you ask one of them a clarifying question. One at a time.",
            ("player", OneOf("Which of them you are questioning.", Players)),
            ("question", Str("The question you are about to ask."))),
        Declare(DeliverVerdict, "Call right before announcing the ruling.",
            ("winner_logic", OneOf("Who argued more logically.", Players)),
            ("winner_correct", OneOf("Who was more factually correct.", Players)),
            ("overall", OneOf("The overall winner.", Players)),
            ("reasons", StrArray("Exactly three short reasons for the ruling.", "One short reason", min: 3, max: 3))),
        Declare(FlagFallacy, "Call the moment one of them commits a clear logical fallacy, right before you name it aloud.",
            ("player", OneOf("Who committed it.", Players)),
            ("fallacy", Str("Its common name, e.g. Strawman, Ad hominem, Whataboutism.")),
            ("quote", Str("Their words it was in, a few at most."))),
    ];

    private static JsonObject Str(string description) => new() { ["type"] = "STRING", ["description"] = description };

    private static JsonObject OneOf(string description, string[] values) => new()
    {
        ["type"] = "STRING",
        ["description"] = description,
        ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]),
    };

    private static JsonObject StrArray(string description, string itemDescription, int min, int max) => new()
    {
        ["type"] = "ARRAY",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "STRING", ["description"] = itemDescription },
        ["minItems"] = min,
        ["maxItems"] = max,
    };

    private static JsonObject Declare(string name, string description, params (string Name, JsonObject Schema)[] parameters)
    {
        var declaration = new JsonObject { ["name"] = name, ["description"] = description };
        if (parameters.Length == 0)
        {
            return declaration;
        }

        var properties = new JsonObject();
        foreach (var (parameterName, schema) in parameters)
        {
            properties[parameterName] = schema;
        }

        // Everything declared is required: a half-filled call is one the orchestrator would have to guess at.
        declaration["parameters"] = new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. parameters.Select(p => (JsonNode)p.Name)]),
        };
        return declaration;
    }
}
