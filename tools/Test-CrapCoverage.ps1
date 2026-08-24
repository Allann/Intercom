param(
    [Parameter(Mandatory = $true)] [string] $Report,
    [double] $Threshold = 15,
    [string] $Assembly = 'Intercom.Core',
    [string] $WriteSnapshot,
    [string] $CompareSnapshot
)

$ErrorActionPreference = 'Stop'
$reportPath = (Resolve-Path -LiteralPath $Report).Path
[xml] $coverage = Get-Content -Raw -LiteralPath $reportPath
$module = @($coverage.CoverageSession.Modules.Module) |
    Where-Object { $_.ModuleName -eq $Assembly }
if ($module.Count -ne 1) {
    throw "Expected one OpenCover module named '$Assembly'; found $($module.Count)."
}

$files = @{}
foreach ($file in $module.Files.File) { $files[[string] $file.uid] = [string] $file.fullPath }

function Test-GeneratedMethod($className, $methodName, $filePath) {
    if ($filePath -match '[\\/]obj[\\/]' -or $filePath -like '*.g.cs') { return $true }
    if ($className -like 'System.Text.RegularExpressions.Generated.*') { return $true }
    if ($className -like 'System.Text.RegularExpressions.Generator.RegexGenerator*') { return $true }
    if ($className -match '(^|/)<>') { return $true }
    if ($methodName -match '(^|::)<.*>g__') { return $true }
    if ($methodName -match '/<.*>d__\d+::MoveNext\(') { return $true }
    return $false
}

$methods = foreach ($class in $module.Classes.Class) {
    $className = [string] $class.FullName
    foreach ($method in @($class.Methods.Method)) {
        $methodName = [string] $method.Name
        $filePath = $files[[string] $method.FileRef.uid]
        if (Test-GeneratedMethod $className $methodName $filePath) { continue }
        $points = @($method.SequencePoints.SequencePoint)
        $covered = @($points | Where-Object { [int] $_.vc -gt 0 }).Count
        $coverageRatio = if ($points.Count -eq 0) { 0.0 } else { $covered / $points.Count }
        $complexity = [double] $method.cyclomaticComplexity
        $crap = $complexity * $complexity * [Math]::Pow(1 - $coverageRatio, 3) + $complexity
        [pscustomobject] @{
            Method = $methodName
            File = $files[[string] $method.FileRef.uid]
            Complexity = [int] $complexity
            Coverage = [Math]::Round($coverageRatio * 100, 2)
            Crap = [Math]::Round($crap, 2)
        }
    }
}

$result = [pscustomobject] @{
    Assembly = $Assembly
    Threshold = $Threshold
    MethodCount = @($methods).Count
    Offenders = @($methods | Where-Object { $_.Crap -gt $Threshold } | Sort-Object Method)
}
$json = $result | ConvertTo-Json -Depth 5

if ($WriteSnapshot) { Set-Content -LiteralPath $WriteSnapshot -Value $json -Encoding utf8 }
if ($CompareSnapshot) {
    $expected = Get-Content -Raw -LiteralPath $CompareSnapshot | ConvertFrom-Json
    $actualStable = $result | ConvertTo-Json -Depth 5 -Compress
    $expectedStable = $expected | ConvertTo-Json -Depth 5 -Compress
    if ($actualStable -cne $expectedStable) { throw 'Coverage result differs from the first deterministic run.' }
}

Write-Output "CRAP gate: $($result.MethodCount) methods, $($result.Offenders.Count) above $Threshold."
foreach ($offender in $result.Offenders) {
    Write-Output ("FAIL CRAP={0} CC={1} COVERAGE={2}% METHOD={3} FILE={4}" -f `
        $offender.Crap, $offender.Complexity, $offender.Coverage, $offender.Method, $offender.File)
}
if ($result.Offenders.Count -gt 0) { exit 1 }
