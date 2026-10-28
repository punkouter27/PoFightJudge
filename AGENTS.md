# AGENTS.md — how this repository is worked on

Written for whoever (or whatever) picks the work up next. [README.md](README.md) says what the app is and how to
run it. This file is the operating detail that does not live there.

## Identity and history

- One branch: `master`. Local git identity `punkouter26 <punkouter26@gmail.com>`.
- **One commit per task**, subject `T47: history, leaderboards and a persona's record`. The prefix is
  `T\d{2,}[a-z]?`, `docs`, `chore` or `fix` — the numbers have gone past two digits, so `T178` counts. Run
  `dotnet format` and the Unit tier before committing.
- The Husky hooks that used to police the subject are gone (removed in `6beaa79`), so the prefix is on the
  honour system: read it back with `git log --oneline -1` before a push.
- Nothing is pushed except on a `commit and sync` (see Session rules). The remote is `origin`,
  `https://github.com/punkouter27/PoFightJudge.git`.

## Environments

| | Development | Test | Production |
|---|---|---|---|
| Auth | FakeAuth (`X-Fake-User`) + guest cookie, and Entra ID sign-in beside them | FakeAuth + guest cookie | Entra ID, JWT bearer |
| Storage | Azurite over HTTPS with OAuth, ports 12000/12002 | in-memory repositories | Azure Storage, managed identity |
| Secrets | Key Vault via `az login` | none needed | Key Vault via managed identity |
| AI | real when a key is present, deterministic fakes when not | fakes, unless `POFIGHTJUDGE_E2E_REAL=1` | **real only** — the fakes refuse to register |
| Logs | console + Seq (5341) | console | Serilog → Application Insights |

