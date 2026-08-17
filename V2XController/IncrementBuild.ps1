$ErrorActionPreference = "Stop"

$buildFile = Join-Path $PSScriptRoot "build.txt"
$versionFile = Join-Path $PSScriptRoot "BuildVersion.cs"
$lockFile = Join-Path $PSScriptRoot ".build-version-lock"

# ---------------------------------------------------------
# OCHRANA PROTI VICENASOBNEMU SPUSTENI PRI JEDNOM WPF BUILDU
# ---------------------------------------------------------

if (Test-Path $lockFile)
{
    $lastRun = [DateTime]::Parse((Get-Content $lockFile -Raw).Trim())
    $elapsed = (Get-Date) - $lastRun

    # WPF muze target pustit vicekrat behem jednoho buildu.
    # Pokud uz probehl pred mene nez 10 sekundami, preskoc ho.
    if ($elapsed.TotalSeconds -lt 10)
    {
        Write-Host "Build version already incremented - skipping."
        exit 0
    }
}

Set-Content $lockFile (Get-Date).ToString("O")


# ---------------------------------------------------------
# VERZE
# ---------------------------------------------------------

if (!(Test-Path $buildFile))
{
    Set-Content $buildFile "3.2.1.0"
}

$version = (Get-Content $buildFile -Raw).Trim()

$parts = $version.Split('.')

if ($parts.Count -ne 4)
{
    throw "Invalid version '$version'. Expected format: 3.2.1.41"
}

$major = [int]$parts[0]
$minor = [int]$parts[1]
$patch = [int]$parts[2]
$build = [int]$parts[3]


# ---------------------------------------------------------
# INKREMENTACE
# ---------------------------------------------------------

$build++

# 3.2.1.99 -> 3.2.2.0
if ($build -ge 100)
{
    $build = 0
    $patch++
}

# 3.2.99.99 -> 3.3.0.0
if ($patch -ge 100)
{
    $patch = 0
    $minor++
}

$newVersion = "$major.$minor.$patch.$build"

Set-Content $buildFile $newVersion


# ---------------------------------------------------------
# ASSEMBLY VERSION
# ---------------------------------------------------------

@"
using System.Reflection;

[assembly: AssemblyFileVersion("$newVersion")]
[assembly: AssemblyInformationalVersion("$newVersion")]
"@ | Set-Content $versionFile


Write-Host "====================================="
Write-Host " BUILD VERSION: $newVersion"
Write-Host "====================================="