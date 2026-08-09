<#
.SYNOPSIS
  Copy raw audio into category folders so normalize-audio.ps1 can apply per-category
  LUFS targets.

.WHY COPY AND NOT MOVE
  The originals stay untouched. Normalization is lossy in the sense that matters - once
  a file has been gain-adjusted you cannot recover the original headroom decision by
  running it again - so the pipeline is always raw -> sorted -> normalized -> Unity,
  with the raws kept outside Assets/ and never processed in place.

.CATEGORIES
  These match the mixer groups in SOUNDSYSTEM_PLAN section 1, with one addition:
  VOICE is split out from combat, because the project's rule is that audio is grouped by
  SOURCE rather than by situation. A goblin's death cry and its sword whoosh happen at
  the same moment but come from different things - and NpcCombatAudio's source is the
  one NpcFace reads to drive the jaw, so anything routed there literally moves the
  goblin's mouth.

.USAGE
  Preview (default - writes nothing):
      .\Tools\sort-audio.ps1 -In Assets\Audio\SoundFiles -Out raw_sorted
  Commit:
      .\Tools\sort-audio.ps1 -In Assets\Audio\SoundFiles -Out raw_sorted -Apply
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $In,
    [Parameter(Mandatory = $true)] [string] $Out,
    [switch] $Apply
)

# First matching pattern wins, so ORDER MATTERS - the specific rules sit above the
# general ones. Regex, case-insensitive, matched against the file name only.
#
# Two entries are marked GUESS: they are the ones I could not tell from the name alone.
# Check those before running with -Apply.
$Rules = @(
    @{ Pattern = '^Dungeon_Ambient.*Loop';        Category = 'ambient_beds' }
    @{ Pattern = 'Torch_Flame';                   Category = 'ambient_beds' }
    @{ Pattern = '^Cave_water_drips';             Category = 'ambient_beds' }   # a bed, not a one-shot: plural + no index

    @{ Pattern = '^water_drip_\d';                Category = 'ambient_oneshots' }
    @{ Pattern = '^Ritual_Chant';                 Category = 'ambient_oneshots' }

    @{ Pattern = '^a_single_soft';                Category = 'footsteps' }      # GUESS: footstep variants?
    @{ Pattern = '^landing_a_jump';               Category = 'footsteps' }

    # CONTINUOUS door sounds sit BELOW the transient ones. A sustained loop at the same
    # integrated LUFS as a thunk feels considerably louder, because it is always there
    # while the thunk is gone in 200 ms - the same reason ambient beds sit lowest of all.
    # These two are PhysicsDoorAudio's creak loop; the rest of the door sounds are its
    # one-shot thunks and slams.
    @{ Pattern = '_(Opening|Open)_Loop';          Category = 'physics_loops' }

    @{ Pattern = '^Goblin_(Death|Grunt)';         Category = 'voice' }
    @{ Pattern = '^Exertion';                     Category = 'voice' }

    @{ Pattern = 'whoosh';                        Category = 'combat' }
    @{ Pattern = 'sword_.*impact';                Category = 'combat' }
    @{ Pattern = '^(Bone|Wood)_impac';            Category = 'combat' }         # note: Wood_impace_01 is misspelled in source

    @{ Pattern = 'door';                          Category = 'physics' }
    @{ Pattern = 'coffin|chest';                  Category = 'physics' }
)
$Fallback = 'unsorted'

$RepoRoot = Split-Path $PSScriptRoot -Parent
function Resolve-Anchored {
    param([string] $Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    if (Test-Path $Path) { return (Resolve-Path $Path).Path }
    $alt = Join-Path $RepoRoot $Path
    if (Test-Path $alt) { return (Resolve-Path $alt).Path }
    return (Join-Path $RepoRoot $Path)
}

$In = Resolve-Anchored $In
$Out = Resolve-Anchored $Out
if (-not (Test-Path $In)) { Write-Error "Input folder not found: $In"; return }

$files = Get-ChildItem -Path $In -Recurse -File -Include *.wav, *.mp3, *.ogg, *.aif, *.aiff
Write-Host ""
Write-Host ("{0} file(s) in {1}" -f $files.Count, $In) -ForegroundColor Cyan
if (-not $Apply) { Write-Host "PREVIEW - nothing will be written. Add -Apply to commit." -ForegroundColor Yellow }
Write-Host ""

$plan = @()
foreach ($f in $files) {
    $cat = $Fallback
    foreach ($r in $Rules) {
        if ($f.Name -match $r.Pattern) { $cat = $r.Category; break }
    }
    $plan += [pscustomobject]@{ Name = $f.Name; Category = $cat; Path = $f.FullName }
}

foreach ($g in $plan | Group-Object Category | Sort-Object Name) {
    $colour = if ($g.Name -eq $Fallback) { 'Yellow' } else { 'Gray' }
    Write-Host ("{0}  ({1})" -f $g.Name, $g.Count) -ForegroundColor $colour
    foreach ($item in $g.Group) { Write-Host ("    {0}" -f $item.Name) }
    Write-Host ""
}

$un = ($plan | Where-Object { $_.Category -eq $Fallback }).Count
if ($un -gt 0) {
    Write-Host ("{0} file(s) matched no rule and would land in '{1}'. Add a pattern for them, " -f $un, $Fallback) -ForegroundColor Yellow
    Write-Host "or let them take the default target." -ForegroundColor Yellow
    Write-Host ""
}

if (-not $Apply) { return }

foreach ($item in $plan) {
    $dir = Join-Path $Out $item.Category
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item -Path $item.Path -Destination (Join-Path $dir $item.Name) -Force
}
Write-Host ("Copied {0} file(s) into {1}" -f $plan.Count, $Out) -ForegroundColor Green
Write-Host ""
Write-Host "Next:" -ForegroundColor Cyan
Write-Host ("  .\Tools\normalize-audio.ps1 -In {0} -Out normalized_audio" -f (Split-Path $Out -Leaf))
