# Azure OpenAI as the AI provider

Since T204 the app's AI runs on `po-aiservices-shared2`, the AI Foundry resource in resource group `PoShared`
(East US 2). Setting `PoFightJudge:Ai:AzureOpenAiEndpoint` selects Azure. When it is set, the `*Model` keys are
deployment names. Every Gemini seam has an Azure implementation behind the same interface, so the slices above
them did not change.

| Seam | Azure class | Deployment | Call |
|---|---|---|---|
| `IGeminiText` (rounds, WATCH judge, personas, previews) | `AzureOpenAiText` | `gpt-4.1-mini` (rounds), `gpt-5-mini` (judge, profile) | `POST openai/v1/chat/completions`, streamed for rounds |
| `ITtsProvider` (after Fish) | `AzureTtsService` | `gpt-4o-mini-tts` | `POST openai/v1/audio/speech`, `response_format: pcm` (24 kHz, the same as Gemini) |
| `ITranscriptionService` (a spoken 1P turn) | `AzureTranscriptionService` | `gpt-4o-transcribe-diarize` | `POST openai/deployments/{d}/audio/transcriptions` |
| `IGeminiTranscribeClient` (a whole fight) | `AzureTranscribeClient` | `gpt-4o-transcribe-diarize` | the same, `diarized_json`, in ten-minute pieces |
| `IGeminiJudgeClient` (the FIGHT judge) | `AzureJudgeClient` | `gpt-5-mini` | through `IGeminiText`, three calls as before |
| `IClaimChecker` (the fact check) | `AzureClaimChecker` | `gpt-5-mini` | `POST openai/v1/responses` with the `web_search` tool |
| `IGeminiLiveClient` (the FIGHT host) | `AzureRealtimeClient` | `gpt-realtime-mini` | `wss://…/openai/v1/realtime?model=…`, GA events |
| `IGeminiFilesClient` | `AzureRecordingFiles` | — | a temp file; Azure takes audio inline |

## Access

Access uses the identity only: `DefaultAzureCredential` and the scope `https://cognitiveservices.azure.com/.default`.
The **Cognitive Services OpenAI User** role on the resource is held by `punkouter27@gmail.com` (local `az login`)
and by the managed identity of `app-pofightjudge2`. Both were granted on 2026-10-27. Owner alone is not enough,
because Owner carries no data actions, so a call made with Owner alone answers 401. A newly granted role took
about ten minutes to take effect, and a new deployment took about as long before its endpoint answered.

## Measured on 2026-10-27

- **Transcription needs the deployment path.** On this resource, `openai/v1/audio/transcriptions` answers
  `DeploymentNotFound` for every transcription deployment. `openai/deployments/{d}/audio/transcriptions?api-version=2025-03-01-preview` works.
- **Whisper invents speech on silence.** Two seconds of silence came back from whisper as "you". The
  `gpt-4o-transcribe` family returns an empty string for the same clip, so spoken turns use
  `gpt-4o-transcribe-diarize`. That model needs `chunking_strategy=auto`.
- **Realtime hears 24 kHz.** The room is recorded at 16 kHz, so `AzureRealtimeClient` upsamples. The host's audio
  comes back at 24 kHz, which is what the player already plays. The session's input transcription accepts
  `gpt-4o-transcribe-diarize`.
- **The host is not interruptible.** With realtime's default barge-in, two people talking over the host cancelled
  its reply mid tool call. `set_players` then arrived as `{}`, failed, and the show never left its intro. The
  session sets `interrupt_response: false`, so the host finishes its short turn and the client never reports
  `Interrupted`.
- **One response at a time.** A text nudge or a tool result needs its own `response.create`. While a response is
  running, the client holds that request back until `response.done`.
- **A streamed chat ends with a usage-only chunk.** Its `choices` array is empty. `AzureOpenAiText.Delta` reads it
  without indexing into the array.
- **The reasoning deployments** (`gpt-5*`) refuse `temperature` and count their thinking inside
  `max_completion_tokens`. They get a `reasoning_effort` and 4,096 extra tokens. Whether a deployment is a
  reasoning one is decided from its name.

## What is different from Gemini

- The FIGHT judge reads the diarized transcript and the measurements but not the audio, because the chat models
  take no audio input.
- There is no live session resumption. A dropped line reconnects as a new conversation, and the orchestrator's
  "pick up where you were" nudge is all the new conversation hears of the old one.
- There is no flex tier. `JudgeServiceTier` and `ProfileServiceTier` only route those calls to the patient client.
- Voices: each Gemini voice is mapped to the nearest OpenAI voice by register (`AzureVoices`). A persona always
  gets the same voice.
