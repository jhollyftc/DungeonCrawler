<#
.SYNOPSIS
  Measure and normalize game audio to per-category targets, by LUFS or by true peak.

.THE TWO MODES, AND WHY BOTH ARE NEEDED
  LUFS (integrated loudness) is the right metric for PROGRAMS - ambient beds, loops,
  music, dialogue. It measures perceived loudness over time, which is what makes a
  drone and a hum sit together properly.

  It is the WRONG metric for SHORT ONE-SHOTS, and this project's own data proves it: a
  whoosh measured -23.9 LUFS while peaking at -0.88 dBTP. That is not a quiet file, it
  is a 200 ms crack surrounded by silence - integrated LUFS gates and averages over
  time, so a transient in a mostly-empty file reads as almost nothing. Chasing a LUFS
  target on those files fails in a specific way: each clip clamps against the true-peak
  ceiling at a different point according to its own crest factor, so they miss the
  target AND stay inconsistent with each other, which is the actual problem.

  So: continuous material -> LUFS. Transients -> PEAK. The mode lives in the category
  table below because it is a property of the MATERIAL, not a choice made per run.

.USAGE
  Report where everything currently sits (writes nothing):
      .\Tools\normalize-audio.ps1 -In raw_sorted -Measure
  Show what each category can actually reach, and name the outliers holding it back:
      .\Tools\normalize-audio.ps1 -In raw_sorted -Suggest
  Normalize:
      .\Tools\normalize-audio.ps1 -In raw_sorted -Out normalized_audio
  Normalize AND copy into the project (only what actually changed):
      .\Tools\normalize-audio.ps1 -In raw_sorted -Out normalized_audio -Install

.REQUIRES
  ffmpeg and ffprobe on PATH.  winget install Gyan.FFmpeg
  (After installing, open a NEW PowerShell - PATH changes do not reach a running one.)
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $In,
    [string] $Out,
    [switch] $Measure,
    [switch] $Suggest,
    # Copy the results straight into the project, into the matching category folder.
    [switch] $Install,
    [string] $InstallRoot = 'Assets\Audio\SoundFiles',
    [int]    $SampleRate = 48000,
    [double] $TruePeakCeiling = -1.0,
    [double] $Tolerance = 1.0
)

# Category (folder name directly under -In) -> how to normalize it.
#   lufs = integrated loudness, for continuous material
#   peak = true peak, for short transients
# Peak targets sit below the -1 dBTP ceiling so summed sources have room before clipping.
$Categories = @{
    'ambient_beds'     = @{ Mode = 'lufs'; Target = -24.0 }
    'physics_loops'    = @{ Mode = 'lufs'; Target = -23.0 }
    'music'            = @{ Mode = 'lufs'; Target = -17.0 }

    'ambient_oneshots' = @{ Mode = 'peak'; Target =  -6.0 }
    'footsteps'        = @{ Mode = 'peak'; Target =  -6.0 }
    'physics'          = @{ Mode = 'peak'; Target =  -4.0 }
    'combat'           = @{ Mode = 'peak'; Target =  -3.0 }
    'voice'            = @{ Mode = 'peak'; Target =  -3.0 }
    'ui'               = @{ Mode = 'peak'; Target =  -3.0 }
}
$DefaultCategory = @{ Mode = 'peak'; Target = -4.0 }

# ffmpeg wants '.' as the decimal separator whatever the machine's locale says. PowerShell's
# -f operator uses the CURRENT culture, so on a comma-decimal machine this would silently
# emit "volume=-3,2dB" and ffmpeg would reject or misread it.
function Num { param([double] $v, [int] $dp = 2) return $v.ToString("F$dp", [cultureinfo]::InvariantCulture) }

