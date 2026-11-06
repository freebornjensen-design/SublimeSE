# Сборка SeHelper.exe компилятором Roslyn, который поставляется с самой игрой.
# .NET SDK не нужен: хватает .NET Framework 4.8 и установленной Space Engineers.
param(
    [string]$Bin64 = "C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64",
    [string]$Out = (Join-Path $PSScriptRoot "..\package\SpaceEngineers\bin")
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path "$Bin64\Microsoft.CodeAnalysis.CSharp.dll")) { throw "Не нашёл Roslyn в $Bin64. Укажи -Bin64 <папка игры>\Bin64" }

[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($s, $e)
    $p = Join-Path $Bin64 (($e.Name -split ',')[0] + '.dll')
    if (Test-Path $p) { return [Reflection.Assembly]::LoadFrom($p) }
    return $null
})
[void][Reflection.Assembly]::LoadFrom("$Bin64\Microsoft.CodeAnalysis.dll")
[void][Reflection.Assembly]::LoadFrom("$Bin64\Microsoft.CodeAnalysis.CSharp.dll")

$fw = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
$refs = @(
    "$fw\mscorlib.dll", "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Xml.dll", "$fw\System.Xml.Linq.dll",
    "$fw\System.Web.Extensions.dll", "$fw\netstandard.dll", "$fw\System.Runtime.dll", "$fw\System.Drawing.dll",
    "$Bin64\Microsoft.CodeAnalysis.dll", "$Bin64\Microsoft.CodeAnalysis.CSharp.dll", "$Bin64\System.Collections.Immutable.dll",
    # API игры: моки реализуют её интерфейсы напрямую
    "$Bin64\Sandbox.Common.dll", "$Bin64\SpaceEngineers.Game.dll", "$Bin64\SpaceEngineers.ObjectBuilders.dll",
    "$Bin64\VRage.dll", "$Bin64\VRage.Game.dll", "$Bin64\VRage.Library.dll", "$Bin64\VRage.Math.dll"
)
# Roslyn собран под netstandard, поэтому нужны фасады из GAC
$gac = Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_MSIL"
foreach ($n in 'System.Text.Encoding','System.Threading.Tasks','System.Globalization','System.IO','System.Security.Cryptography.Primitives',
               'System.Reflection','System.Threading','System.Collections','System.Runtime.Extensions','System.Diagnostics.Debug') {
    $f = Get-ChildItem (Join-Path $gac $n) -Recurse -Filter "$n.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($f) { $refs += $f.FullName }
}
$refs = $refs | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) }

$parseOpts = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp7_3)
$trees = Get-ChildItem (Join-Path $PSScriptRoot "src\*.cs") | ForEach-Object {
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($_.FullName), $parseOpts, $_.FullName, [Text.Encoding]::UTF8)
}
$opts = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$opts = $opts.WithPlatform([Microsoft.CodeAnalysis.Platform]::X64).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create("SeHelper", [Microsoft.CodeAnalysis.SyntaxTree[]]$trees, [Microsoft.CodeAnalysis.MetadataReference[]]$refs, $opts)

New-Item -ItemType Directory -Force $Out | Out-Null
$exe = Join-Path $Out "SeHelper.exe"
$fs = [IO.File]::Create($exe)
try { $res = $comp.Emit($fs) } finally { $fs.Dispose() }
$errors = $res.Diagnostics | Where-Object { $_.Severity -eq 'Error' }
$res.Diagnostics | Where-Object { $_.Severity -ge 'Warning' } | ForEach-Object { $_.ToString() }
if (-not $res.Success) { Remove-Item $exe -ErrorAction SilentlyContinue; throw "Сборка не удалась: $(@($errors).Count) ошибок" }

# Конфиг: разрешаем грузить DLL игры и включаем переносимые PDB для номеров строк в стектрейсах скриптов.
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup>
  <runtime>
    <loadFromRemoteSources enabled="true" />
    <AppContextSwitchOverrides value="Switch.System.Diagnostics.IgnorePortablePDBsInStackTraces=false" />
  </runtime>
</configuration>
"@ | Set-Content -Encoding UTF8 "$exe.config"
"OK: $exe"
