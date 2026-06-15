import re

with open('d:/exam02/exam/Exam/Exam/Controllers/AttendanceController.cs', 'r', encoding='utf-8') as f:
    content = f.read()

replacements = [
    (r'"Name / .*?"', r'"Name / الاسم"'),
    (r'"Code / .*?"', r'"Code / الكود"'),
    (r'"Branch / .*?"', r'"Branch / الفرع"'),
    (r'"Status / .*?"', r'"Status / الحالة"'),
    (r'"Check In Time / .*?"', r'"Check In Time / وقت الحضور"'),
    (r'"Check Out Time / .*?"', r'"Check Out Time / وقت الانصراف"'),
    (r'"Present / .*?"', r'"Present / حاضر"'),
    (r'"Absent / .*?"', r'"Absent / غائب"'),
    (r"N'Wave / .*?'", r"N'Wave / دفعة'"),
    (r"N'Company / .*?'", r"N'Company / شركة'"),
    (r'"Employee Name / .*?"', r'"Employee Name / الاسم"'),
    (r'"Employee Code / .*?"', r'"Employee Code / الكود"'),
    (r'"Group/Company Name / .*?"', r'"Group/Company Name / المجموعة/الشركة"'),
    (r'"Type / .*?"', r'"Type / النوع"'),
    (r'"Session Name / .*?"', r'"Session Name / اسم المحاضرة"'),
    (r'"Date / .*?"', r'"Date / التاريخ"'),
    (r'"Check-In Time / .*?"', r'"Check-In Time / وقت الحضور"'),
    (r'"Check-Out Time / .*?"', r'"Check-Out Time / وقت الانصراف"'),
    (r'"Total Present / .*?"', r'"Total Present / إجمالي الحضور"')
]

for p, r in replacements:
    content = re.sub(p, r, content)

with open('d:/exam02/exam/Exam/Exam/Controllers/AttendanceController.cs', 'w', encoding='utf-8') as f:
    f.write(content)
