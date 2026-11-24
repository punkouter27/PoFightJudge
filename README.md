# PoFightJudge

One app, two ways to have a married-couple fight and get judged.

**WATCH** — pick a husband and a wife from your cast of personas, give them something to argue about, and they argue
it out three rounds each in their own synthesised voices while a judge scores it. You can take one of the two seats
yourself.

**Profiles are never written by hand.** The cast is the pre-made personas (added at startup) plus everybody who has
played, whose persona is written from their own debates. Each profile's record (wins, losses, rating) is counted from
its games on read: `GET /api/profiles` is read-only and returns it with every profile. "Load default cast" resets the
page to the built-in personas: every other profile is removed (records stay; a player's profile returns after their
next match). The pre-made cast never
changes; a person's profile is first written after their first match and refined after every one after that.

**Sides.** Give a topic and each person picks For or Against it on the setup screen (the other is always the
opposite). The side shows on the cards and the stage, the AI argues its side, the 2P host announces them, and the
judges weigh them. The fight and round requests carry it as an optional stance.

**One leaderboard.** `GET /api/leaderboard` ranks the people you have argued with and the cast together, on one Elo
scale, with the cast marked. There is no nav bar: Home is every destination (CPU, 1P, 2P, Profiles, Leaderboard),
the logo leads back to it, and the end of a match links to History and the Leaderboard.

**FIGHT** — you two, one microphone. A Gemini Live host introduces you, interrupts, cross-examines and rules, and
afterwards the recording is transcribed, diarised, measured and judged: claims, fallacies, traits, thirty
measurements each, and the moments worth listening to again.

**Fact check.** After the judge rules on a fight, every claim it found goes to one Gemini call with Google Search
grounding. Each claim comes back True, False or Unverifiable, with up to three source links, and the verdict's "What
they claimed" tab shows them. Each search is billed (5,000 a month free across Gemini 3), so this runs once per
fight and never in WATCH. If the check fails, the judge's own verdicts stand. During the fight, a **Temperature** row
shows how much of each person's talking was loud: Calm, Warm or 🔥 Heated.

