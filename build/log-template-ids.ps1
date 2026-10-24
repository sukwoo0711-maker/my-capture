[CmdletBinding()]
param(
    # msg= values from a log line, such as 3fa2c1d0. Without it, every template is listed.
    [string[]]$Id
)

# A MyCapture log line keeps only an eight-digit SHA-256 prefix of its message template
# (SafeLogLine.TemplateId), never the words. This maps those values back to the literal
# templates passed to Log* calls under src, so a diagnostics bundle reads as call sites.
# Templates built by concatenation are not matched.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceRoot = Join-Path $repoRoot 'src'
$call = [regex]'\.Log(?:Trace|Debug|Information|Warning|Error|Critical)\((?:[^"()]|\([^()]*\))*?"((?:[^"\\]|\\.)*)"'
$sha = [Security.Cryptography.SHA256]::Create()
$wanted = @($Id | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() })

try {
    Get-ChildItem -LiteralPath $sourceRoot -Recurse -Filter '*.cs' -File | Sort-Object FullName | ForEach-Object {
        $file = $_
        $text = [IO.File]::ReadAllText($file.FullName)
        foreach ($match in $call.Matches($text)) {
            $template = [regex]::Unescape($match.Groups[1].Value)
            $digest = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($template))
            $templateId = -join ($digest[0..3] | ForEach-Object { $_.ToString('x2') })
            if ($wanted.Count -gt 0 -and $wanted -notcontains $templateId) { continue }
            $line = ($text.Substring(0, $match.Index) -split "`n").Count
            [pscustomobject]@{
                Id = $templateId
                Location = '{0}:{1}' -f $file.FullName.Substring($repoRoot.Length + 1), $line
                Template = $template
            }
        }
    }
}
finally {
    $sha.Dispose()
}
