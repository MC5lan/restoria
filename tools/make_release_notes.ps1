# make_release_notes.ps1 — build the release body from CHANGELOG.md.
#
# Why generate it instead of writing it twice: the release body and the
# repository changelog are the same facts, and a hand-copied body drifts from
# the changelog within one release. Here the body *is* the changelog entry,
# wrapped in the download and requirements sections that belong to the release
# page alone.
#
# Usage: powershell -File tools\make_release_notes.ps1 -Version 1.5.7 [-OutFile path]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutFile = "$env:TEMP\restoria-release-notes.md"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)

$changelog = Join-Path $root 'CHANGELOG.md'
if (-not (Test-Path $changelog)) { throw "CHANGELOG.md not found at $changelog" }
$text = [System.IO.File]::ReadAllText($changelog, [System.Text.Encoding]::UTF8)

# Take the section whose heading carries this version. The heading uses an
# em dash (U+2014); match it by shape rather than by literal so a typo in the
# changelog cannot silently produce an empty body.
$pattern = '(?ms)^##\s+' + [regex]::Escape($Version) + '\s*[\u2014-].*?(?=^##\s|\z)'
$m = [regex]::Match($text, $pattern)
if (-not $m.Success) { throw "CHANGELOG.md has no entry for $Version" }
$section = $m.Value.Trim()

# Prefer the English summary block if the entry carries one. The release page is
# read by people who do not read the repository's primary language, and the
# detailed notes below the block stay in the changelog where they belong.
# The block is delimited by explicit markers rather than guessed from quoting:
# an earlier attempt matched on leading '>' and captured the wrong paragraph.
$en = [regex]::Match($section, '(?ms)^<!-- release-notes:start -->\r?\n(?<body>.*?)^<!-- release-notes:end -->')
if ($en.Success) {
    $entry = @(
        '**Release notes**'
        ''
        $en.Groups['body'].Value.Trim()
    ) -join "`n"
}
else {
    $entry = $section
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Restoria v$Version")
$lines.Add('')
$lines.Add($entry)
$lines.Add('')
$lines.Add('---')
$lines.Add('')
$lines.Add('## Download')
$lines.Add('')
$lines.Add('| Asset | What it is |')
$lines.Add('|---|---|')
$lines.Add('| `Restoria-setup.exe` | Single-file installer. Double-click, accept the license, done. |')
$lines.Add('| `Restoria-portable.zip` | No install needed. Extract, run `setup-boot.exe`. |')
$lines.Add('')
$lines.Add('## Requirements')
$lines.Add('')
$lines.Add('- Windows 7 SP1 or later, x64 or 32-bit')
$lines.Add('- .NET Framework 4.8')
$lines.Add('- No code-signing certificate is configured yet, so Windows shows a publisher')
$lines.Add('  warning on first run. Choose *More info -> Run anyway*.')
$lines.Add('')
$lines.Add('Everything runs locally. No network access at any point. The optional AI')
$lines.Add('inpainting and seal OCR need the bundled `ai/` payload (about 59 MB) and switch')
$lines.Add('themselves off cleanly when the runtime is absent; the core pipeline never does.')
$lines.Add('')
$lines.Add('## Known limitations')
$lines.Add('')
$lines.Add('- Rotation needs physical evidence. Pages with no binding holes (glue-bound')
$lines.Add('  books, loose sheets, two-hole documents) are not rotated automatically: turning')
$lines.Add('  an already-upright table page is worse than leaving a flipped one for someone to')
$lines.Add('  notice. Use the manual control, or the Exhaustive mode, which falls back to OCR.')
$lines.Add('- Orientation thresholds are calibrated for roughly 300 DPI scans.')
$lines.Add('- Designed for flatbed-scanner output; phone photos have perspective distortion.')
$lines.Add('')
$lines.Add('Licence: MIT. Bundled third-party components keep their own licences; see')
$lines.Add('THIRD_PARTY_NOTICES in the repository.')

$out = $lines -join "`n"
[System.IO.File]::WriteAllText($OutFile, $out, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("wrote {0} ({1} bytes) from the {2} changelog entry" -f $OutFile, $out.Length, $Version)