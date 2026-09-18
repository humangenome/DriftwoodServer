@echo off
setlocal EnableExtensions DisableDelayedExpansion
title How to Fish - Driftwood Installer

set "URL_REPO=https://github.com/humangenome"
set "URL_SERVER_REPO=https://github.com/HumanGenome/DriftwoodServer.git"
set "GITHUB_API=https://api.github.com/repos/HumanGenome"

:: ---------- ADMIN ----------

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo Requesting Administrator privileges...
    set "INSTALLER_PATH=%~f0"
    powershell -NoProfile -Command "Start-Process -FilePath $env:INSTALLER_PATH -Verb RunAs"
    exit /b
)

:: ---------- REQUIREMENTS ----------

echo.
echo Checking requirements...

where git >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: Git was not found.
    echo Install Git from:
    echo https://git-scm.com/install/
    pause
    exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: .NET was not found.
    echo Install the .NET 8 SDK from:
    echo https://dotnet.microsoft.com/en-us/download/dotnet/8.0
    pause
    exit /b 1
)

dotnet --list-sdks | findstr /r /b "8\." >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: .NET 8 SDK was not found.
    echo Install the .NET 8 SDK from:
    echo https://dotnet.microsoft.com/en-us/download/dotnet/8.0
    pause
    exit /b 1
)

echo Git: OK
echo .NET 8 SDK: OK

:: ---------- VERSIONS ----------

echo.
echo Checking GitHub for the latest Driftwood releases...

set "LATEST_SERVER_VERSION="
for /f "usebackq delims=" %%A in (`powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $h=@{'User-Agent'='DriftwoodInstaller'}; $r=Invoke-RestMethod 'https://api.github.com/repos/HumanGenome/DriftwoodServer/releases/latest' -Headers $h; ($r.tag_name -replace '^v','')" 2^>nul`) do set "LATEST_SERVER_VERSION=%%A"

if not defined LATEST_SERVER_VERSION (
    echo WARNING: Could not determine the latest DriftwoodServer release from GitHub.
    set "LATEST_SERVER_VERSION=0.1.7"
    echo Using fallback server version: v0.1.7
)

set "LATEST_CLIENT_VERSION="
for /f "usebackq delims=" %%A in (`powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $h=@{'User-Agent'='DriftwoodInstaller'}; $r=Invoke-RestMethod 'https://api.github.com/repos/HumanGenome/Driftwood/releases/latest' -Headers $h; ($r.tag_name -replace '^v','')" 2^>nul`) do set "LATEST_CLIENT_VERSION=%%A"

if not defined LATEST_CLIENT_VERSION (
    echo WARNING: Could not determine the latest Driftwood client release from GitHub.
    set "LATEST_CLIENT_VERSION=0.1.3"
    echo Using fallback client version: v0.1.3
)

echo.
echo Latest DriftwoodServer release: v%LATEST_SERVER_VERSION%
echo Latest Driftwood client release: v%LATEST_CLIENT_VERSION%
echo.

:server_version_prompt
set "SERVER_VERSION=%LATEST_SERVER_VERSION%"
set "USER_INPUT="
set /p "USER_INPUT=Server version [%LATEST_SERVER_VERSION%]: "
if defined USER_INPUT set "SERVER_VERSION=%USER_INPUT%"
if /I "%SERVER_VERSION:~0,1%"=="v" set "SERVER_VERSION=%SERVER_VERSION:~1%"

powershell -NoProfile -Command "if($env:SERVER_VERSION -match '^[0-9A-Za-z][0-9A-Za-z._-]*$'){exit 0}else{exit 1}"
if errorlevel 1 (
    echo ERROR: Invalid server version.
    goto server_version_prompt
)

:client_version_prompt
set "CLIENT_VERSION=%LATEST_CLIENT_VERSION%"
set "USER_INPUT="
set /p "USER_INPUT=Client version [%LATEST_CLIENT_VERSION%]: "
if defined USER_INPUT set "CLIENT_VERSION=%USER_INPUT%"
if /I "%CLIENT_VERSION:~0,1%"=="v" set "CLIENT_VERSION=%CLIENT_VERSION:~1%"

