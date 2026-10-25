@echo off
title n8n Workflow Automation Engine
echo ========================================================
echo Starting n8n Automation Engine on port 5678...
echo Webhook URL: http://localhost:5678/webhook/wave-nomination-notification
echo ========================================================

set N8N_PORT=5678
set N8N_DEFAULT_BINARY_DATA_MODE=default
set WEBHOOK_URL=http://localhost:5678/

where n8n >nul 2>nul
if %errorlevel% equ 0 (
    call n8n start
) else (
    echo [INFO] n8n not found globally in PATH, launching via npx...
    call npx -y n8n start
)
pause