# ---------------------------------------------------------------------------
# ffmpeg reports DIGITAL SILENCE as "-inf", not as a number, and PowerShell's [double]
# cast throws on it ("Cannot convert value -inf to type System.Double"). The error names
# the pscustomobject line rather than the file, so a single empty clip anywhere in the
# batch aborts the run with no clue which file caused it.
#
# Also parses with InvariantCulture explicitly: ffmpeg always emits "-14.2", but a
# comma-decimal locale would read that as a thousands separator and silently return
# -142. Same reason Num() formats invariantly on the way out.
# ---------------------------------------------------------------------------
function ToNum {
    param([string] $s)
    if ([string]::IsNullOrWhiteSpace($s)) { return [double]::NaN }
    $t = $s.Trim()
    if ($t -match '^-\s*inf')  { return [double]::NegativeInfinity }
    if ($t -match '^\+?\s*inf') { return [double]::PositiveInfinity }
    if ($t -match '^nan')      { return [double]::NaN }
    $d = 0.0
    if ([double]::TryParse($t, [Globalization.NumberStyles]::Float, [cultureinfo]::InvariantCulture, [ref] $d)) { return $d }
    return [double]::NaN
}

# True for a measurement that cannot be normalized: silence (-inf) or a failed parse.
function BadNum { param([double] $v) return ([double]::IsNaN($v) -or [double]::IsInfinity($v)) }

# Format an ffmpeg measurement for feeding BACK into an ffmpeg filter string. Non-finite
# values are floored, because "-Infinity" in a loudnorm argument is a parse error at the
# other end - and measured_thresh in particular comes back as -inf on very quiet material
# even when the integrated loudness itself is a perfectly good number.
function NumS {
    param([string] $s, [double] $floor = -99, [int] $dp = 2)
    $v = ToNum $s
    if (BadNum $v) { $v = $floor }
    return Num $v $dp
}

# ---------------------------------------------------------------------------
# PS 5.1 wraps a native exe's stderr in ErrorRecords and reports failure on a successful
# exit when you pipe it. ProcessStartInfo reads the stream directly and avoids all of it.
# ---------------------------------------------------------------------------
function Invoke-Capture {
    param([string] $Exe, [string] $Arguments)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $Exe
    $psi.Arguments              = $Arguments
    $psi.RedirectStandardError  = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $err = $p.StandardError.ReadToEnd()
    $out = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    return [pscustomobject]@{ Err = $err; Out = $out; Code = $p.ExitCode }
}

function Get-Duration {
    param([string] $Path)
    $r = Invoke-Capture 'ffprobe' ('-v error -show_entries format=duration -of default=nw=1:nk=1 "{0}"' -f $Path)
    $d = 0.0
    [double]::TryParse($r.Out.Trim(), [ref] $d) | Out-Null
    return $d
}

# One measurement serves BOTH modes: loudnorm reports integrated loudness AND true peak,
# so a peak-mode file needs no separate analysis pass.
function Measure-Audio {
    param([string] $Path)
    $af = 'loudnorm=I=-24:TP={0}:LRA=11:print_format=json' -f (Num $TruePeakCeiling)
    $r = Invoke-Capture 'ffmpeg' ('-hide_banner -nostats -i "{0}" -af {1} -f null -' -f $Path, $af)
    $s = $r.Err.LastIndexOf('{'); $e = $r.Err.LastIndexOf('}')
    if ($s -lt 0 -or $e -le $s) { return $null }
    try { return $r.Err.Substring($s, $e - $s + 1) | ConvertFrom-Json } catch { return $null }
}

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    Write-Error ("ffmpeg not found on PATH.`n  winget install Gyan.FFmpeg`n" +
                 "Then OPEN A NEW POWERSHELL WINDOW - PATH changes do not reach a running session.")
    return
}

# Relative paths anchor to the REPO ROOT, not the current directory: this script lives in
# Tools/, so the natural thing to type is a repo-relative path.
$RepoRoot = Split-Path $PSScriptRoot -Parent
function Resolve-Anchored {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    if (Test-Path $Path) { return (Resolve-Path $Path).Path }
    $alt = Join-Path $RepoRoot $Path
    if (Test-Path $alt) { return (Resolve-Path $alt).Path }
    return $Path
}

$In = Resolve-Anchored $In
if (-not (Test-Path $In)) {
    Write-Error ("Input folder not found: {0}`n  (tried the current directory and {1})" -f $In, $RepoRoot)
    return
}
if ($Out -and -not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $RepoRoot $Out }
Write-Host ("in : {0}" -f $In) -ForegroundColor DarkGray
if ($Out) { Write-Host ("out: {0}" -f $Out) -ForegroundColor DarkGray }