powershell -NoProfile -Command "if($env:CLIENT_VERSION -match '^[0-9A-Za-z][0-9A-Za-z._-]*$'){exit 0}else{exit 1}"
if errorlevel 1 (
    echo ERROR: Invalid client version.
    goto client_version_prompt
)

set "EXPECTED_SERVER_TAG=v%SERVER_VERSION%"
set "EXPECTED_SERVER_COMMIT="
for /f "usebackq delims=" %%A in (`powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $h=@{'User-Agent'='DriftwoodInstaller'}; $ref=Invoke-RestMethod ('https://api.github.com/repos/HumanGenome/DriftwoodServer/git/ref/tags/v'+$env:SERVER_VERSION) -Headers $h; $obj=$ref.object; while($obj.type -eq 'tag'){ $tag=Invoke-RestMethod $obj.url -Headers $h; $obj=$tag.object }; if($obj.type -ne 'commit'){throw 'Selected tag does not resolve to a commit.'}; $obj.sha" 2^>nul`) do set "EXPECTED_SERVER_COMMIT=%%A"

if not defined EXPECTED_SERVER_COMMIT (
    echo.
    echo ERROR: Server release/tag v%SERVER_VERSION% could not be resolved on GitHub.
    echo Check the version and try again.
    goto server_version_prompt
)

set "URL_SERVER="
set "SERVER_EXPECTED_SHA256="
for /f "usebackq tokens=1,2 delims=|" %%A in (`powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $h=@{'User-Agent'='DriftwoodInstaller'}; $r=Invoke-RestMethod ('https://api.github.com/repos/HumanGenome/DriftwoodServer/releases/tags/v'+$env:SERVER_VERSION) -Headers $h; $name='DriftwoodServer-'+$env:SERVER_VERSION+'.zip'; $a=$r.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1; if(-not $a){throw ('Release asset not found: '+$name)}; $digest=[string]$a.digest; if($digest -like 'sha256:*'){$digest=$digest.Substring(7)}else{$digest=''}; Write-Output ($a.browser_download_url+'|'+$digest)" 2^>nul`) do (
    set "URL_SERVER=%%A"
    set "SERVER_EXPECTED_SHA256=%%B"
)

if not defined URL_SERVER (
    echo.
    echo ERROR: DriftwoodServer v%SERVER_VERSION% does not have the expected release ZIP.
    echo Expected asset: DriftwoodServer-%SERVER_VERSION%.zip
    goto server_version_prompt
)

set "URL_CLIENT="
set "CLIENT_EXPECTED_SHA256="
for /f "usebackq tokens=1,2 delims=|" %%A in (`powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $h=@{'User-Agent'='DriftwoodInstaller'}; $r=Invoke-RestMethod ('https://api.github.com/repos/HumanGenome/Driftwood/releases/tags/v'+$env:CLIENT_VERSION) -Headers $h; $name='Driftwood-Portable-v'+$env:CLIENT_VERSION+'.zip'; $a=$r.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1; if(-not $a){throw ('Release asset not found: '+$name)}; $digest=[string]$a.digest; if($digest -like 'sha256:*'){$digest=$digest.Substring(7)}else{$digest=''}; Write-Output ($a.browser_download_url+'|'+$digest)" 2^>nul`) do (
    set "URL_CLIENT=%%A"
    set "CLIENT_EXPECTED_SHA256=%%B"
)

if not defined URL_CLIENT (
    echo.
    echo ERROR: Driftwood client v%CLIENT_VERSION% does not have the expected portable ZIP.
    echo Expected asset: Driftwood-Portable-v%CLIENT_VERSION%.zip
    goto client_version_prompt
)

echo.
echo Selected server: v%SERVER_VERSION%
echo Server tag commit: %EXPECTED_SERVER_COMMIT%
echo Selected client: v%CLIENT_VERSION%

:: ---------- SETTINGS ----------

echo.
echo Configure the server.
echo Press Enter to accept a value shown in brackets.
echo.

