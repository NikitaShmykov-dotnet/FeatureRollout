$ErrorActionPreference = 'Stop'
$demo = Join-Path $PSScriptRoot 'FeatureRollout.Demo/bin/Release/net10.0/FeatureRollout.Demo.dll'
if (-not (Test-Path -LiteralPath $demo)) { throw 'Build the demo in Release first.' }

$vectors = @(
    @{ Key = 'checkout'; User = 'user-000001'; Expected = '9654' }
    @{ Key = 'ab'; User = 'c'; Expected = '6104' }
    @{ Key = 'a'; User = 'bc'; Expected = '8830' }
    @{ Key = 'checkout'; User = 'пользователь'; Expected = '3203' }
)

foreach ($vector in $vectors) {
    for ($run = 0; $run -lt 2; $run++) {
        $bucket = dotnet $demo bucket $vector.Key $vector.User
        if ($LASTEXITCODE -ne 0 -or $bucket -cne $vector.Expected) {
            throw "Bucket mismatch for $($vector.Key)/$($vector.User): $bucket"
        }
    }
}
'4 vectors matched in 8 separate processes.'