$files = Get-ChildItem -Path $In -Recurse -File -Include *.wav, *.mp3, *.ogg, *.aif, *.aiff
if ($files.Count -eq 0) { Write-Warning "No audio files under $In"; return }

Write-Host ""
Write-Host ("{0} file(s)" -f $files.Count) -ForegroundColor Cyan
Write-Host ""

$rows = @()
$skipped = @()
$inRoot = (Resolve-Path $In).Path
foreach ($f in $files) {
    $rel = $f.FullName.Substring($inRoot.Length).TrimStart('\', '/')
    $parts = $rel -split '[\\/]'
    $cat = if ($parts.Count -gt 1) { $parts[0] } else { '(root)' }
    $spec = if ($Categories.ContainsKey($cat)) { $Categories[$cat] } else { $DefaultCategory }

    $m = Measure-Audio $f.FullName
    if ($null -eq $m) { Write-Warning ("could not measure: {0}" -f $rel); continue }
    $dur = Get-Duration $f.FullName

    $mI  = ToNum $m.input_i
    $mTP = ToNum $m.input_tp

    # A SILENT file cannot be normalized: there is no gain that raises silence to a
    # target, so skip it loudly and NAME it rather than aborting the whole batch. Almost
    # always an export that captured the wrong track, or a trim that landed on empty air.
    if ((BadNum $mI) -or (BadNum $mTP)) {
        Write-Warning ("SILENT or unmeasurable, skipped: {0}   (I={1}  TP={2})" -f $rel, $m.input_i, $m.input_tp)
        $skipped += $rel
        continue
    }

    $rows += [pscustomobject]@{
        Rel = $rel; Cat = $cat; Mode = $spec.Mode; Target = [double] $spec.Target
        I = $mI; TP = $mTP
        Dur = $dur; Meas = $m; Path = $f.FullName
        # Headroom is what a LINEAR gain can spend before the peak hits the ceiling.
        Room = $TruePeakCeiling - $mTP
    }

    $now = if ($spec.Mode -eq 'peak') { $mTP } else { $mI }
    $unit = if ($spec.Mode -eq 'peak') { 'dBTP' } else { 'LUFS' }
    $shrt = if ($dur -gt 0 -and $dur -lt 0.4 -and $spec.Mode -eq 'lufs') { '  SHORT for LUFS' } else { '' }
    Write-Host ("{0,-44} {1,-17} {2,7:N1} {3}  ->{4,6:N1}{5}" -f `
        ($rel.Substring(0, [Math]::Min(44, $rel.Length))), ("$cat/$($spec.Mode)"), $now, $unit, $spec.Target, $shrt)
}

# ---------------------------------------------------------------------------
if ($Measure -or $Suggest) {
    Write-Host ""
    Write-Host "PER CATEGORY" -ForegroundColor Cyan
    Write-Host ""
    foreach ($g in $rows | Group-Object Cat | Sort-Object Name) {
        $mode = $g.Group[0].Mode
        $tgt  = $g.Group[0].Target
        $vals = if ($mode -eq 'peak') { $g.Group | ForEach-Object { $_.TP } } else { $g.Group | ForEach-Object { $_.I } }
        $sorted = $vals | Sort-Object
        $min = $sorted[0]; $max = $sorted[-1]
        $median = $sorted[[int]([Math]::Floor($sorted.Count / 2))]
        $unit = if ($mode -eq 'peak') { 'dBTP' } else { 'LUFS' }

        Write-Host ("{0,-18} {1,3} file(s)  {2}  target {3,6:N1}   now {4,6:N1} .. {5,6:N1}  median {6,6:N1} {7}" -f `
            $g.Name, $g.Count, $mode, $tgt, $min, $max, $median, $unit)

        if ($Suggest -and $mode -eq 'lufs') {
            # What a linear gain can actually reach, per file. Report the MEDIAN, not the
            # minimum: lowering a whole category to accommodate one bad take drags every
            # healthy file down with it. The outliers are named instead, because fixing two
            # files beats penalising twenty.
            $ach = $g.Group | ForEach-Object { [pscustomobject]@{ Rel = $_.Rel; Max = $_.I + $_.Room } }
            $ms = ($ach | ForEach-Object { $_.Max } | Sort-Object)
            $achMedian = $ms[[int]([Math]::Floor($ms.Count / 2))]
            Write-Host ("                     reachable: median {0,6:N1}, worst {1,6:N1}" -f $achMedian, $ms[0])
            foreach ($o in $ach | Where-Object { $_.Max -lt $tgt } | Sort-Object Max) {
                Write-Host ("                       OUTLIER {0,-40} can only reach {1,6:N1}" -f $o.Rel, $o.Max) -ForegroundColor DarkYellow
            }
        }
    }
    if ($Suggest) {
        Write-Host ""
        Write-Host "Outliers are usually one quiet take or one already-hot file. Fix THOSE" -ForegroundColor Yellow
        Write-Host "(re-gain, re-trim, re-record) rather than lowering the category to match." -ForegroundColor Yellow
    }
    Write-Host ""
    return
}

