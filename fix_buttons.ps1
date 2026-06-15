$file = 'd:\exam02\exam\Exam\Exam\Views\Exams\Start.cshtml'
$content = [System.IO.File]::ReadAllText($file)

# Old submit button
$old1 = '<button type="button" id="submitBtn" class="bg-brand-600 hover:bg-brand-700 text-white px-8 py-4 rounded-xl font-black uppercase text-xs tracking-widest transition-all shadow-lg hover:shadow-brand-500/30 hover:-translate-y-0.5 hidden">'
$new1 = '<button type="button" id="submitBtn" disabled class="bg-brand-600 text-white px-8 py-4 rounded-xl font-black uppercase text-xs tracking-widest transition-all shadow-lg hidden" style="opacity:.4;cursor:not-allowed;">'
$content = $content.Replace($old1, $new1)

# Old next button
$old2 = '<button type="button" id="nextBtn" class="bg-slate-900 hover:bg-slate-800 text-white px-10 py-4 rounded-xl font-black uppercase text-xs tracking-widest transition-all shadow-lg hover:-translate-y-0.5">'
$new2 = '<button type="button" id="nextBtn" disabled class="bg-slate-900 text-white px-10 py-4 rounded-xl font-black uppercase text-xs tracking-widest transition-all shadow-lg" style="opacity:.4;cursor:not-allowed;">'
$content = $content.Replace($old2, $new2)

[System.IO.File]::WriteAllText($file, $content, [System.Text.Encoding]::UTF8)
Write-Host "Done - buttons updated"
