<# Builds the transcription payload as part of a Release build. End users run no setup. #>
[CmdletBinding()]
param([string] $BundleDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# MSBuild can inherit PowerShell 7's module path before launching Windows PowerShell.
# Resolve the built-in modules from this interpreter, not the inherited search path.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1')
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Management\Microsoft.PowerShell.Management.psd1')
$repository = Split-Path -Parent $PSScriptRoot
if (-not $BundleDirectory) { $BundleDirectory = Join-Path $repository 'artifacts\lyrics-bundle' }
$BundleDirectory = [IO.Path]::GetFullPath($BundleDirectory)
$requirements = Join-Path $repository 'src\WaveLab\Transcription\requirements.txt'
$fingerprint = (Get-FileHash -LiteralPath $requirements -Algorithm SHA256).Hash
$buildFingerprint = $fingerprint + ':' + (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash + ':' +
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'prepare_bundle.py') -Algorithm SHA256).Hash
$manifest = Join-Path $BundleDirectory 'bundle.json'
if (Test-Path -LiteralPath $manifest) {
    try {
        $state = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        $complete = $state.schema_version -eq 1 -and $state.requirements_sha256 -eq $fingerprint -and $state.build_fingerprint -eq $buildFingerprint
        foreach ($file in $state.required_files.PSObject.Properties) {
            $path = [IO.Path]::GetFullPath((Join-Path $BundleDirectory $file.Name))
            if (-not $path.StartsWith($BundleDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { $complete = $false; break }
            if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -ne $file.Value) { $complete = $false; break }
        }
        if ($complete) { Write-Host 'Bundled lyrics engine is current.'; exit 0 }
    } catch { Write-Host 'Rebuilding the incomplete lyrics bundle.' }
}

$toolsDirectory = Join-Path $repository 'artifacts\lyrics-build-tools'
New-Item -ItemType Directory -Path $toolsDirectory,$BundleDirectory -Force | Out-Null
if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }
$uv = Join-Path $toolsDirectory 'uv-0.12.3.exe'
if (-not (Test-Path -LiteralPath $uv)) {
    $archive = Join-Path $toolsDirectory 'uv.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest 'https://github.com/astral-sh/uv/releases/download/0.12.3/uv-x86_64-pc-windows-msvc.zip' -OutFile $archive -UseBasicParsing
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'B23350C79E8AD0192B8124AF13A0F17E8D4E4549524785E1AEF389AE5A06990E') { throw 'Installer integrity check failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $zip.Entries | Where-Object Name -eq 'uv.exe'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $uv, $true)
    } finally { $zip.Dispose() }
    Remove-Item -LiteralPath $archive -Force
}
if (-not $env:UV_CACHE_DIR) { $env:UV_CACHE_DIR = Join-Path $toolsDirectory 'cache' }
if (-not $env:UV_PYTHON_INSTALL_DIR) { $env:UV_PYTHON_INSTALL_DIR = Join-Path $toolsDirectory 'python' }
if (-not $env:HF_HOME) { $env:HF_HOME = Join-Path $toolsDirectory 'model-cache' }
$env:HF_HUB_DISABLE_TELEMETRY = '1'
$env:UV_NO_PROGRESS = '1'
& $uv python install 3.11.15
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the bundled Python runtime.' }
$sourcePython = (& $uv python find --managed-python 3.11.15).Trim()
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $sourcePython)) { throw 'The bundled Python runtime was not found.' }
$pythonDirectory = Join-Path $BundleDirectory 'python'
New-Item -ItemType Directory -Path $pythonDirectory -Force | Out-Null
# A full standalone Python distribution is relocatable. Do not ship a virtualenv:
# its pyvenv.cfg would point at the build machine's interpreter.
& robocopy (Split-Path -Parent $sourcePython) $pythonDirectory /E /NFL /NDL /NJH /NJS /NP /XD __pycache__ | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Could not copy the bundled interpreter.' }
$python = Join-Path $pythonDirectory 'python.exe'
$sitePackages = Join-Path $pythonDirectory 'Lib\site-packages'
& $uv pip install --python $python --target $sitePackages --index-url https://download.pytorch.org/whl/cu128 torch==2.8.0
if ($LASTEXITCODE -ne 0) { throw 'Could not bundle the CPU/GPU inference runtime.' }
& $uv pip install --python $python --target $sitePackages --requirement $requirements
if ($LASTEXITCODE -ne 0) { throw 'Could not bundle the transcription dependencies.' }
# Static import libraries/headers are for compiling extensions, not inference.
# Resolve each removal inside this bundle before deleting it.
Get-ChildItem -LiteralPath (Join-Path $sitePackages 'torch') -Recurse -File -Filter '*.lib' | ForEach-Object {
    if (-not $_.FullName.StartsWith($BundleDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid library cleanup path.' }
    Remove-Item -LiteralPath $_.FullName -Force
}
& $python -I -B -X utf8 (Join-Path $repository 'installer\prepare_bundle.py') --bundle $BundleDirectory --requirements $requirements --build-fingerprint $buildFingerprint
if ($LASTEXITCODE -ne 0) { throw 'Could not bundle the transcription models.' }
$env:HF_HOME = Join-Path $BundleDirectory 'models'
$env:HF_HUB_OFFLINE = '1'
& $python -I -B -X utf8 (Join-Path $repository 'src\WaveLab\Transcription\lyrics_worker.py') --check
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $manifest -Force
    throw 'The bundled engine failed its startup check.'
}
