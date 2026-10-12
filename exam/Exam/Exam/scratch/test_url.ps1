try {
    $url = 'https://sites.google.com/u/2/d/1VgJVqoROuStx6C7487iQUYOBA9PRFb6l/p/186ZCjC-JINmH0Ev-4yBijPSafJ8SZsJl/preview?authuser=2'
    $req = [System.Net.HttpWebRequest]::Create($url)
    $req.UserAgent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'
    $res = $req.GetResponse()
    Write-Output "Status: $([int]$res.StatusCode)"
    Write-Output "X-Frame-Options: $($res.Headers['X-Frame-Options'])"
    Write-Output "CSP: $($res.Headers['Content-Security-Policy'])"
} catch {
    Write-Output "Error: $($_.Exception.Message)"
}