set "SERVER_NAME=The Island"
set "USER_INPUT="
set /p "USER_INPUT=Server name [The Island]: "
if defined USER_INPUT set "SERVER_NAME=%USER_INPUT%"

:server_id_prompt
set "SERVER_ID=the-island"
set "USER_INPUT="
set /p "USER_INPUT=Server ID [the-island]: "
if defined USER_INPUT set "SERVER_ID=%USER_INPUT%"

powershell -NoProfile -Command "if($env:SERVER_ID -match '^[A-Za-z0-9_-]{1,80}$'){exit 0}else{exit 1}"
if errorlevel 1 (
    echo ERROR: Server ID may contain only letters, numbers, dashes, and underscores.
    goto server_id_prompt
)

:game_port_prompt
set "GAME_PORT=22003"
set "USER_INPUT="
set /p "USER_INPUT=Game port [22003]: "
if defined USER_INPUT set "GAME_PORT=%USER_INPUT%"

powershell -NoProfile -Command "$p=0; if([int]::TryParse($env:GAME_PORT,[ref]$p) -and $p -ge 1 -and $p -le 65534){exit 0}else{exit 1}"
if errorlevel 1 (
    echo ERROR: Game port must be a number from 1 through 65534.
    goto game_port_prompt
)

:: ---------- SELECT INSTALL FOLDER ----------

:select_install_folder
echo.
echo Select the folder where Driftwood should be installed.
echo The client and server folders will be created inside it.
echo.

set "D="
set "INSTALL_PICK_FILE=%TEMP%\driftwood-install-%RANDOM%-%RANDOM%.txt"
del "%INSTALL_PICK_FILE%" >nul 2>&1

powershell -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; $d=New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description='Select the folder where Driftwood should be installed'; $d.ShowNewFolderButton=$true; if($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK){Set-Content -LiteralPath $env:INSTALL_PICK_FILE -Value $d.SelectedPath -NoNewline}"

if not exist "%INSTALL_PICK_FILE%" (
    echo ERROR: No install folder was selected.
    pause
    exit /b 1
)

set /p "D="<"%INSTALL_PICK_FILE%"
del "%INSTALL_PICK_FILE%" >nul 2>&1

if not defined D (
    echo ERROR: The selected install folder is invalid.
    pause
    goto select_install_folder
)

echo Install root: "%D%"

:: ---------- SELECT STEAMAPPS ----------

:select_steamapps
echo.
echo Select the Steam "steamapps" folder containing How to Fish.
echo Example: E:\SteamLibrary\steamapps
echo.

set "D_STEAMAPPS="
set "PICK_FILE=%TEMP%\driftwood-steamapps-%RANDOM%-%RANDOM%.txt"
del "%PICK_FILE%" >nul 2>&1

powershell -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; $d=New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description='Select the Steam steamapps folder containing How to Fish'; $d.ShowNewFolderButton=$false; if($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK){Set-Content -LiteralPath $env:PICK_FILE -Value $d.SelectedPath -NoNewline}"

if not exist "%PICK_FILE%" (
    echo ERROR: No Steam folder was selected.
    pause
    exit /b 1
)

set /p "D_STEAMAPPS="<"%PICK_FILE%"
del "%PICK_FILE%" >nul 2>&1

if not exist "%D_STEAMAPPS%\appmanifest_4001890.acf" (
    echo.
    echo ERROR: appmanifest_4001890.acf was not found in:
    echo "%D_STEAMAPPS%"
    echo.
    echo Select the Steam "steamapps" folder itself.
    pause
    goto select_steamapps
)

set "D_GAME=%D_STEAMAPPS%\common\How to Fish"

if exist "%D_GAME%\How to Fish.exe" goto game_found
if exist "%D_GAME%\How to Fish\How to Fish.exe" set "D_GAME=%D_GAME%\How to Fish"
if exist "%D_GAME%\How to Fish.exe" goto game_found

echo.
echo ERROR: How to Fish.exe was not found under:
echo "%D_STEAMAPPS%\common\How to Fish"
echo.
pause
goto select_steamapps

:game_found

:: ---------- CONSTANTS ----------

