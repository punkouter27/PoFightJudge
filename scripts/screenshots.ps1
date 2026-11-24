<#
.SYNOPSIS
    One command for the before/after page a UI change gets (AGENTS.md session rules: "UI changes get a before/after").

.DESCRIPTION
    Uses the repo's own camera, tests/PoFightJudge.E2EUI/ScreenshotRunner.cs (in-process Test host, fake AI, FakeAuth,
    the trimmed Release client), once per side, so nothing ever touches the live site:
    1. `-Before` (a git ref, default HEAD~1) is checked out in a temporary detached worktree; `-After` is a git ref too,
       or the working tree when empty. `-BeforeDir` / `-AfterDir` skip that side's run and read `<route>.png` /
       `<route>-phone.png` (e.g. the shots-* folders a `-KeepWork` run leaves, to recompose without rebuilding).
    2. Each side: Release build, `dotnet publish -p:WasmApplicationEnvironmentName=Test` (as the E2EUI tier does), then
       ScreenshotRunner with VIEWPORTS=desktop,phone, DESKTOP_SIZE=1366x768 (phone 390x844) and EFFECTS=off (reduced
       motion, so the stage effects cannot show up as a change). A ref older than DESKTOP_SIZE gets today's
       ScreenshotRunner.cs copied in, so both sides are shot the same way; the page says when that failed.
    3. Compares before/after pixel by pixel in Chromium and boxes the changed areas in red on the after shot.
    4. Writes SCREENSHOTS/<yyMMdd>-<Feature>.html (self-contained; unchanged routes shown once, small) and
       SCREENSHOTS/<yyMMdd>-<Feature>.png (the 4 most-changed routes, before | after, with the notes).
       SCREENSHOTS/ is git-ignored on purpose (.gitignore: review material, not source): give the owner the path.

    One heavy process at a time (AGENTS.md section 2): don't run this beside E2EUI or another browser run.