# ---------------------------------------------------------------------------
if (-not $Out) { Write-Error "Specify -Out, or use -Measure / -Suggest."; return }

Write-Host ""
Write-Host "NORMALIZING" -ForegroundColor Cyan
$misses = @()

foreach ($r in $rows) {
    $dest = Join-Path $Out $r.Rel
    New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null

    if ($r.Mode -eq 'peak') {
        # One linear gain. No compression, no gating - the sound keeps its shape exactly,
        # which is the whole point for a transient.
        $gain = $r.Target - $r.TP
        $af = 'volume={0}dB' -f (Num $gain)
    } else {
        $m = $r.Meas
        # Two-pass loudnorm, feeding the measurements back so it applies a LINEAR gain
        # rather than dynamic compression - dynamic would alter the character, which is not
        # what "make these consistent" should ever mean.
        $af = ('loudnorm=I={0}:TP={1}:LRA=11:measured_I={2}:measured_TP={3}:measured_LRA={4}:' +
               'measured_thresh={5}:offset={6}:linear=true:print_format=summary') -f `
               (Num $r.Target), (Num $TruePeakCeiling), (NumS $m.input_i), (NumS $m.input_tp), `
               (NumS $m.input_lra 0), (NumS $m.input_thresh), (NumS $m.target_offset 0)
    }

    $enc = Invoke-Capture 'ffmpeg' ('-hide_banner -nostats -y -i "{0}" -af {1} -ar {2} -c:a pcm_s16le "{3}"' -f `
                                    $r.Path, $af, $SampleRate, $dest)
    if ($enc.Code -ne 0) { Write-Warning ("encode failed: {0}" -f $r.Rel); continue }

    # VERIFY on the axis that was targeted. Normalization fails quietly - loudnorm falls
    # back to dynamic mode when it cannot reach a target linearly - and a claim of success
    # that nobody checked is how the first pass produced 23 silent misses.
    $after = Measure-Audio $dest
    if ($null -eq $after) { Write-Warning ("could not verify: {0}" -f $r.Rel); continue }
    $got = if ($r.Mode -eq 'peak') { ToNum $after.input_tp } else { ToNum $after.input_i }
    # A verified file measuring -inf means the ENCODE produced silence from audible input,
    # which is a worse failure than a missed target and must not be reported as a near miss.
    if (BadNum $got) { Write-Warning ("VERIFY: output is SILENT: {0}" -f $r.Rel); continue }
    $unit = if ($r.Mode -eq 'peak') { 'dBTP' } else { 'LUFS' }

    $flag = ''
    if ([Math]::Abs($got - $r.Target) -gt $Tolerance) {
        $flag = '   MISS'
        $misses += [pscustomobject]@{ Rel = $r.Rel; Got = $got; Want = $r.Target; Mode = $r.Mode; Unit = $unit }
    }
    Write-Host ("{0,-44} -> {1,6:N1} {2} (target {3,6:N1}){4}" -f `
        ($r.Rel.Substring(0, [Math]::Min(44, $r.Rel.Length))), $got, $unit, $r.Target, $flag)
}

Write-Host ""