set "D_CLIENT=%D%\Client"
set "D_SERVER=%D%\%SERVER_ID%"
set "D_SERVER_GAME=%D_SERVER%\How to Fish"
set "D_SERVER_STEAMAPPS=%D_SERVER%\steamapps"
set "D_TMP=%D%\.tmp"
set "D_REPO=%D_TMP%\repo"
set "D_CONFIG=%D_SERVER%\appsettings.json"
set "D_SUPERVISOR=%D_SERVER%\bin\DriftwoodServer.exe"
set "D_SUPERVISOR_DLL=%D_SERVER%\bin\DriftwoodServer.dll"
set "SOURCE_VERIFY_FILE=%D_SERVER%\source-verification.txt"
set "BUILD_HASH_FILE=%D_SERVER%\build-hashes.txt"
set "CLIENT_ZIP=%D_TMP%\Driftwood-Portable-v%CLIENT_VERSION%.zip"
set "SERVER_ZIP=%D_TMP%\DriftwoodServer-%SERVER_VERSION%.zip"
set "VERIFY_FILE=%D_TMP%\verify-build.txt"
set "TOKEN_FILE=%D_TMP%\auth-token.txt"

:: ---------- SAFETY ----------

if exist "%D_SERVER%\appsettings.json" (
    echo.
    echo ERROR: This server already appears to be installed:
    echo "%D_SERVER%"
    echo.
    echo Choose a different Server ID or remove the existing installation manually.
    pause
    exit /b 1
)

:: ---------- PRELIM ----------

echo.
echo Creating directories...

if not exist "%D%" mkdir "%D%"
if exist "%D_TMP%" rmdir /s /q "%D_TMP%"

mkdir "%D_CLIENT%" 2>nul
mkdir "%D_SERVER%" 2>nul
mkdir "%D_SERVER_GAME%" 2>nul
mkdir "%D_SERVER_STEAMAPPS%" 2>nul
mkdir "%D_TMP%" 2>nul
mkdir "%D_SERVER%\Saves" 2>nul
mkdir "%D_SERVER%\host-state" 2>nul
mkdir "%D_SERVER%\Backups" 2>nul
mkdir "%D_SERVER%\bin" 2>nul

if not exist "%D_SERVER_GAME%" goto directory_error
if not exist "%D_SERVER_STEAMAPPS%" goto directory_error
if not exist "%D_SERVER%\Saves" goto directory_error
if not exist "%D_SERVER%\host-state" goto directory_error
if not exist "%D_SERVER%\Backups" goto directory_error
if not exist "%D_SERVER%\bin" goto directory_error
goto directories_ok

:directory_error
echo.
echo ERROR: One or more install directories could not be created.
pause
exit /b 1

:directories_ok

powershell -NoProfile -Command "$token=[guid]::NewGuid().ToString('N')+[guid]::NewGuid().ToString('N'); Set-Content -LiteralPath $env:TOKEN_FILE -Value $token -NoNewline"
if errorlevel 1 goto install_error
set /p "SERVER_AUTH_TOKEN="<"%TOKEN_FILE%"
del "%TOKEN_FILE%" >nul 2>&1

:: ---------- CLIENT ----------

echo.
echo Downloading Driftwood client v%CLIENT_VERSION%...

powershell -NoProfile -Command "Invoke-WebRequest -Uri $env:URL_CLIENT -OutFile $env:CLIENT_ZIP"
if errorlevel 1 goto install_error

if defined CLIENT_EXPECTED_SHA256 (
    echo Verifying Driftwood client ZIP SHA-256...
    powershell -NoProfile -Command "$actual=(Get-FileHash -LiteralPath $env:CLIENT_ZIP -Algorithm SHA256).Hash; if($actual -ine $env:CLIENT_EXPECTED_SHA256){Write-Error ('Client ZIP SHA-256 mismatch. Expected '+$env:CLIENT_EXPECTED_SHA256+' but got '+$actual); exit 1}; Write-Host ('Client ZIP SHA-256 OK: '+$actual)"
    if errorlevel 1 goto download_verification_error
) else (
    echo WARNING: GitHub did not provide a SHA-256 digest for this client release asset.
)

