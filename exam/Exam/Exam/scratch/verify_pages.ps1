$loginUrl = "http://localhost:8052/Auth/Login"
$assignmentsUrl = "http://localhost:8052/Assignments"

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
    "Email" = "192115@eru.edu.eg"
    "Password" = "Password123!"
    "RememberMe" = "false"
}
if ($token) {
    $body["__RequestVerificationToken"] = $token
}

Write-Host "Logging in..."
try {
    $response = Invoke-WebRequest -Uri $loginUrl -Method Post -Body $body -WebSession $session -UserAgent "Mozilla/5.0" -UseBasicParsing
    Write-Host "Login response status: $($response.StatusCode)"
    
    # Now request the Assignments page
    Write-Host "Requesting Assignments Index..."
    $assignmentsPage = Invoke-WebRequest -Uri $assignmentsUrl -Method Get -WebSession $session -UserAgent "Mozilla/5.0" -UseBasicParsing
    
    Write-Host "Assignments page retrieved. Length: $($assignmentsPage.Content.Length)"
    
    # Check for some English keywords
    $keywords = @("My Training Assignments", "Scheduled", "Score", "Solve Portal", "Completed", "Pending")
    foreach ($kw in $keywords) {
        if ($assignmentsPage.Content -match $kw) {
            Write-Host "[SUCCESS] Found keyword: $kw"
        } else {
            Write-Host "[WARNING] Keyword not found: $kw"
        }
    }
}
catch {
    Write-Error $_
}
