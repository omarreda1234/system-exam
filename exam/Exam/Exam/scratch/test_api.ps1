try {
    $resp = Invoke-WebRequest -Uri "http://localhost:8052/Notification/GetAvailableCompanyColleagues?sessionId=7093&companyId=3029&branchId=55" -MaximumRedirection 0 -ErrorAction Stop
    Write-Host "Status:" $resp.StatusCode
    Write-Host "Content:" $resp.Content
} catch {
    Write-Host "Caught error:" $_.Exception.Message
    if ($_.Exception.Response) {
        Write-Host "Response Status:" $_.Exception.Response.StatusCode
        Write-Host "Location:" $_.Exception.Response.Headers.Location
    }
}