echo Extracting Driftwood client...
powershell -NoProfile -Command "Expand-Archive -LiteralPath $env:CLIENT_ZIP -DestinationPath $env:D_CLIENT -Force"
if errorlevel 1 goto install_error

if not exist "%D_CLIENT%\Driftwood.Launcher.exe" (
    echo.
    echo ERROR: Driftwood.Launcher.exe was not found after extraction.
    goto install_error
)

del "%CLIENT_ZIP%" >nul 2>&1

:: ---------- SERVER GAME ----------

echo.
echo Copying How to Fish into the server directory...
robocopy "%D_GAME%" "%D_SERVER_GAME%" /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /NFL /NDL /NP
if errorlevel 8 (
    echo.
    echo ERROR: Failed to copy the How to Fish game files.
    goto install_error
)

if not exist "%D_SERVER_GAME%\How to Fish.exe" (
    echo.
    echo ERROR: How to Fish.exe was not found in the copied server files.
    goto install_error
)

echo Copying Steam app manifest...
copy /Y "%D_STEAMAPPS%\appmanifest_4001890.acf" "%D_SERVER_STEAMAPPS%\appmanifest_4001890.acf" >nul
if errorlevel 1 goto install_error

:: ---------- SERVER OVERLAY ----------

echo.
echo Downloading DriftwoodServer v%SERVER_VERSION%...

powershell -NoProfile -Command "Invoke-WebRequest -Uri $env:URL_SERVER -OutFile $env:SERVER_ZIP"
if errorlevel 1 goto install_error

if defined SERVER_EXPECTED_SHA256 (
    echo Verifying DriftwoodServer ZIP SHA-256...
    powershell -NoProfile -Command "$actual=(Get-FileHash -LiteralPath $env:SERVER_ZIP -Algorithm SHA256).Hash; if($actual -ine $env:SERVER_EXPECTED_SHA256){Write-Error ('Server ZIP SHA-256 mismatch. Expected '+$env:SERVER_EXPECTED_SHA256+' but got '+$actual); exit 1}; Write-Host ('Server ZIP SHA-256 OK: '+$actual)"
    if errorlevel 1 goto download_verification_error
) else (
    echo WARNING: GitHub did not provide a SHA-256 digest for this server release asset.
)

echo Extracting DriftwoodServer overlay...
powershell -NoProfile -Command "Expand-Archive -LiteralPath $env:SERVER_ZIP -DestinationPath $env:D_TMP -Force"
if errorlevel 1 goto install_error

del "%SERVER_ZIP%" >nul 2>&1

set "D_OVERLAY=%D_TMP%\DriftwoodServer\bepinex"
if exist "%D_OVERLAY%\client\winhttp.dll" set "D_OVERLAY=%D_OVERLAY%\client"

if not exist "%D_OVERLAY%\winhttp.dll" (
    echo.
    echo ERROR: winhttp.dll was not found in the server overlay.
    goto install_error
)

if not exist "%D_OVERLAY%\doorstop_config.ini" (
    echo.
    echo ERROR: doorstop_config.ini was not found in the server overlay.
    goto install_error
)

if not exist "%D_OVERLAY%\BepInEx\plugins\DriftwoodHost.dll" (
    echo.
    echo ERROR: DriftwoodHost.dll was not found in the server overlay.
    goto install_error
)

echo Installing server overlay...
powershell -NoProfile -Command "Copy-Item -LiteralPath (Join-Path $env:D_OVERLAY 'winhttp.dll') -Destination $env:D_SERVER_GAME -Force; Copy-Item -LiteralPath (Join-Path $env:D_OVERLAY 'doorstop_config.ini') -Destination $env:D_SERVER_GAME -Force; Copy-Item -LiteralPath (Join-Path $env:D_OVERLAY 'BepInEx') -Destination $env:D_SERVER_GAME -Recurse -Force"
if errorlevel 1 goto install_error

:: ---------- SUPERVISOR SOURCE ----------

echo.
echo Downloading DriftwoodServer source...
git clone --branch "v%SERVER_VERSION%" --single-branch "%URL_SERVER_REPO%" "%D_REPO%"
if errorlevel 1 goto install_error

