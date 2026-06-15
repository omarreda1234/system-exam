foreach ($file in @('d:\exam02\exam\Exam\Exam\Views\Attendance\TakeAttendance.cshtml', 'd:\exam02\exam\Exam\Exam\Views\SkillTracks\TakeAttendance.cshtml')) {
    $text = Get-Content $file -Raw -Encoding UTF8
    
    $text = $text -replace '<option value="No Branch">No Branch / بدون فرع"fas fa-chevron-down', "<option value="No Branch">No Branch / بدون فرع</option>
                            }
                        </select>
                        <i class="fas fa-chevron-down"
    
    $text = $text -replace '@\(user.BranchName \?\? "No Branch / بدون فرع"md:col-span-2', "@(user.BranchName ?? "No Branch / بدون فرع")
                                    </span>
                                </div>
                            </div>
                        <div class="md:col-span-2"
    
    Set-Content $file $text -Encoding UTF8
}
