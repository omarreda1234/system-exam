try {
    $res = Invoke-WebRequest -Uri 'http://127.0.0.1:8052/Notification/GetAvailableCompanyColleagues?sessionId=7093&companyId=3029&branchName=%D8%A7%D9%84%D8%AC%D9%8A%D8%B4' -UseBasicParsing -MaximumRedirection 0
    Write-Output "Status: $($res.StatusCode)"
    Write-Output $res.Content.Substring(0, [Math]::Min(500, $res.Content.Length))
} catch {
    Write-Output "Error: $_"
    if ($_.Exception.Response) {
        Write-Output "Status: $($_.Exception.Response.StatusCode)"
        $stream = $_.Exception.Response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        $body = $reader.ReadToEnd()
        Write-Output $body.Substring(0, [Math]::Min(500, $body.Length))
    }
}
