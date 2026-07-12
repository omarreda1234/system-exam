$loginUrl = "http://localhost:8052/Auth/Login"
$searchUrl = "http://localhost:8052/Assignments/SearchItems?q=1"
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

# First, GET login page to obtain antiforgery token
$loginPage = Invoke-WebRequest -Uri $loginUrl -SessionVariable session -UseBasicParsing
$token = ""
if ($loginPage.Content -match 'name="__RequestVerificationToken" type="hidden" value="([^"]+)"') {
    $token = $Matches[1]
}

# Login body
$body = @{
    "Email" = "192115@eru.edu.eg"
    "Password" = "Password123!"
    "RemmberMe" = "false"
}
if ($token) {
    $body["__RequestVerificationToken"] = $token
}

Write-Host "Logging in..."
try {
    $response = Invoke-WebRequest -Uri $loginUrl -Method Post -Body $body -WebSession $session -UserAgent "Mozilla/5.0" -UseBasicParsing
    
    Write-Host "Testing assignments search endpoint with q=1..."
    $searchResponse = Invoke-WebRequest -Uri $searchUrl -Method Get -WebSession $session -UserAgent "Mozilla/5.0" -UseBasicParsing
    
    Write-Host "Search response status: $($searchResponse.StatusCode)"
    Write-Host "Search response: $($searchResponse.Content)"
}
catch {
    Write-Error $_
}
