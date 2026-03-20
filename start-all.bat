@echo off
echo ============================================
echo   Air Traffic Control - Starting All Components
echo ============================================
echo.

:: 0. Build and run tests
echo [0/5] Building solution and running tests...
cd /d %~dp0
dotnet build --nologo -v q
if %ERRORLEVEL% neq 0 (
    echo ERROR: Build failed.
    pause
    exit /b 1
)

dotnet test tests\ATC.Tests\ATC.Tests.csproj --nologo --no-build -v q
if %ERRORLEVEL% neq 0 (
    echo ERROR: Tests failed. Fix test failures before starting.
    pause
    exit /b 1
)
echo All tests passed.
echo.

:: 1. Start infrastructure (Kafka + Redis)
echo [1/5] Starting Kafka and Redis via Docker Compose...
docker compose up -d
if %ERRORLEVEL% neq 0 (
    echo ERROR: Docker Compose failed. Make sure Docker Desktop is running.
    pause
    exit /b 1
)

:: 2. Wait for infrastructure to be healthy
echo [2/5] Waiting for Kafka and Redis to be ready...
:wait_loop
docker compose ps --format "{{.Health}}" | findstr /i "starting" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    timeout /t 5 /nobreak >nul
    goto wait_loop
)
echo Infrastructure is ready.
echo.

:: 3. Start .NET services in separate windows
echo [3/5] Starting ATC.TelemetryProducer...
start "ATC.TelemetryProducer" cmd /k "cd /d %~dp0src\ATC.TelemetryProducer && dotnet run"

echo [4/5] Starting ATC.TrackingEngine...
start "ATC.TrackingEngine" cmd /k "cd /d %~dp0src\ATC.TrackingEngine && dotnet run"

:: Small delay so the SignalR hub is ready when the dashboard connects
timeout /t 3 /nobreak >nul

echo [5/5] Starting ATC.DashboardApi (web interface)...
start "ATC.DashboardApi" cmd /k "cd /d %~dp0src\ATC.DashboardApi && dotnet run --no-launch-profile"

echo.
echo ============================================
echo   All components started!
echo.
echo   Dashboard:  http://localhost:5000
echo   Redis UI:   http://localhost:8001
echo ============================================
echo.
echo Each .NET service is running in its own window.
echo Close those windows or press Ctrl+C in them to stop.
echo To stop infrastructure: docker compose down
echo.
pause