A merge of [PoMarriedLife](https://github.com/punkouter26/PoMarriedLife) and
[PoArgueJudge](https://github.com/punkouter26/PoArgueJudge) into one fresh solution.

Governance: [AGENTS.md](AGENTS.md) is how it is worked on.

**On-device transcription.** The Settings page's **On-device transcription** switch (off by default) transcribes a
spoken 1P turn in the browser with Whisper (`Xenova/whisper-tiny.en`, q8, a 41 MB download cached after the first
time), on WebGPU when the browser has a GPU adapter and WASM when not. transformers.js 4.3.1 is vendored under
`wwwroot/lib/transformers` because the CSP runs same-origin scripts only. The model (Hugging Face) and the ONNX
runtime's `.wasm` (jsdelivr, pinned to one build) are data, and `connect-src` names exactly those hosts. Until the
model is loaded, or if it fails, the turn goes to the server as before.

**AI provider: Azure OpenAI.** `PoFightJudge:Ai:AzureOpenAiEndpoint` points at `po-aiservices-shared2` (AI
Foundry, resource group `PoShared`), and the model ids are its deployment names: `gpt-4.1-mini` writes the WATCH
lines, `gpt-5-mini` judges, rewrites personas and fact-checks with web search, `gpt-4o-mini-tts` speaks,
`gpt-4o-transcribe-diarize` transcribes, and `gpt-realtime-mini` hosts a FIGHT. There is no key: the app's
identity (`az login` locally, the web app's managed identity in Azure) holds **Cognitive Services OpenAI User** on
the resource. The Gemini personas' voices are mapped to the nearest OpenAI voice. Two things Gemini did that Azure
does not: the judge reads the transcript and the measurements but not the audio, and a dropped live line
reconnects as a new conversation. More in [docs/azure-openai.md](docs/azure-openai.md). Empty the endpoint and set
Gemini model ids to go back to Gemini.

## Run it

On a clean clone, one script does the lot — prerequisites, the dev certificate, Azurite, Seq, `az login`, build and
the whole suite:

```powershell
./scripts/setup.ps1          # add -Run to start the app when it finishes
```

Or by hand:

```powershell
./scripts/azurite.ps1                        # Azurite (OAuth + HTTPS on 12000/12002) + Seq (5341)
az login                                     # DefaultAzureCredential reads kv-poshared2 and Storage with this
dotnet run --project src/PoFightJudge.Api  # https://localhost:5001
```

**Signing in locally:** the login page offers **Sign in with Microsoft** (the same Entra registration as the
deployed site; `https://localhost:5001/authentication/login-callback` is registered on it) and **Continue as Guest**.

**With no Gemini key it still works.** Every AI seam has a deterministic stand-in — the rounds, the voices, the live
host, the transcription and the judge — so a whole watch and a whole fight run end to end offline, and the app says
so with a banner. The fakes are refused outright in Production.

## Test

```powershell
dotnet test tests/PoFightJudge.Unit          # no Docker, no network
dotnet test tests/PoFightJudge.Integration   # Testcontainers.Azurite (Docker)
dotnet test tests/PoFightJudge.E2EAPI        # the real API in process, with the fakes
dotnet test tests/PoFightJudge.E2EUI         # Playwright Chromium with a fake microphone playing a real argument
```

`dotnet test PoFightJudge.slnx` runs all four.

UI changes get a before/after page: `pwsh ./scripts/screenshots.ps1 -Feature <name>` shoots the old (`-Before`, default
`HEAD~1`) and new build with the E2EUI `ScreenshotRunner` (local Test host, never the live site) and writes
`SCREENSHOTS/<yyMMdd>-<name>.html` plus one annotated PNG. `SCREENSHOTS/` is git-ignored on purpose (review material,
not source): the page is handed over by its path.

## How it is put together

```
src/PoFightJudge.Shared   contracts, route table, config keys, value objects — the API and the client share one truth
src/PoFightJudge.Api      minimal API, vertical slices under Features/, SignalR hub for the live fight
src/PoFightJudge.Client   Blazor WebAssembly, Radzen, one set of design tokens
infra/                      Bicep: resource group, storage, F1 Linux plan, web app, role assignments
```

The slices: `Profiles` (the cast, read-only) · `Watch` (the round loop and its judge) · `Fight` + `Live` (the show, the state
machine and the Gemini socket) · `Analysis` (transcribe, measure, judge, highlight) · `Fighters` (people, and how
they argue) · `Records` (matches, results, history, leaderboards) · `Auth` · `Diagnostics` · `Storage` · `Voice`.

**`/diag`** (alias `/status`) is the plain-English system status page, open without signing in: an overall banner,
the API's response time, storage and each outside service (status, what it is for, probe latency), then settings
as set/not set, the build and the environment. It reads `GET /api/health/details`, which carries no value, URI,
path or exception text; `GET /api/diag` is the signed-in (admin in Production) operator view with models and usage.

**Forms** run the Shared FluentValidation rules on both ends: the page shows each message under its field and
disables the button while it submits, and the endpoint answers a 400 validation problem keyed by the same field
names (`CreateFightRequestValidator` on `POST /api/fights`, `RenameFighterRequestValidator` on the rename,
`WatchSetupValidator` for the CPU/1P setup screen). The fighter rename uses `RadzenTemplateForm` with the
`FluentValidationValidator` EditContext bridge; the multi-step setup screens call the validator directly.

Two rules run through all of it:

- **Storage is identity-only.** Endpoints and `DefaultAzureCredential`, never a connection string — the
  constructors that take one are a build error, and the storage account has shared-key access switched off.
- **Records are computed on read.** Nothing is accumulated on a counter, so re-reading a fight corrects its record
  instead of counting it twice, and deleting one removes it everywhere. The Elo rating on the boards and the roster
  is replayed the same way (`EloRatings`), so a deleted fight takes its points with it.
- **Recorded fights.** `POST /api/fights/recorded` (multipart: `audio` = 16 kHz mono 16-bit WAV, ≤ 64 MB; `meta` =
  `RecordedFightRequest` JSON with the tags, names, topic, stance and the phone's turn timeline) stores a fight
  argued with no host — offline, or by choice — exactly like a live fight's players' track (Opus when `StoreOpus`),
  saves the turns so `SpeakerMapper` puts each voice on its fighter, and queues the same analysis. The phone picks
  the `MatchId`, so a retried upload answers 200 with the fight it already made. Covered in E2EAPI
  (`AnalysisTests`, folded into the "a fight somebody actually argued" fact to stay at the tier's cap).

## Phone app (`src/PoFightJudge.Mobile`)

.NET MAUI for Android/iOS; not in `PoFightJudge.slnx`, not built by CI; uses only packages already pinned
(MAUI, the SignalR client). Play (live fight, record offline, watch), History, Settings, plus verdict, shared
ruling, leaderboard and watch result pages.

- **Live fight in the background.** The microphone (echo-cancelled: Android `VoiceCommunication` +
  `AcousticEchoCanceler`, iOS voice processing) streams 100 ms frames of 16 kHz PCM to the live hub (JSON protocol)
  and the host's 24 kHz voice plays back, stopping on `AudioClear`. A microphone + media-playback foreground service
  with a partial wake lock (Android) or background audio (iOS `UIBackgroundModes` audio) keeps it going with the
  screen locked.
- **Record now, upload later.** Offline, the phone records a WAV and the players tap whose turn it is; it is
  kept on the phone and uploaded to `/api/fights/recorded` as soon as there is a connection.
- **On-device speech.** A 1P watch turn is read by the phone's own recognizer (Android on-device
  `SpeechRecognizer`, iOS `SFSpeechRecognizer` with `RequiresOnDeviceRecognition`) instead of the browser's
  Whisper download; a phone with none records the turn and uses `/api/watch/transcribe`.
- **Haptics** on turn changes, the host cutting in, heated lines and slaps, and a three-beat buzz on the ruling.
- **Opponent from contacts.** Either fighter can be picked from contacts (name, a three-letter tag); the ruling's
  share link is sent to them by SMS (or the share sheet). `https://<host>/v/{token}` and `pofightjudge://v/{token}`
  open the shared ruling in the app when it is installed, and the web page otherwise.

Sign-in: Microsoft (Entra PKCE → bearer, also sent to the hub as `access_token`; needs the redirect
`pofightjudge://auth`) or, in Debug, the guest cookie. Dev loop: run the API, then
`pwsh scripts/mobile-dev.ps1 -ApiPort 5000 -Run`.

## Secrets

Configuration keys are `PoFightJudge:*`; the same values live in Key Vault as `PoFightJudge--*`. Nothing is ever
committed.

`PoFightJudge:Ai:DailyTokenBudget` (2,000,000 in `appsettings.json`, `0` to switch it off) is how many Gemini tokens
one caller may spend per UTC day. Past it, every rate-limited AI route answers 429 until midnight UTC, and `/diag`
shows the caller where they stand. The count is in memory, so a restart forgives the day.

`PoFightJudge:Ai:JudgeServiceTier` and `PoFightJudge:Ai:ProfileServiceTier` (both `flex`) mark the fight judge
and the persona rewrite as calls nobody waits on. On Gemini that is its discount tier (busy means a retry at the
standard price); on Azure, which has no such tier, it means the patient ten-minute client.

The three secrets (`GeminiApiKey`, `AzureAd--TenantId`, `AzureAd--ClientId`) are set in `kv-poshared2` by hand;
`./scripts/setup.ps1` checks the names are there and never reads a value.

## Deploy

`infra/main.bicep` creates everything and is applied by hand (`az deployment group what-if`, then `create`).
`.github/workflows/deploy.yml` is the single workflow for this repo: on a push to `master` it checks formatting, builds,
runs the Unit tier, publishes and deploys, signing in with a federated credential rather than a stored secret, then waits
until `/health/live` reports the pushed commit. Integration and E2E tiers run locally. `scripts/ci-checks.ps1` (format check and the E2EAPI tier) and `scripts/post-deploy-smoke.ps1`
(shell, runtime, manifest and the API's 401/404 answers on the live site) are run by hand. The plan is free-tier: one F1 Linux
plan, which means one instance — and a live fight lives in the API process with an in-process hub, so a second
instance would not see it anyway.

Nothing deploys itself. Creating the repository, the OIDC registration and the first release are deliberate steps.