if not exist "%D_REPO%\src\DriftwoodServer\DriftwoodServer.csproj" (
    echo.
    echo ERROR: DriftwoodServer project was not found after cloning.
    goto install_error
)

echo.
echo Verifying DriftwoodServer source before building...

set "ACTUAL_REMOTE="
for /f "delims=" %%A in ('git -C "%D_REPO%" remote get-url origin') do set "ACTUAL_REMOTE=%%A"

set "ACTUAL_TAG="
for /f "delims=" %%A in ('git -C "%D_REPO%" describe --tags --exact-match 2^>nul') do set "ACTUAL_TAG=%%A"

set "ACTUAL_COMMIT="
for /f "delims=" %%A in ('git -C "%D_REPO%" rev-parse HEAD') do set "ACTUAL_COMMIT=%%A"

set "TAG_COMMIT="
for /f "delims=" %%A in ('git -C "%D_REPO%" rev-list -n 1 "%EXPECTED_SERVER_TAG%"') do set "TAG_COMMIT=%%A"

set "SOURCE_DIRTY="
for /f "delims=" %%A in ('git -C "%D_REPO%" status --porcelain') do set "SOURCE_DIRTY=1"

if /I not "%ACTUAL_REMOTE%"=="%URL_SERVER_REPO%" (
    echo.
    echo ERROR: DriftwoodServer remote does not match the expected repository.
    echo Expected: %URL_SERVER_REPO%
    echo Actual:   %ACTUAL_REMOTE%
    goto source_verification_error
)

if /I not "%ACTUAL_TAG%"=="%EXPECTED_SERVER_TAG%" (
    echo.
    echo ERROR: DriftwoodServer checkout is not on the expected tag.
    echo Expected: %EXPECTED_SERVER_TAG%
    echo Actual:   %ACTUAL_TAG%
    goto source_verification_error
)

if /I not "%ACTUAL_COMMIT%"=="%EXPECTED_SERVER_COMMIT%" (
    echo.
    echo ERROR: DriftwoodServer HEAD does not match the expected commit.
    echo Expected: %EXPECTED_SERVER_COMMIT%
    echo Actual:   %ACTUAL_COMMIT%
    goto source_verification_error
)

if /I not "%TAG_COMMIT%"=="%EXPECTED_SERVER_COMMIT%" (
    echo.
    echo ERROR: The expected tag resolves to an unexpected commit.
    echo Tag:      %EXPECTED_SERVER_TAG%
    echo Expected: %EXPECTED_SERVER_COMMIT%
    echo Actual:   %TAG_COMMIT%
    goto source_verification_error
)

if defined SOURCE_DIRTY (
    echo.
    echo ERROR: DriftwoodServer source has local modifications.
    git -C "%D_REPO%" status --short
    goto source_verification_error
)

(
    echo DriftwoodServer source verification
    echo.
    echo Selected server release: v%SERVER_VERSION%
    echo Selected client release: v%CLIENT_VERSION%
    echo Remote: %ACTUAL_REMOTE%
    echo Expected tag: %EXPECTED_SERVER_TAG%
    echo Expected commit from GitHub: %EXPECTED_SERVER_COMMIT%
    echo Actual tag: %ACTUAL_TAG%
    echo Actual commit: %ACTUAL_COMMIT%
    echo Local tag resolves to: %TAG_COMMIT%
    echo Working tree: CLEAN
) > "%SOURCE_VERIFY_FILE%"

echo Remote:       %ACTUAL_REMOTE%
echo Tag:          %ACTUAL_TAG%
echo Commit:       %ACTUAL_COMMIT%
echo Working tree: CLEAN
echo Source verification: OK
echo Saved to: "%SOURCE_VERIFY_FILE%"

:: ---------- SUPERVISOR BUILD ----------

echo.
echo Building supervisor...
dotnet publish "%D_REPO%\src\DriftwoodServer\DriftwoodServer.csproj" -c Release -o "%D_SERVER%\bin"
if errorlevel 1 goto install_error

