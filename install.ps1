# Установка пакета Space Engineers в Sublime Text 4:
# 1) собирает SeHelper.exe компилятором из папки игры;
# 2) копирует .\package\SpaceEngineers в Packages каждого профиля Sublime:
#    %APPDATA%\Sublime Text и %APPDATA%\Sublime Text 3 (ST4, обновлённый с ST3, живёт во втором)
#    (после правок в репозитории запусти ещё раз).
param(
    [string]$Bin64 = "C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64"
)
$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "package\SpaceEngineers"

Get-Process SeHelper -ErrorAction SilentlyContinue | Stop-Process -Force
& (Join-Path $PSScriptRoot "helper\build.ps1") -Bin64 $Bin64

# Какой профиль возьмёт ST4, зависит от истории установки, поэтому ставим во все существующие.
$profiles = @("Sublime Text", "Sublime Text 3") | ForEach-Object { Join-Path $env:APPDATA $_ } | Where-Object { Test-Path $_ }
if (-not $profiles) { $profiles = @(Join-Path $env:APPDATA "Sublime Text") }

foreach ($profile in $profiles) {
    $packages = Join-Path $profile "Packages"
    New-Item -ItemType Directory -Force $packages | Out-Null
    $dst = Join-Path $packages "SpaceEngineers"
    if (Test-Path $dst) {
        $item = Get-Item $dst -Force
        if ($item.LinkType) {
            # старые версии установщика делали junction, но Sublime такие пакеты не загружает
            cmd /c rmdir "$dst" | Out-Null
        } elseif (-not (Test-Path (Join-Path $dst "se_plugin.py"))) {
            throw "$dst существует, но это не наш пакет. Переименуй её сам, установщик её не трогает."
        }
    }
    # Копия, а не ссылка: Sublime пропускает пакеты-junction. Повторный запуск обновит пакет.
    robocopy $src $dst /MIR /NFL /NDL /NJH /NJS /XD __pycache__ | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy завершился с кодом $LASTEXITCODE" }
    Write-Host "Пакет скопирован в $dst"
}
Write-Host "Готово. Перезапусти Sublime Text 4 и открой .cs-скрипт. Симулятор: Ctrl+Alt+S."