# ---------------------------------------------------------------------------
if ($Install) {
    $root = Resolve-Anchored $InstallRoot
    Write-Host ("INSTALLING into {0}" -f $root) -ForegroundColor Cyan

    # ONLY FILES WHOSE CONTENTS ACTUALLY CHANGED are copied. Normalizing is idempotent -
    # a file already on target gets a zero-gain pass - so a re-run would otherwise rewrite
    # all 64 clips, making Unity reimport the lot and turning a two-file addition into a
    # 64-file diff that hides what really changed.
    $added = 0; $updated = 0; $same = 0; $blocked = 0

    # A running editor can regenerate a .meta while we replace the file beneath it, which
    # is how a REPLACEMENT loses its guid and every reference to it. Adding NEW files is
    # safe either way, so this only refuses when it would actually overwrite something.
    $unityUp = @(Get-Process Unity -ErrorAction SilentlyContinue).Count -gt 0

    foreach ($r in $rows) {
        $src = Join-Path $Out $r.Rel
        if (-not (Test-Path $src)) { continue }
        $dst = Join-Path $root $r.Rel
        $isNew = -not (Test-Path $dst)

        if (-not $isNew) {
            $a = (Get-FileHash $src -Algorithm MD5).Hash
            $b = (Get-FileHash $dst -Algorithm MD5).Hash
            if ($a -eq $b) { $same++; continue }
            if ($unityUp) {
                Write-Host ("  BLOCKED (Unity running, would replace) {0}" -f $r.Rel) -ForegroundColor Red
                $blocked++; continue
            }
        }

        New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
        Copy-Item $src $dst -Force
        if ($isNew) { $added++;   Write-Host ("  + {0}" -f $r.Rel) -ForegroundColor Green }
        else        { $updated++; Write-Host ("  ~ {0}" -f $r.Rel) -ForegroundColor Yellow }
    }

    Write-Host ""
    Write-Host ("  {0} new, {1} updated, {2} unchanged (skipped)" -f $added, $updated, $same)
    if ($blocked -gt 0) {
        Write-Host ("  {0} REPLACEMENT(S) REFUSED - close Unity and re-run." -f $blocked) -ForegroundColor Red
        Write-Host "  Replacing a file while the editor is live can cost it its .meta, and" -ForegroundColor Red
        Write-Host "  with it the guid every AudioProfile and prefab uses to find it." -ForegroundColor Red
    }
    if ($added -gt 0) {
        Write-Host ""
        Write-Host "  New clips need import settings: Force To Mono for POSITIONAL sounds" -ForegroundColor Cyan
        Write-Host "  (with Normalize UNTICKED), and Streaming load type for long loops." -ForegroundColor Cyan
    }
    Write-Host ""
}

if ($misses.Count -eq 0) {
    Write-Host ("All {0} file(s) within {1} of target." -f $rows.Count, $Tolerance) -ForegroundColor Green
} else {
    Write-Host ("{0} miss(es):" -f $misses.Count) -ForegroundColor Yellow
    foreach ($x in $misses) {
        $why = if ($x.Mode -eq 'lufs') { '  (LUFS mode - crest factor may be capping it; see -Suggest)' } else { '' }
        Write-Host ("  {0,-44} got {1,6:N1} {2}, wanted {3,6:N1}{4}" -f $x.Rel, $x.Got, $x.Unit, $x.Want, $why)
    }
}

if ($skipped.Count -gt 0) {
    Write-Host ""
    Write-Host ("{0} file(s) SKIPPED as silent/unmeasurable - these were NOT written:" -f $skipped.Count) -ForegroundColor Red
    foreach ($s in $skipped) { Write-Host ("  {0}" -f $s) }
    Write-Host "  Check the export: an all-silent clip is usually the wrong track or an empty trim."
}

Write-Host ""
Write-Host "IN UNITY, after importing:" -ForegroundColor Cyan
Write-Host "  - Force To Mono on POSITIONAL clips, but UNTICK the Normalize box beside it."
Write-Host "    It peak-normalizes each file on import and undoes everything above."
Write-Host "  - Long ambient loops: Load Type = Streaming. Short one-shots: Decompress On Load."
