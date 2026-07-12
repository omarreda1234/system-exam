$loginUrl = "http://localhost:8052/Auth/Login"
$assignmentsUrl = "http://localhost:8052/Assignments"
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$loginPage = Invoke-WebRequest -Uri $loginUrl -SessionVariable session -UseBasicParsing
$token = ""
if ($loginPage.Content -match 'name="__RequestVerificationToken" type="hidden" value="([^"]+)"') {
    $token = $Matches[1]
}
$body = @{
    "Email" = "192115@eru.edu.eg"
    "Password" = "Password123!"
    "RemmberMe" = "false"
}
if ($token) {
    $body["__RequestVerificationToken"] = $token
}
try {
    $response = Invoke-WebRequest -Uri $loginUrl -Method Post -Body $body -WebSession $session -UserAgent "Mozilla/5.0" -UseBasicParsing
    $assignmentsPage = Invoke-WebRequest -Uri $assignmentsUrl -Method Get -WebSession $session -UserAgent "Mozilla/5.0" -UseBasicParsing
    [IO.File]::WriteAllText("c:\exam final\exam\Exam\Exam\scratch\assignments_response.html", $assignmentsPage.Content)
    Write-Host "Wrote assignments response to assignments_response.html"
}
catch {
    Write-Error $_
}
