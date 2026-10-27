using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Analysis;

namespace PoFightJudge.Unit.Analysis;

/// <summary>
/// The whole-recording transcription asks for a verbatim, speaker-diarized transcript with word offsets. Each of the
/// three matters: a cleaned-up transcript hides the fillers and false starts the judge scores, and without speakers
/// and offsets there is nothing to map onto Player1 and Player2.
/// </summary>
public class GeminiTranscribeClientTests
{
    [Fact]
    public void Request_asks_for_verbatim_speaker_diarized_word_timestamps_and_the_response_keeps_each_words_speaker()
    {
        var request = JsonNode.Parse(GeminiTranscribeClient.BuildRequest("gemini-3.5-transcribe", "files/abc", "audio/webm"))!;
        var mode = request["generation_config"]!["transcription_config"]!["mode"]!;

        request["model"]!.GetValue<string>().Should().Be("gemini-3.5-transcribe");
        mode["type"]!.GetValue<string>().Should().Be("verbatim");
        mode["diarization_mode"]!.GetValue<string>().Should().Be("speaker");
        mode["timestamp_granularities"]!.AsArray().Select(g => g!.GetValue<string>()).Should().Equal("word");

        var response = JsonNode.Parse("""
            {"status":"completed","outputs":[{"text":"no you did","annotations":[
              {"type":"word_info","text":"no","speaker":"spk_1","start_offset":"0s","end_offset":"0.3s"},
              {"type":"word_info","text":"you","speaker":"spk_2","start_offset":"0.5s","end_offset":"0.7s"},
              {"type":"word_info","text":"did","speaker":"spk_2","start_offset":"0.7s","end_offset":"1s"}]}]}
            """);

        var transcript = GeminiTranscribeClient.ParseResponse(response);

        transcript.Words.Select(w => w.Label).Should().Equal("spk_1", "spk_2", "spk_2");

        // Azure answers in segments: their words are spread across the segment, labelled per piece and shifted by
        // where the piece starts in the recording.
        var (text, words) = AzureTranscribeClient.ParseResponse(
            JsonNode.Parse("""
                {"text":"no you did","segments":[
                  {"speaker":"A","start":0.0,"end":0.4,"text":" no"},
                  {"speaker":"B","start":0.5,"end":1.1,"text":" you did"}]}
                """) as JsonObject,
            offset: 600,
            labelPrefix: "p1_");
        text.Should().Be("no you did");
        words.Select(w => (w.Text, w.Label)).Should().Equal(("no", "p1_A"), ("you", "p1_B"), ("did", "p1_B"));
        words[2].Start.Should().BeApproximately(600.8, 0.001);
        words[2].End.Should().BeApproximately(601.1, 0.001);
    }
}
