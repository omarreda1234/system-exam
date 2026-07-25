param (
    [int]$Port = 8052,
    [string]$FilePath = "exam/Exam/Exam/app_offline.htm"
)

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://+:$Port/")
try {
    $listener.Start()
    Write-Host "Listening on port $Port..."
} catch {
    Write-Error "Failed to start listener: $_"
    exit 1
}

# Read html content
if (!(Test-Path $FilePath)) {
    # Fallback to local app_offline.htm if not in the subfolder path
    $FilePath = "app_offline.htm"
}

if (Test-Path $FilePath) {
    $htmlContent = [System.IO.File]::ReadAllBytes($FilePath)
} else {
    $htmlContent = [System.Text.Encoding]::UTF8.GetBytes("<html><body><h2>System Under Maintenance</h2></body></html>")
}

# We will run for a maximum of 10 minutes or until a stop file/signal is received
$stopFile = Join-Path $env:TEMP "stop_listener_$Port.txt"
if (Test-Path $stopFile) { Remove-Item $stopFile }

while ($listener.IsListening) {
    if (Test-Path $stopFile) {
        break
    }
    
    # Check for incoming requests
    $contextTask = $listener.GetContextAsync()
    
    # Wait for the task to complete or the stop file to appear
    while (-not $contextTask.IsCompleted) {
        if (Test-Path $stopFile) {
            break
        }
        Start-Sleep -Milliseconds 100
    }
    
    if (Test-Path $stopFile) {
        break
    }
    
    try {
        $context = $contextTask.Result
        $response = $context.Response
        
        $response.StatusCode = 200
        $response.ContentType = "text/html; charset=utf-8"
        $response.ContentLength64 = $htmlContent.Length
        
        $response.OutputStream.Write($htmlContent, 0, $htmlContent.Length)
        $response.Close()
    } catch {
        # Ignore request failures
    }
}

$listener.Stop()
$listener.Close()
if (Test-Path $stopFile) { Remove-Item $stopFile }
Write-Host "Listener stopped."
