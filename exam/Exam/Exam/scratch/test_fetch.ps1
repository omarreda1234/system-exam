$res = Invoke-WebRequest -Uri "https://sites.google.com/view/tarshoubiacademyweeklymessage" -UserAgent "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" -UseBasicParsing
Write-Output "Status: $($res.StatusCode), Length: $($res.Content.Length)"
$res.Content | Out-File -FilePath "c:\exam final\exam\Exam\Exam\scratch\google_site.html" -Encoding utf8
