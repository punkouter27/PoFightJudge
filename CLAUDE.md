# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Read [AGENTS.md](AGENTS.md) first — it carries the rules, the environment matrix and the deviations table. This file
is the short version for a working session.

## Commands

```powershell
dotnet build PoFightJudge.slnx -c Release       # warnings are errors; this must be clean
dotnet test PoFightJudge.slnx                   # Unit, Integration (Docker), E2EAPI, E2EUI (Playwright)
dotnet test tests/PoFightJudge.Unit             # the fast tier
dotnet test tests/PoFightJudge.Unit --filter "FullyQualifiedName~GeminiVoiceStyleTests"   # one class or test
dotnet format PoFightJudge.slnx                 # run it before committing
./SCRIPTS/setup.ps1                               # clean machine → running app
./SCRIPTS/azurite.ps1                             # just the emulator (and -Down -Wipe to throw it away)
dotnet run --project src/PoFightJudge.Api       # https://localhost:5001
```

## Architecture in one breath

- `Shared` (contracts, `ApiRoutes`, `ConfigKeys`, Vogen ids, validators) is the one truth both the minimal-API host
  (`Api`) and the Blazor WASM client (`Client`, Radzen) compile against. A new config key goes in `ConfigKeys.cs`.
- `Api/Features/{Name}/` are vertical slices (Profiles, Watch, Fight, Live, Analysis, Fighters, Records, Voice, Ai,
  Auth, Storage, Diagnostics). Slices talk through `Shared` contracts and repositories, never each other's services.
- WATCH: AI personas argue in rounds; `IGeminiText` is transport, `IWatchAi` the prompting (the fake sits there).
- FIGHT: two humans, one mic, a Gemini Live host over SignalR. `ILiveSocket` → `IGeminiLiveClient` → `DebateSession`
  (pure state machine, no I/O). Afterwards `Analysis` transcribes, measures and judges the recording.
- Records/leaderboards are computed on read (`FighterStatsBuilder`, `StyleProfileBuilder`); nothing increments.
- Every AI seam has a deterministic fake, used when no key is present; registration refuses fakes in Production.
- Storage is `DefaultAzureCredential` + endpoints only (Azurite locally); connection-string ctors are banned.

## Working rules

- One task, one commit, subject `T##: what it does`.
- Tests first, then the smallest code that passes them. Never delete, skip or weaken a failing test — if it is
  wrong, say so and fix it deliberately; if it is right, the code is wrong.
- A task edits the files it lists. Going outside that is allowed when it is necessary, but it is said out loud in
  the commit message and in AGENTS.md's deviations table.
- Report faithfully. A failing test is reported as failing, with the output.
- Never invent an API's shape. Check the documentation or ask.

## Session rules

- **master only.** All work goes on `master`. Another branch happens only when it is asked for by name.
- **Restart and check.** After a code change, restart the app and confirm it came back up before saying it works.
- **Overview first.** The overall summary of the project lives in `README.md` and this file. `docs/` is not it —
  it holds only `screenshots/`, the test pictures, which git ignores. When a dependency, a configuration key or a
  major endpoint changes, update `README.md` in the same commit, and put anything longer than a paragraph in
  `docs/`.
- **No `dotnet user-secrets`.** Local settings go in `appsettings*.json`; anything secret goes in Azure Key Vault.
- **Never push.** `git push` happens only when the words **`git sync`** are typed — never off my own bat,
  and never because it seemed implied. On a `git sync`: commit with a short, non-technical subject in American slang, so
  it reads like a person wrote it, then push.
- **TL;DR.** Any answer over 100 words ends with a 20-word summary.
- **Run the tests the change touched**, not the whole suite — and none at all when the change is simple.
- **Warnings are errors.** A compile warning gets fixed, not left. `Directory.Build.props` turns them into
  errors; only `CS1591` and `NU1900` are let through.
- **Say when a lot goes.** If a prompt removes more than 100 lines of code overall, mention it.
- **UI changes get a before/after.** Take annotated screenshots of the old and the new UI with the changes
  marked, put them in `SCREENSHOTS/YYMMDD-feature-name.html`, and give its full path.
- **3-tier diff summary.** A code change over 20 lines ends with three one-line summaries: *Toddler* (the
  simple idea), *Child* (what it does), *Adult* (the technical/architectural change).
- **Direction questions.** After a code change over 100 lines, and only when the prompt asked no questions of
  its own, end with exactly three high-level questions about where the app's purpose is heading — the kind whose
  answer could start more changes.
- **Build version.** Every page shows the build as `vYYMMDD` in the bottom-right corner. The client build stamps
  it (`AssemblyMetadata("BuildDate")`, the UTC build day); `BuildStamp.Version` reads it. Never typed by hand.
- **Screenshots.** After any change that affects what a page shows, run the screenshot runner for the affected
  routes and send the PNGs in chat with any console errors before saying the change is done. Cloud sessions can't
  be browsed, so this is how changes are reviewed before a deploy. Route names live in `ScreenshotRunner.Routes`;
  leave `ROUTES` off for all of them. Google Fonts errors in a cloud session are the proxy, not the app.
  ```bash
  SHOTS=<scratchpad>/shots ROUTES=home,watch dotnet test tests/PoFightJudge.E2EUI \
    --filter "FullyQualifiedName~ScreenshotRunner" --logger "console;verbosity=detailed"
  ```
- **Do not hand over commands to type.** If it can be run here, run it — CLI or web GUI.

## Ask before

Any `az` write (Key Vault, app registration, deployment, role assignment) · creating the GitHub repository or its
OIDC credential · **every** `git push` · deleting everybody's matches or profiles · moving off the F1 plan or
changing a model tier · adding a NuGet package that is not already in `Directory.Packages.props`.

## Things that will bite

- `Arg.Any<MatchId>()` and friends throw: an uninitialised Vogen value object is not a valid argument matcher.
  Match on the real id.
- bunit disposes its container synchronously, so a test whose component owns an `IAsyncDisposable` needs
  `IAsyncLifetime`. And `FakeTimeProvider.Advance` must go through `cut.InvokeAsync`, or it deadlocks.
- A click that opens a Radzen confirm does not return until the dialog is answered. Capture the task, answer the
  dialog, then await it.
- `RadzenDataGrid` is bound to a list instance: mutate it and nothing re-renders. Assign a new list.
- `RadzenTabs` renders only the selected tab unless `RenderMode="TabRenderMode.Client"`.
- `RadzenChart` measures the DOM and throws under bunit. Test the decision, not the drawing.
- The analyzers are strict (`AnalysisLevel=latest-all`, Meziantou, VS.Threading, banned APIs). CA1711, MA0046,
  CA1849, VSTHRD200, MA0002 and MA0016 all bite regularly; read the rule before working around it.