if not exist "%D_SUPERVISOR_DLL%" (
    echo.
    echo ERROR: DriftwoodServer.dll was not present after the build.
    echo If your antivirus displayed an alert, check its quarantine before continuing.
    goto build_artifact_error
)

if not exist "%D_SUPERVISOR%" (
    echo.
    echo ERROR: DriftwoodServer.exe was not present after the build.
    echo If your antivirus displayed an alert, check its quarantine before continuing.
    goto build_artifact_error
)

:: ---------- BUILD HASHES ----------

echo.
echo Calculating SHA-256 hashes of the generated server files...

powershell -NoProfile -Command "$files=@($env:D_SUPERVISOR_DLL,$env:D_SUPERVISOR); $lines=@('DriftwoodServer build hashes',''); foreach($file in $files){ if(-not (Test-Path -LiteralPath $file -PathType Leaf)){ throw ('Build artifact is missing: ' + $file) }; $hash=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash; $line=$hash + '  ' + $file; Write-Host $line; $lines += $line }; Set-Content -LiteralPath $env:BUILD_HASH_FILE -Value $lines -Encoding ASCII"
if errorlevel 1 goto build_artifact_error

echo.
echo Hashes saved to:
echo "%BUILD_HASH_FILE%"
echo.
echo Source verification and file hashes are complete.
echo DriftwoodServer has NOT been executed yet.
echo.
choice /C YN /N /M "Run DriftwoodServer build verification now? [Y/N]: "
if errorlevel 2 goto execution_cancelled

:: ---------- CONFIG ----------

echo.
echo Creating appsettings.json...
copy /Y "%D_REPO%\appsettings.example.json" "%D_CONFIG%" >nul
if errorlevel 1 goto install_error

powershell -NoProfile -Command "$config=Get-Content -LiteralPath $env:D_CONFIG -Raw | ConvertFrom-Json; $config.instanceId=$env:SERVER_ID; $config.gameRoot=$env:D_SERVER_GAME; $config.instanceRoot=$env:D_SERVER; $config.stateRoot=(Join-Path $env:D_SERVER 'host-state'); $config.saveRoot=(Join-Path $env:D_SERVER 'Saves'); $config.backupRoot=(Join-Path $env:D_SERVER 'Backups'); $config.steamAppsDirectory=$env:D_SERVER_STEAMAPPS; $config.serverName=$env:SERVER_NAME; $config.authToken=$env:SERVER_AUTH_TOKEN; $config.gamePort=[int]$env:GAME_PORT; $config.httpPort=0; $config.pinnedBuild.assemblySha256='0'*64; $config.pinnedBuild.buildId=''; $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $env:D_CONFIG -Encoding UTF8"
if errorlevel 1 goto install_error

if not exist "%D_CONFIG%" goto install_error

:: ---------- BUILD PIN ----------

echo.
echo Reading installed game build...
echo BUILD_PIN_FAILED is expected during this first check.
echo.

"%D_SUPERVISOR%" --verify-build "%D_CONFIG%" > "%VERIFY_FILE%" 2>&1
type "%VERIFY_FILE%"

powershell -NoProfile -Command "$text=Get-Content -LiteralPath $env:VERIFY_FILE -Raw; $assembly=[regex]::Match($text,'(?m)^assemblySha256=([A-Fa-f0-9]{64})\s*$').Groups[1].Value; $steam=[regex]::Match($text,'(?m)^steamBuildId=([0-9]+)\s*$').Groups[1].Value; if($assembly.Length -ne 64){throw 'Could not read assemblySha256 from build verification.'}; if([string]::IsNullOrWhiteSpace($steam)){throw 'Could not read steamBuildId from build verification.'}; $config=Get-Content -LiteralPath $env:D_CONFIG -Raw | ConvertFrom-Json; $config.pinnedBuild.assemblySha256=$assembly; $config.pinnedBuild.buildId=$steam; $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $env:D_CONFIG -Encoding UTF8; Write-Host ('Pinned assemblySha256=' + $assembly); Write-Host ('Pinned steamBuildId=' + $steam)"
if errorlevel 1 goto install_error

