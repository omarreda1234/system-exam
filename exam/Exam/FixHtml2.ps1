foreach ($file in @('d:\exam02\exam\Exam\Exam\Views\Attendance\TakeAttendance.cshtml', 'd:\exam02\exam\Exam\Exam\Views\SkillTracks\TakeAttendance.cshtml')) {
    $text = Get-Content $file -Raw -Encoding UTF8
    
    $text = $text -replace '<!-- Check-In / الحضور<div class="md:col-span-6', "<!-- Check-In / الحضور -->
                        <div class="md:col-span-6"
    
    Set-Content $file $text -Encoding UTF8
}
