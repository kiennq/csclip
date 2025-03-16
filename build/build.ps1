# strict
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = "Release",
    [ValidateSet('anycpu')]
    [string]$Platform = "anycpu",
    [string]$Project = "csclip",
    [switch]$Publish
)

# solution root is in the parent of this scrip
$solutionRoot = Split-Path -Parent $PsScriptRoot
Set-Location $solutionRoot
$binDir = "$solutionRoot\$Project\bin\$Configuration\"
Remove-Item -Recurse -Force $binDir -ErrorAction SilentlyContinue

# restore nuget
msbuild "$Project/$Project.csproj" -t:restore
# build using msbuild
msbuild "$Project/$Project.csproj" -p:Configuration=$Configuration -p:Platform=$Platform

# return if failed
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed"
    exit $LASTEXITCODE
}

# compress the output and publish
if ($Publish) {
    $publishDir = "$solutionRoot\publish"
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    $zipFile = Join-Path "$publishDir" "$Project.tar.xz"

    # remove all files that not .dll, .exe, .pdb or .config
    Get-ChildItem -Path $binDir -Recurse | Where-Object {
        # only delete file
        !$_.PSIsContainer -and $_.Extension -notin @('.dll', '.exe', '.pdb', '.config')
    } | Remove-Item -Force

    & tar -zcvf $zipFile -C $binDir .
    # compress the output
    # Compress-Archive -Path $binDir -DestinationPath $zipFile -Force
    Write-Host "Compressed to $zipFile"
}
