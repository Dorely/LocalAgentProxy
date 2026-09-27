param(
    [Parameter(Mandatory = $true)][ValidateRange(3, 3)][int]$AuthorizedGenerations,
    [string]$Model = 'claude-opus-5-5',
    [string]$Executable = "$PSScriptRoot\..\src\LocalAgentProxy\bin\Release\net10.0\LocalAgentProxy.exe"
)
$ErrorActionPreference = 'Stop'
# Explicit opt-in only. Three additional model generations: tool request, result,
# image. Never run this from the automated test suite or CI.
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$config = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'LocalAgentProxy\config.json') -Raw | ConvertFrom-Json
$base = $config.Url
$keyOutput = & $Executable clients add 'Live validation (temporary)'
if ($LASTEXITCODE -ne 0) { throw 'Could not create temporary client.' }
$clientId = ($keyOutput | Select-String '^Client ID: ').ToString().Substring(11)
$key = ($keyOutput | Select-String '^API key \(shown once\): ').ToString().Substring(22)
$headers = @{ Authorization = "Bearer $key" }
$issued = 0
function Chat($body) {
    Invoke-RestMethod -Method Post -Uri "$base/v1/chat/completions" -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 50 -Compress) -TimeoutSec 150
}
try {
    $messages = @(@{ role = 'user'; content = 'Call read_fixture exactly once. After it returns, answer with the exact value returned. Do not guess the value.' })
    $tools = @(@{ type = 'function'; function = @{ name = 'read_fixture'; description = 'Read a fixture string supplied by the caller'; parameters = @{ type = 'object'; properties = @{}; additionalProperties = $false } } })
    $issued++
    $first = Chat @{ model = $Model; messages = $messages; tools = $tools }
    if ($first.choices[0].finish_reason -ne 'tool_calls') { throw 'Expected real tool invocation.' }
    $status = Invoke-RestMethod -Uri "$base/v1/status" -Headers $headers
    if ($status.conversations -ne 1 -or $status.active_generation) { throw 'Waiting tool did not release generation capacity.' }
    $nonce = 'caller-' + [Guid]::NewGuid().ToString('N')
    $messages += $first.choices[0].message
    $messages += @{ role = 'tool'; tool_call_id = $first.choices[0].message.tool_calls[0].id; content = $nonce }
    $issued++
    $second = Chat @{ model = $Model; messages = $messages; tools = $tools }
    if ($second.choices[0].finish_reason -ne 'stop' -or -not $second.choices[0].message.content.Contains($nonce)) { throw 'Final answer did not contain actual caller result.' }
    $status = Invoke-RestMethod -Uri "$base/v1/status" -Headers $headers
    if ($status.conversations -ne 0) { throw 'Completed continuation was not reclaimed.' }
    Write-Output 'PASS: public REST tool round trip, caller nonce, waiting-slot release, final text, continuation cleanup.'

    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new(128, 128)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $memory = [System.IO.MemoryStream]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Red)
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = [Convert]::ToBase64String($memory.ToArray())
    } finally { $graphics.Dispose(); $bitmap.Dispose(); $memory.Dispose() }
    $issued++
    $image = Chat @{ model = $Model; messages = @(@{ role = 'user'; content = @(@{type='text'; text='What is the dominant color of this image? Reply with one color word.'}, @{type='image_url'; image_url=@{url="data:image/png;base64,$data"}}) }) }
    if ($image.choices[0].message.content -notmatch '(?i)red') { throw 'Image result did not identify the fixture color.' }
    Write-Output 'PASS: base64 image input identified the fixture color.'
} finally {
    & $Executable clients remove $clientId
    Write-Output "Additional live model generations requested: $issued (budget: $AuthorizedGenerations)."
}