echo.
echo Verifying pinned build...
"%D_SUPERVISOR%" --verify-build "%D_CONFIG%"
if errorlevel 1 (
    echo.
    echo ERROR: Final build verification failed.
    goto install_error
)

:: ---------- STARTER FILES ----------

echo.
echo Creating launcher files...

powershell -NoProfile -Command "$q=[char]34; $lines=@('@echo off','title How to Fish - ' + $env:SERVER_NAME,'cd /d ' + $q + $env:D_SERVER + $q,$q + $env:D_SUPERVISOR + $q + ' --config ' + $q + $env:D_CONFIG + $q,'pause'); Set-Content -LiteralPath (Join-Path $env:D_SERVER 'Start Server.bat') -Value $lines -Encoding ASCII"
if errorlevel 1 goto install_error

powershell -NoProfile -Command "$q=[char]34; $launcher=Join-Path $env:D_CLIENT 'Driftwood.Launcher.exe'; $lines=@('@echo off','start ' + $q + $q + ' ' + $q + $launcher + $q); Set-Content -LiteralPath (Join-Path $env:D 'Start Client.bat') -Value $lines -Encoding ASCII"
if errorlevel 1 goto install_error

:: ---------- CLEANUP ----------

echo.
echo Cleaning temporary files...
rmdir /s /q "%D_TMP%" >nul 2>&1

:: ---------- COMPLETE ----------

echo.
echo ============================================================
echo Installation complete.
echo ============================================================
echo.
powershell -NoProfile -Command "Write-Host ('Server name:    ' + $env:SERVER_NAME); Write-Host ('Server ID:      ' + $env:SERVER_ID); Write-Host ('Server version: v' + $env:SERVER_VERSION); Write-Host ('Client version: v' + $env:CLIENT_VERSION); Write-Host ('Game port:      ' + $env:GAME_PORT); Write-Host ('Install:        ' + $env:D_SERVER); Write-Host ('Config:         ' + $env:D_CONFIG)"
echo.
echo Start the server with:
echo "%D_SERVER%\Start Server.bat"
echo.
echo Start the Driftwood client with:
echo "%D%\Start Client.bat"
echo.
echo On this same PC, connect to:
echo 127.0.0.1:%GAME_PORT%
echo.
echo The generated admin token is stored in appsettings.json.
echo.
pause
exit /b 0

:download_verification_error
echo.
echo ============================================================
echo DOWNLOAD VERIFICATION FAILED
echo ============================================================
echo.
echo A downloaded release ZIP did not match the SHA-256 digest
echo reported by GitHub. The installer will not use that file.
echo.
echo Temporary files were left in place for inspection:
echo "%D_TMP%"
echo.
pause
exit /b 1

:source_verification_error
echo.
echo ============================================================
echo SOURCE VERIFICATION FAILED
echo ============================================================
echo.
echo Nothing from DriftwoodServer was built or executed.
echo Temporary files were left in place for inspection:
echo "%D_TMP%"
echo.
pause
exit /b 1

:build_artifact_error
echo.
echo ============================================================
echo BUILD ARTIFACT CHECK FAILED
echo ============================================================
echo.
echo The source passed verification, but one or more generated
echo DriftwoodServer files are missing or could not be hashed.
echo.
echo If antivirus software displayed an alert, inspect its
echo quarantine. This installer does not add exclusions or
echo automatically restore quarantined files.
echo.
echo Temporary files were left in place for inspection:
echo "%D_TMP%"
echo.
pause
exit /b 1

:execution_cancelled
echo.
echo Installation stopped before executing DriftwoodServer.
echo.
echo Verified source information:
echo "%SOURCE_VERIFY_FILE%"
echo.
echo Generated build hashes:
echo "%BUILD_HASH_FILE%"
echo.
echo Temporary files were left in place:
echo "%D_TMP%"
echo.
pause
exit /b 0

:install_error
echo.
echo ============================================================
echo INSTALLATION FAILED
echo ============================================================
echo.
echo Temporary files were left in place for troubleshooting:
echo "%D_TMP%"
echo.
pause
exit /b 1
