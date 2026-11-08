param(
    [Parameter(Mandatory)] [ValidateRange(0, 100)] [int] $Percent,
    [Parameter(Mandatory)] [ValidateRange(0, [long]::MaxValue)] [long] $ExpectedRevision,
    [string] $BaseUrl = 'http://localhost:5080'
)
$ErrorActionPreference = 'Stop'
if (-not $env:Admin__Token) { throw 'Set Admin__Token first.' }

$headers = @{ Authorization = "Bearer $env:Admin__Token" }
$environment = "$BaseUrl/v1/admin/environments/demo"
Invoke-RestMethod -Method Put -Uri $environment -Headers $headers | Out-Null

$body = @{
    expectedRevision = $ExpectedRevision
    flags = @{
        checkout = @{
            default = $false
            rolloutPercent = $Percent
            overrides = @{ 'preview-user' = $true; 'legacy-user' = $false }
            rules = @(@{ attribute = 'tier'; equalsValue = 'staff'; enabled = $true })
        }
    }
} | ConvertTo-Json -Depth 8
Invoke-RestMethod -Method Post -Uri "$environment/publications" -Headers $headers -ContentType application/json -Body $body
