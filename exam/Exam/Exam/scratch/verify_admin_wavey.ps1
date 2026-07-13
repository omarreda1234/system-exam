$loginUrl = "http://localhost:8052/Auth/Login"
$waveyUrl = "http://localhost:8052/Admin/WaveyResults?waveId=11"

# We will use a session variable to store cookies
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

# First, GET the login page to retrieve any cookies/antiforgery tokens
$loginPage = Invoke-WebRequest -Uri $loginUrl -SessionVariable session -UseBasicParsing
$token = ""
if ($loginPage.Content -match 'name="__RequestVerificationToken" type="hidden" value="([^"]+)"') {
    $token = $Matches[1]
}

# Login request
$body = @{
    "Email" = "mohamedmeati893@gmail.com"
    "Password" = "Password123!"
    "RememberMe" = "false"
}
if ($token) {
    $body["__RequestVerificationToken"] = $token
}

Write-Host "Logging in as admin..."
try {
    $response = Invoke-WebRequest -Uri $loginUrl -Method Post -Body $body -WebSession $session -UserAgent "Mozilla/5.0" -UseBasicParsing
    Write-Host "Login response status: $($response.StatusCode)"
    
    # Now request the WaveyResults page
    Write-Host "Requesting WaveyResults..."
    $waveyPage = Invoke-WebRequest -Uri $waveyUrl -Method Get -WebSession $session -UserAgent "Mozilla/5.0" -UseBasicParsing
    
    Write-Host "WaveyResults page retrieved. Length: $($waveyPage.Content.Length)"
    
    # Check for Exams Done column
    if ($waveyPage.Content -match "Exams Done") {
        Write-Host "[ERROR] 'Exams Done' column is still present in the page content!"
    } else {
        Write-Host "[SUCCESS] 'Exams Done' column is NOT present in the page content."
    }

    # Check for Final Exam Mode
    if ($waveyPage.Content -match "Final Exam Mode") {
        Write-Host "[SUCCESS] 'Final Exam Mode' header is present in the page content."
    } else {
        Write-Host "[WARNING] 'Final Exam Mode' header is NOT present in the page content."
    }

    # Check for Score & Percentage
    if ($waveyPage.Content -match "Score" -and $waveyPage.Content -match "Percentage") {
        Write-Host "[SUCCESS] 'Score' and 'Percentage' are present."
    }
}
catch {
    Write-Error $_
}
