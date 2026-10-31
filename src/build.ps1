# Armory Unbound build: C# 5 with the Windows .NET Framework csc against the game's DLLs.
# Usage: .\build.ps1 -Game 'C:\...\steamapps\common\7 Days To Die'   (needs 0_TFP_Harmony in that install's Mods folder)
param([string]$Game = 'E:\SteamLibrary\steamapps\common\7 Days To Die')
$ErrorActionPreference = 'Stop'
$game = $Game
$m    = Join-Path $game '7DaysToDie_Data\Managed'
$harm = Join-Path $game 'Mods\0_TFP_Harmony\0Harmony.dll'
$src  = Get-ChildItem -Path $PSScriptRoot -Filter *.cs | % FullName
$outDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'Mods\0-ArmoryUnbound'
$out  = Join-Path $outDir 'ArmoryUnbound.dll'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs = 'mscorlib','netstandard','System','System.Core','System.Xml','System.Xml.Linq','Assembly-CSharp','LogLibrary','UnityEngine','UnityEngine.CoreModule','UnityEngine.ImageConversionModule','MemoryPack' | % { '/r:' + (Join-Path $m ($_ + '.dll')) }
$refs += '/r:' + $harm
& $csc /nologo /noconfig /nostdlib+ /target:library /optimize+ /langversion:5 "/out:$out" @refs @src
"exit $LASTEXITCODE"
if (Test-Path $out) { (Get-Item $out).Length }