In Development a request with a bearer token (or the live hub's `access_token`) goes to Entra and anything else to
the guest cookie or FakeAuth (`AuthServiceExtensions.SchemeFor`); the client registers MSAL there too, puts a token on
a call only when there is one (`OptionalTokenHandler`), and reads who is signed in from `/auth/me`. Entra is added
only when `AzureAd:ClientId` is configured, so the Test host stays offline.

`Test` is its own environment name, used by `ApiFactory` and by the E2EUI host. Anything that must never happen in
Production is written as `!environment.IsProduction()`, never as `IsDevelopment()`.

## Configuration

Keys are `PoFightJudge:*` in configuration and `PoFightJudge--*` in Key Vault; `SecretManager` maps one to the
other. Every key has a constant in `Shared/Configuration/ConfigKeys.cs`, and that is where a new one goes — the
host, the tests and the Bicep app settings all read the same name from there.

`appsettings.json` carries only what differs from a code default or has to be supplied: endpoints, the model
ids, the token budget, telemetry sampling and the toggles. Debate timings, analysis and the blob container names
default on `DebateOptions`, `AnalysisOptions` and `StorageContainers`, and their keys still override them.

Three switches are settable (`PoFightJudge:Toggles`, read once at startup): `UseFakes`, `UseAzurite` and
`TtsCache`. Everything else that used to be called a feature flag is a question about the environment or the
configuration and is answered where it is asked. `/api/features` tells the client what it needs to know —
`UseFakeAi`, `DevGuestEnabled`, `HumanInWatch`, `FishVoices` — and the "using fake AI" banner reads it.

## Rules that are not negotiable

- **No connection strings.** Storage is `DefaultAzureCredential` and endpoints, everywhere, and the SDK
  constructors that take a connection string are in `BannedSymbols.txt`.
- **No fakes in Production.** Registration is guarded, and a test asserts the guard.
- **`TimeProvider`, never `DateTime.UtcNow`.** Also banned at build time.
- **Radzen for anything a person operates.** `DesignRulesTests` greps the `.razor` files for raw `<button>`,
  `<input>`, `<select>` and `<textarea>`.
- **No `prefers-color-scheme` block.** The palette is `light-dark()` over one set of tokens, with
  `:root[data-theme]` pinning the explicit choice; the same test refuses the block and a fourth breakpoint, and
  `PaletteContrastTests` checks contrast in both themes.
- **Never weaken a test to make it pass.** A failing test is reported as failing.
- **The tiers are capped:** Unit 100, Integration 50, E2EAPI 25, E2EUI 25, counted in cases (a fact, or one
  theory row). `InfraTests.Every_test_tier_stays_under_its_cap` counts them from source. A new test makes room by
  folding cases that make the same decision into one fact with a case table, not by dropping an assertion.
- **Ask first** for: any `az` write (Key Vault, app registration, deployment, role assignment), creating the GitHub
  repository or its OIDC credential, every `git push` other than a `commit and sync`, deleting everybody's matches or profiles, moving off the F1
  plan or changing a model tier, and adding a NuGet package that is not already in `Directory.Packages.props`.

## Where things live

`Features/{Name}/` holds everything about one thing — endpoints, domain, storage, options — and slices talk to each
other through `Shared` contracts and repositories, not through each other's services. The seams worth knowing:

- `IGeminiText` is the transport; `IWatchAi` is the prompting. The WATCH fake sits at the second seam, so the fake
  understands the game rather than pretending to be an HTTP response.
- `ILiveSocket` is the WebSocket; `IGeminiLiveClient` is the protocol; `DebateSession` is the rules with no I/O at
  all. The scripted host replaces the client, so the state machine under test is the real one.
- Records are computed on read by `FighterStatsBuilder` and `StyleProfileBuilder` from
  result rows. Nothing anywhere increments a counter. A profile's record (`ProfileDto.Record`) is the same:
  `ProfileRecords` counts the cast's watches, or a person's fights, when `/api/profiles` is read.
- Profiles are read-only. They are the pre-made cast (`SeedOnStartup` adds any that are missing) or written from play
  by `FighterPersonaWriter`. "Load default cast" (`POST /api/seed/profiles`) is a reset: it removes every profile
  that is not a built-in persona, the ones from play included, and writes the built-ins back. Records are untouched,
  and a person's profile returns after their next match. There is no create, edit, delete, generate or face
  upload route, and no editor in the client. The pre-made cast never changes; a person's persona is written after
  their first match and rewritten from all their words after every one after that.
- A person's tag is exactly three letters (`Initials.IsTag`), checked where it is entered: the 2P start, a human seat in
  CPU/1P, and the client's tag boxes. Reading still accepts the 1–3 alphanumerics older tags were made with.
- Sides: with a topic, each person is For or Against it (`Stance`; only the first side's is carried, the second is
  always the opposite). It rides on `GenerateRoundRequest`/`VerdictRequest.HusbandStance` and
  `CreateFightRequest.Player1Stance`, is kept on `MatchDto.Side1Stance`, and reaches every prompt as one line from
  `Sides.Describe` ("... (the husband is FOR it; the wife is AGAINST it)"): the personas, the 2P host and both judges.
- One leaderboard: `GET /api/leaderboard` (`LeaderboardEndpoints.Together`) ranks this account's people and the cast
  on one Elo scale. A person whose persona was also argued appears once, as themselves; `IsPerson` marks which is which.
- The effects layer is five files in `wwwroot/js` and nothing in the app depends on any of it: `sfx.js` synthesizes
  every sound that is not a voice (HRTF-placed when it has a side, a made-up courtroom reverb when it wants one),
  `gfx.js` runs the WebGL2 shaders — single passes, plus two simulated kinds: `fluid` (stable fluids in half-float
  ping-pong targets) and `swarm` (particles stepped by transform feedback) — `particles.js` the canvas physics
  (sparks, confetti, shards, Verlet streamers), `terrain.js` a clip's spectrum drawn as mountains, and `fx.js` the
  one-shot CSS classes, the stamps, the holo foil and table mode. Each entry point answers "no"
  rather than throwing, the drawn half is off until the Settings panel's **Stage effects** switch turns it on
  (`po-effects`, read by `PoMotion.still()` in `theme.js`) and off whatever the switch when reduced motion is asked for, and the sound switch is stored where JS
  can read it before Blazor boots. The catalogues are spelled twice — `Sfx`/`Shaders` in C#, the voices and shaders
  in JS — and `EffectCatalogueTests` holds the pairs together, because a name the browser does not know is silence
  rather than an error; `StageEffectTests` compiles every shader and plays every voice in a real browser.
- The optional choices live in the Settings panel, not on the setup screens: `Preferences` (client, LocalStorage)
  holds stage effects (off), spin heat (Medium), the 2P host (the Referee), on-device transcription (off) and
  detailed analysis (off), each defaulting to the simplest app. With detailed analysis off the verdict is three tabs
  (the fight, the claims, the advice); on, it adds the traits and mood chart and the thirty measurements. A change
  raises `Preferences.Changed`, so an open verdict redraws without a reload.
- `ScreenshotRunner` with `SEED=1` plays a watch and a fight on the fakes and photographs the moments the effects
  live in (the tape, the glitch, the heat, table mode, the verdict's reveal, shatter, story image, rematch, the
  podium). A CSS effect too quick to catch is frozen part-way; a canvas is whatever frame it lands on.

## Deviations from the plan

Each was a deliberate decision made while building; the commit that made it says why.

| Task | Plan said | What was done | Why |
|---|---|---|---|
| T44, T47 | `FormSparklines` as a `RadzenChart` line | drawn by hand as an SVG polyline | eight numbers in a table cell; a chart that measures the DOM to lay itself out is the wrong tool, and it cannot render under bunit |
| T47 | five client files | plus `Components/Board.razor` and a describer in `Stats/StatFormat.cs` | the two leaderboards are one component rather than the same grid twice, and every measured field is described in one place |
| T48 | client files only | plus `GET /api/fighters/roster` (`FighterEndpoints`, `ApiRoutes`) | a page cannot show a record the API does not return, and one read beats one read per person |
| T49 | registration in `index.html` | `wwwroot/js/pwa.js` | the app's own CSP is `script-src 'self'`, so the inline script was blocked and nothing registered — caught by `PwaTests` |
| T50 | "workflow YAML parsed by a test" | `InfraTests` reads the workflow as text | a YAML parser is a new package, which is ask-first |
| T39 | one judge call assessing both players | one call per player | the live endpoint refuses a response schema carrying two full assessments, and `$ref` with it |
| T53 | one `Shared/Models` file per task | three (`LiveModels`, `RecordModels`, `ProfileModels`) | the seat travels from the 2P setup request, through the fighter row, to the persona the cast page shows; one contract per hop |
| T65 | a crowd bed under the stage | no ambient loop at all | every mode has an open microphone, and continuous noise out of the speakers is what the recording picks up |
| T67 | glass on the live chrome | glass, and the opaque floor kept underneath it | the contrast test reads tokens, not blurs; the floor is what the words are actually read against |
| T89 | the persona reads the fight it was handed | it reads a new fighter-words table: every debate's words, both engines | "the total collection of words said in all the debate games" is the ask; a style snapshot is a reading of a debate, not the debate |
| T90 | `WatchEndpoints` only | plus `WatchPersonaBacklog`, a hosted drain loop | a watch rules inside the request that ends it, and a model call per person there is time the room spends waiting on something it did not ask for |
| T92 | `MediaRecorder` for a watch turn, decoded back to samples | the `pcm-capture` worklet a live fight already uses, straight to WAV | `decodeAudioData` refuses MediaRecorder's WebM on some machines (`EncodingError`), and a container the page only ever unpacks itself is a step that can fail for nothing |
| T68 | a WebGPU pass on the verdict, WebGL2 as the fallback | WebGL2 only (`PoGfx.probe()` still reports WebGPU) | a second rendering backend for one cosmetic pass, which no tier here can exercise — headless Chromium has no `navigator.gpu` — is untested code for an effect nobody could tell apart |
| T97 | the voice slice only | plus `TtsSettings.FishReferenceId` out of the profile editor, the validator and the seed | a persona field whose only consumer was the provider being deleted is not a field any more |
| T98 | the flags | plus `appsettings.Development.json`'s duplicate `PoFightJudge` key | the Ollama block that T86 added was a second key in the same object, and removing it fixed a silent shadow |
| T100 | the pages and their endpoints | plus `.husky/commit-msg` | the hook took two-digit task numbers only, so T100 could not be committed under the rule the hook exists to enforce; the hooks were removed again in `6beaa79`, so the rule is kept by hand now |
| T101 | trim the suites | 62 Unit files deleted outright | at one test per decision most of those files had no decision left that another test did not already make |
| T110–T116 | ship the seven visual effects end to end, with full Unit tests on every commit, then run E2EUI | ship T110–T116 one task one commit each, Unit tier + `dotnet format --verify-no-changes` green on every commit; E2EUI (StageEffectTests additions) deferred to the next time `./SCRIPTS/setup.ps1` brings Azurite + the host up | the user asked for all in one session without bringing the dev env online, and Unit + format are the contracts that already run on every commit; E2EUI's job is to prove the shader/canvas paths, which the existing StageEffectTests pattern covers once the host is reachable |
| T120 | the crosstalk glitch only | plus `EffectCatalogueTests` | the pair test between `Sfx`/`Shaders` and the JS went in T101, and T119–T138 add nine voices and three shaders to the two lists |
| T121 | a Shepard-tone bed under the last half-minute | a 2.6 s riser at 30 s and 10 s left | T65: an open microphone records whatever the speakers play, so the climb is punctuation, never a bed |
| T122 | fluid behind the live fight and the watch rounds | the live fight only | the watch stage already carries the speaker's aura and the slap's ring on its own canvas; the fluid is the live stage's |
| T125 | table mode with tap zones to take a turn | table mode without tap zones | the live hub has no call for a fighter to claim the floor, so tap zones are server work, not a page |
| T127 | the gavel's ring refracting the page | a chromatic ring in the grain shader over the report | a WebGL canvas cannot read the DOM under it; the ring is drawn over it instead |
| T134 | rank tiers with an Elo history chart | tiers, badges and a promotion, no chart | ratings are replayed whole on read, so there is no per-fight rating to draw without a change on the server |
| T137 | WebGPU flocks with a WebGL2 fallback | WebGL2 transform feedback only | T68's reason: headless Chromium has no `navigator.gpu`, so a second backend would be untested code |
| T138 | a demo fight on the stand-in AI | a demo watch on whatever AI the environment has | in Production the fakes refuse to register; the demo is a real watch there and counts against the daily allowance |
| T140 | five client files (nav and settings) | plus `ScreenshotRunner` shots of the open Play menu and Settings panel | the runner is how a UI change is reviewed; a closed menu photographs as nothing |
| T142 | Home only | plus the client's `GetMatchesAsync` removed from `ApiClient.cs` | Home now reads one page through `GetMatchPageAsync`; nothing else called it |
| T143 | sortable column headers on History | an Order dropdown behind Filters | the grid became cards, which have no headers to click |
| T146 | the 2P host as a radio list (keyboard + screen reader as one choice) | a `RadzenDropDown` of the same items | the same single, keyboard-operable choice in one line instead of 210 px, which is what lets a desktop show the whole 2P setup at once |
| T204 | Gemini as the one AI provider (SPEC §2) | Azure OpenAI on `po-aiservices-shared2` behind the same seams, chosen by `Ai:AzureOpenAiEndpoint`; Gemini stays for an empty endpoint. Also touched outside the AI slices: `StorageServiceExtensions` (shares its `TokenCredential`), `DiagEndpoints.IsFakeAi`, `GeminiClaimChecker`'s prompt wording | the Gemini account's prepaid credit ran out; the Azure resource was already paid for, and reaching it with the app identity keeps a key out of Key Vault |
| — | face uploads decoded and re-encoded on the server with ImageSharp | the browser crops to a 512 px PNG (`js/face.js`); the server checks the PNG header and size, and decodes nothing | every ImageSharp 3.x has open advisories (three high), the fixes are 4.1.2+ only, and 4.x will not build without a paid-for licence key |

## Still outstanding

- **The first release went out on 2026-09-08.** `infra/main.bicep` had been deployed by hand (resource group
  `PoFightJudge`, `app-pofightjudge2` on `asp-pofightjudge-f1`, `stpofightjudge2`, the storage roles and the vault
  policy), the six `PoFightJudge--*` secrets seeded into `kv-poshared2`, and the site's redirect URI added to the
  `PoArgueJudge` Entra app the client signs in with. The GitHub half followed the same day: the private repository,
  its three `AZURE_*` variables (client id `018bf14a-14f7-4c38-8547-2ebfb12b72e7`), the `production` environment,
  and `deploy.yml`. Two things the plan had not foreseen: GitHub now stamps owner and repository ids into the OIDC
  subject (`repo:punkouter26@121304072/PoFightJudge@1360738000:…`), so the `PoFightJudge-GitHub-OIDC` registration
  carries a second pair of federated credentials in that form beside the original ones; and the client's
  `wwwroot/appsettings.json` had shipped with empty MSAL values, which curl cannot see but `DeployedSiteTests` did.
- **Transcription** of a finished fight goes to `gemini-3.5-transcribe` over the interactions API on every fight;
  the live captions are only used when `Analysis:UseLiveCaptionTranscript` is switched on. There is no Azure Speech
  path in the code. Whether the diarized call holds up on a real fifteen-minute fight has not been measured.
- The judge's **explicit context cache** (T81) has never run against the real service either. The shape is from the
  published reference, the fallback to inline parts is tested, and a refusal is logged rather than fatal — but
  whether a real fight's prefix clears the minimum token count is a measurement nobody has taken. The log line at
  event 5302/5303 says which way it went on any given fight.
- **WATCH prompts are never cached.** Measured 2026-10-20: rounds average about 1,145 prompt tokens and the judge
  about 790, and `round`/`judge` show a 0% `CacheHitRatio` on `/api/diag`. The published implicit-cache floor for
  Gemini 3.x Flash is 4,096 input tokens, so the cache-friendly round layout is kept but pays nothing until a
  prompt or a model crosses it.

The real-key measurements for WATCH, FIGHT and the analysis were taken on 2026-09-07. They were worth running:
nine defects came out of them, seven of which no test on the stand-ins could have seen. The write-ups were
point-in-time reports and were deleted in T102. The runs that produced their screenshots are still in
`WatchHardeningTests`, which writes them to `docs/screenshots/` (ignored by git).

## Costs

The four test tiers spend nothing: every AI seam has a deterministic stand-in, and the tests that talk to Google
skip unless a variable says otherwise — `GEMINI_API_KEY` for the live smoke, `POFIGHTJUDGE_E2E_REAL=1` for a
browser fight against the real service, `POFIGHTJUDGE_SMOKE_URL` for the deployed-site check. Those are worth
running when the wire format or a schema changes, and not otherwise.

`JudgeEvalTests` (Integration, `GEMINI_API_KEY`) runs the WATCH judge over sixteen hand-labelled golden fights,
half of them traps where the funnier, louder or more in-character side argued worse. It is what a judge model or a
rubric change has to hold; `EVAL_JUDGE_MODEL` points it at another tier. On 2026-10-20 both `gemini-3.7-flash`
and `gemini-3.1-flash-lite` called all sixteen both ways round — the sides swapped, no flips — which is why
the judge has no order-bias correction: there was no bias to correct.

## Session rules

- **master only.** All work goes on `master`. Another branch happens only when it is asked for by name.
- **Restart and check.** After a code change, restart the app and confirm it came back up before saying it works.
- **Overview first.** Read `README.md` (and `docs/`) before architectural work. The overall summary lives in
  `README.md` and this file. `docs/` is not it —
  it holds only `screenshots/`, the test pictures, which git ignores. When a dependency, a configuration key or a
  major endpoint changes, update `README.md` in the same commit, and put anything longer than a paragraph in
  `docs/`.
- **No `dotnet user-secrets`.** Local settings go in `appsettings*.json`; anything secret goes in Azure Key Vault.
- **Push only on "commit and sync".** When the words **`commit and sync`** (or **`git sync`**) are typed, commit
  everything with a short, non-technical subject in American slang, so it reads like a person wrote it, then push
  without asking. Any other push is never done on my own initiative, and never because it seemed implied.
- **TL;DR.** Any answer over 100 words ends with a summary of at most 20 words.
- **Run the tests the change touched**, and none at all when the change is simple. Never the whole suite.
- **Do not hand over commands to type.** If it can be run here, run it — CLI or web GUI.
- **Warnings are errors.** A compile warning gets fixed, not left. `Directory.Build.props` turns them into
  errors; only `CS1591` and `NU1900` are let through.
- **Say when a lot goes.** If a prompt removes more than 100 lines of code overall, mention it.
- **UI changes get a before/after.** Take annotated screenshots of the old and the new UI with the changes
  marked, put them in `SCREENSHOTS/YYMMDD-feature-name.html`, and give its absolute path.
- **3-tier diff summary.** A code change over 20 lines ends with three summaries, strictly one sentence each: *Toddler* (the
  simple idea), *Child* (what it does), *Adult* (the technical/architectural change).
- **Direction questions.** After a code change over 100 lines, and only when the prompt asked no questions of
  its own, end with exactly three high-level questions about where the app's purpose is heading — the kind whose
  answer could start more changes.
- **Build version.** Every page shows the build as `vYYMMDD` in the bottom-right corner. The client build stamps
  it (`AssemblyMetadata("BuildDate")`, the UTC build day); `BuildStamp.Version` reads it. Never typed by hand.

## CI/CD and Azure (same section in every Po repo)

- A push to `master` builds, tests, deploys and health-gates (`.github/workflows/deploy.yml`). Nothing is deployed by hand.
- Azure sign-in is GitHub OIDC (the `gh-<repo>-deploy` app). Its IDs are repo **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and the App Service name is `AZURE_WEBAPP_NAME`. Never move them to secrets or write resource names into workflows.
- Every permission the app and CI need lives in `SCRIPTS/ensure-azure-access.ps1`. A role granted by hand and not added there is a bug: the next re-provision loses it.
- The app answers `/health/live` (process up, no dependencies) and `/health` (dependencies reachable). The deploy's health gate checks both.
- Tests pass on any machine: no drive-letter, temp-folder or working-directory assumptions, and no live config value hard-coded as an expected result (read it from the same config the app reads).
- A red run on the default branch gets an ntfy push from the CI watcher in PoPunkouterSoftware. Fix it, or say why it is red.