.PARAMETER Routes
    ScreenshotRunner.Routes names (home, watch, one-player, fight, fighters, profiles, history, health, login).
    `home` also shoots `home-settings` (the gear's page).

.EXAMPLE
    pwsh ./scripts/screenshots.ps1 -Feature podium-colours -Routes fighters -Notes 'Podium steps use the gold/silver/bronze tokens.'
    pwsh ./scripts/screenshots.ps1 -Feature v4-audit -Before 999d725 -After c8b3a4c
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Feature,
    [string]$Before = 'HEAD~1',
    [string]$BeforeDir = '',
    [string]$After = '',
    [string]$AfterDir = '',
    [string[]]$Routes = @('home', 'watch', 'fighters', 'profiles', 'history', 'health', 'login'),
    [string[]]$Notes = @(),
    [string]$Date = (Get-Date).ToUniversalTime().ToString('yyMMdd'),
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot 'SCREENSHOTS'
$work = Join-Path ([IO.Path]::GetTempPath()) "pofightjudge-shots-$PID"
$Routes = $Routes | ForEach-Object { $_ -split ',' } | Where-Object { $_ }
$runner = 'tests/PoFightJudge.E2EUI/ScreenshotRunner.cs'
function Wait-Task($task) { $task.GetAwaiter().GetResult() }

New-Item -ItemType Directory -Force $work | Out-Null
$worktrees = @()
$warnings = [Collections.Generic.List[string]]::new()
$pw = $null; $browser = $null
try {
    function Get-Source([string]$ref, [string]$side) {
        if (-not $ref) { return $repoRoot }
        $dir = Join-Path $work "src-$side"
        git -C $repoRoot worktree add --detach $dir $ref 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git worktree add $ref failed" }
        $script:worktrees += $dir
        if (-not (Select-String -Path (Join-Path $dir $runner) -Pattern 'DESKTOP_SIZE' -Quiet)) {
            Copy-Item (Join-Path $repoRoot $runner) (Join-Path $dir $runner) -Force
            $script:copiedRunner = $dir
        }
        return $dir
    }

    function Invoke-Capture([string]$src, [string]$side) {
        Write-Host "[$side] building $src" -ForegroundColor Cyan
        $log = Join-Path $work "$side.log"
        dotnet build (Join-Path $src 'PoFightJudge.slnx') -c Release *> $log
        if ($LASTEXITCODE -ne 0 -and $script:copiedRunner -eq $src) {
            # Today's camera does not compile against that ref: shoot with the ref's own (desktop at its own size).
            git -C $src checkout -- $runner
            $warnings.Add("$side ($src): today's ScreenshotRunner did not compile there; its own runner shot desktop at its default size.")
            dotnet build (Join-Path $src 'PoFightJudge.slnx') -c Release *>> $log
        }
        if ($LASTEXITCODE -ne 0) { throw "[$side] build failed (see $log)" }
        $pub = Join-Path $work "pub-$side"
        dotnet publish (Join-Path $src 'src/PoFightJudge.Api') -c Release --no-build -p:WasmApplicationEnvironmentName=Test -o $pub *>> $log
        if ($LASTEXITCODE -ne 0) { throw "[$side] publish failed (see $log)" }
        $shots = Join-Path $work "shots-$side"
        $env:SHOTS = $shots
        $env:ROUTES = $Routes -join ','
        $env:VIEWPORTS = 'desktop,phone'
        $env:DESKTOP_SIZE = '1366x768'
        $env:EFFECTS = 'off'
        $env:POFIGHTJUDGE_E2E_PUBLISH_DIR = $pub
        try {
            dotnet test (Join-Path $src 'tests/PoFightJudge.E2EUI') -c Release --no-build --filter 'FullyQualifiedName~ScreenshotRunner' --logger 'console;verbosity=detailed' *>> $log
            if ($LASTEXITCODE -ne 0) { throw "[$side] ScreenshotRunner failed (see $log)" }
        }
        finally { 'SHOTS', 'ROUTES', 'VIEWPORTS', 'DESKTOP_SIZE', 'EFFECTS', 'POFIGHTJUDGE_E2E_PUBLISH_DIR' | ForEach-Object { Remove-Item "env:$_" -ErrorAction SilentlyContinue } }
        Select-String -Path $log -Pattern '^\s+\S+: \[(CONSOLE ERROR|PAGE ERROR|HTTP|REQUEST FAILED)' | ForEach-Object { $warnings.Add("$side $($_.Line.Trim())") }
        Write-Host "[$side] $((Get-ChildItem $shots -Filter *.png).Count) shots"
        return $shots
    }

    $script:copiedRunner = $null
    $beforeShots = if ($BeforeDir) { $BeforeDir } else { Invoke-Capture (Get-Source $Before 'before') 'before' }
    $script:copiedRunner = $null
    $afterShots = if ($AfterDir) { $AfterDir } else { Invoke-Capture (Get-Source $After 'after') 'after' }

    # ── Compare, annotate, compose (Playwright from the E2EUI build) ─────────
    Add-Type -Path (Join-Path $repoRoot 'tests/PoFightJudge.E2EUI/bin/Release/net10.0/Microsoft.Playwright.dll')
    $pw = Wait-Task ([Microsoft.Playwright.Playwright]::CreateAsync())
    $browser = Wait-Task ($pw.Chromium.LaunchAsync([Microsoft.Playwright.BrowserTypeLaunchOptions]@{ Headless = $true }))
    $tool = Wait-Task ($browser.NewPageAsync())
    $diffJs = @'
async ([a, b]) => {
  const load = src => new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = no; i.src = src; });
  const [ia, ib] = await Promise.all([load(a), load(b)]);
  const w = Math.max(ia.width, ib.width), h = Math.max(ia.height, ib.height);
  const pix = img => { const c = new OffscreenCanvas(w, h); const x = c.getContext('2d'); x.drawImage(img, 0, 0); return x.getImageData(0, 0, w, h).data; };
  const pa = pix(ia), pb = pix(ib);
  const CELL = 16, gw = Math.ceil(w / CELL), gh = Math.ceil(h / CELL), grid = new Uint8Array(gw * gh);
  let changed = 0;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = (y * w + x) * 4;
    if (Math.abs(pa[i] - pb[i]) + Math.abs(pa[i + 1] - pb[i + 1]) + Math.abs(pa[i + 2] - pb[i + 2]) + Math.abs(pa[i + 3] - pb[i + 3]) > 48) { changed++; grid[((y / CELL) | 0) * gw + ((x / CELL) | 0)] = 1; }
  }
  const seen = new Uint8Array(gw * gh), boxes = [];
  for (let s = 0; s < grid.length; s++) {
    if (!grid[s] || seen[s]) continue;
    let x0 = 1e9, y0 = 1e9, x1 = -1, y1 = -1; const st = [s]; seen[s] = 1;
    while (st.length) {
      const c = st.pop(), cx = c % gw, cy = (c / gw) | 0;
      x0 = Math.min(x0, cx); y0 = Math.min(y0, cy); x1 = Math.max(x1, cx); y1 = Math.max(y1, cy);
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
        const nx = cx + dx, ny = cy + dy, n = ny * gw + nx;
        if (nx >= 0 && ny >= 0 && nx < gw && ny < gh && grid[n] && !seen[n]) { seen[n] = 1; st.push(n); }
      }
    }
    boxes.push([x0 * CELL, y0 * CELL, (x1 + 1) * CELL, (y1 + 1) * CELL]);
  }
  const c = new OffscreenCanvas(ib.width, ib.height), x = c.getContext('2d');
  x.drawImage(ib, 0, 0); x.strokeStyle = '#e00'; x.lineWidth = 3;
  for (const [l, t, r, btm] of boxes) x.strokeRect(l - 3, t - 3, r - l + 6, btm - t + 6);
  const blob = await c.convertToBlob({ type: 'image/png' });
  const marked = await new Promise(ok => { const f = new FileReader(); f.onload = () => ok(f.result); f.readAsDataURL(blob); });
  return JSON.stringify({ pct: Math.round(changed * 10000 / (w * h)) / 100, boxes: boxes.length, marked, size: [ia.width, ia.height, ib.width, ib.height] });
}
'@
    $enc = [Net.WebUtility]
    $rows = foreach ($a in Get-ChildItem $afterShots -Filter *.png | Sort-Object Name) {
        $b = Join-Path $beforeShots $a.Name
        $bUri = if (Test-Path $b) { 'data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($b)) } else { $null }
        $aUri = 'data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($a.FullName))
        $d = if ($bUri) { Wait-Task ($tool.EvaluateAsync[string]($diffJs, [string[]]@($bUri, $aUri))) | ConvertFrom-Json } else { $null }
        [pscustomobject]@{ Name = $a.BaseName; Phone = $a.BaseName.EndsWith('-phone'); Before = $bUri; After = $aUri; Diff = $d
            Changed = (-not $d) -or $d.pct -gt 0 }
    }
    if (-not $rows) { throw "No shots in $afterShots" }

    $title = "$Date $Feature"
    $beforeLabel = if ($BeforeDir) { "PNGs from $(Split-Path $BeforeDir -Leaf)$(if ($PSBoundParameters.ContainsKey('Before')) { " (git $Before)" })" } else { "git $Before ($(git -C $repoRoot rev-parse --short $Before))" }
    $afterLabel = if ($AfterDir) { "PNGs from $(Split-Path $AfterDir -Leaf)$(if ($After) { " (git $After)" })" } elseif ($After) { "git $After ($(git -C $repoRoot rev-parse --short $After))" } else { "working tree on $(git -C $repoRoot rev-parse --short HEAD)" }
    $css = 'body{font:14px system-ui,sans-serif;margin:16px;background:#fff;color:#111}h1{font-size:20px}h2{font-size:16px;margin:20px 0 6px}' +
        '.pair{display:flex;gap:12px;align-items:flex-start;flex-wrap:wrap}figure{margin:0}figcaption{font-weight:600;margin-bottom:4px}' +
        'img{border:1px solid #bbb;display:block}.same{color:#555}.chg{color:#b00;font-weight:600}.warn{color:#8a5a00}'
    function Format-Page([object[]]$items, [double]$deskScale, [double]$phoneScale, [bool]$all) {
        $sb = [Text.StringBuilder]::new()
        [void]$sb.Append("<!doctype html><html lang=`"en`"><head><meta charset=`"utf-8`"><title>$($enc::HtmlEncode($title))</title><style>$css</style></head><body>")
        [void]$sb.Append("<h1>$($enc::HtmlEncode($title)): before / after</h1><p>Before: $($enc::HtmlEncode($beforeLabel)). After: $($enc::HtmlEncode($afterLabel)). ScreenshotRunner on the Test host (trimmed Release client, fake AI), full pages at 1366 and 390 wide, effects off; red boxes mark changed pixels.</p>")
        if ($Notes) { [void]$sb.Append('<ul>' + (($Notes | ForEach-Object { "<li>$($enc::HtmlEncode($_))</li>" }) -join '') + '</ul>') }
        if ($warnings.Count) { [void]$sb.Append('<ul class="warn">' + (($warnings | Select-Object -First 12 | ForEach-Object { "<li>$($enc::HtmlEncode($_))</li>" }) -join '') + '</ul>') }
        foreach ($r in $items) {
            $wpx = [int]($(if ($r.Phone) { 390 * $phoneScale } else { 1366 * $deskScale }))
            $what = if (-not $r.Diff) { '<span class="chg">new (no before shot)</span>' }
                elseif ($r.Changed) { "<span class=`"chg`">changed: $($r.Diff.pct)% of pixels, $($r.Diff.boxes) area(s)</span>" }
                else { '<span class="same">no visible change</span>' }
            [void]$sb.Append("<h2>$($enc::HtmlEncode($r.Name)): $what</h2><div class=`"pair`">")
            if ($r.Changed -or $all) {
                if ($r.Before) { [void]$sb.Append("<figure><figcaption>Before</figcaption><img width=`"$wpx`" alt=`"before`" src=`"$($r.Before)`"></figure>") }
                $shown = if ($r.Diff) { $r.Diff.marked } else { $r.After }
                [void]$sb.Append("<figure><figcaption>After</figcaption><img width=`"$wpx`" alt=`"after`" src=`"$shown`"></figure>")
            }
            else { [void]$sb.Append("<figure><figcaption>Both</figcaption><img width=`"$([int]($wpx / 2))`" alt=`"unchanged`" src=`"$($r.After)`"></figure>") }
            [void]$sb.Append('</div>')
        }
        [void]$sb.Append('</body></html>')
        $sb.ToString()
    }

    New-Item -ItemType Directory -Force $outDir | Out-Null
    $html = Join-Path $outDir "$Date-$Feature.html"
    $png = Join-Path $outDir "$Date-$Feature.png"
    [IO.File]::WriteAllText($html, (Format-Page $rows 0.5 0.6 $false), [Text.UTF8Encoding]::new($false))

    # The PNG stays readable and small: the 4 most-changed routes (the HTML has all of them).
    $pngRows = @($rows | Where-Object Changed | Sort-Object { if ($_.Diff) { $_.Diff.pct } else { 100 } } -Descending | Select-Object -First 4)
    if (-not $pngRows) { $pngRows = @($rows | Select-Object -First 2) }
    $more = @($rows | Where-Object Changed).Count - $pngRows.Count
    if ($more -gt 0) { $Notes += "PNG: the $($pngRows.Count) most-changed shots; $more more changed shot(s) are in the HTML page." }
    foreach ($scale in 0.45, 0.35, 0.28, 0.22, 0.16) {
        $shot = Wait-Task ($browser.NewPageAsync([Microsoft.Playwright.BrowserNewPageOptions]@{ ViewportSize = [Microsoft.Playwright.ViewportSize]@{ Width = 1300; Height = 800 } }))
        Wait-Task ($shot.SetContentAsync((Format-Page $pngRows $scale ($scale * 1.4) $true))) | Out-Null
        Wait-Task ($shot.ScreenshotAsync([Microsoft.Playwright.PageScreenshotOptions]@{ Path = $png; FullPage = $true })) | Out-Null
        Wait-Task ($shot.CloseAsync()) | Out-Null
        if ((Get-Item $png).Length -le 300KB) { break }
    }
    Write-Host ''
    Write-Host "Page: $html ($([int]((Get-Item $html).Length / 1KB)) KB)" -ForegroundColor Green
    Write-Host "PNG:  $png ($([int]((Get-Item $png).Length / 1KB)) KB)" -ForegroundColor Green
    $rows | ForEach-Object { "{0,-22} {1}" -f $_.Name, $(if (-not $_.Diff) { 'new' } elseif ($_.Changed) { "changed $($_.Diff.pct)%" } else { 'same' }) }
    $warnings | ForEach-Object { Write-Warning $_ }
}
finally {
    if ($browser) { Wait-Task ($browser.CloseAsync()) | Out-Null }
    if ($pw) { $pw.Dispose() }
    foreach ($w in $worktrees) { git -C $repoRoot worktree remove --force $w 2>&1 | Out-Null }
    if (-not $KeepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
