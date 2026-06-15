using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

class Program
{
    static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var enc1252 = Encoding.GetEncoding(1252);
        
        string[] files = {
            @"d:\exam02\exam\Exam\Exam\Views\Shared\_Layout.cshtml",
            @"d:\exam02\exam\Exam\Exam\Views\SkillTracks\TakeAttendance.cshtml",
            @"d:\exam02\exam\Exam\Exam\Views\Attendance\TakeAttendance.cshtml",
            @"d:\exam02\exam\Exam\Exam\Views\Attendance\Analytics.cshtml",
            @"d:\exam02\exam\Exam\Exam\Views\SkillTracks\Analytics.cshtml"
        };
        
        foreach (var file in files) {
            if (!File.Exists(file)) continue;
            string text = File.ReadAllText(file, Encoding.UTF8);
            
            // Decode mojibake like Ã˜Â­Ã˜Â¶Ã... by converting string -> 1252 -> UTF8
            // We only want to convert the Arabic parts, but if the whole file was read as 1252 and saved as UTF8,
            // we could convert the whole file back. But we only did that to some files!
            // Wait, we didn't do it to TakeAttendance.cshtml!
            // Actually, let's just do regex replaces on the known English prefixes.
            
            text = Regex.Replace(text, @"""pharmacist""\)[^\)]*\)[^\)]*\)[^\)]*\)[^\)]*\)[^\)]*\)", @"""pharmacist"") || User.IsInRole(""assistant"") || User.IsInRole(""صيدلي"") || User.IsInRole(""مساعد صيدلي"") || User.IsInRole(""Pharmacist"") || User.IsInRole(""Assistant"")");
            
            text = Regex.Replace(text, @"All Branches / [^<]*", "All Branches / جميع الفروع");
            text = Regex.Replace(text, @"No Branch / [^<]*", "No Branch / بدون فرع");
            text = Regex.Replace(text, @"No Branch / [^""]*", "No Branch / بدون فرع");
            
            text = Regex.Replace(text, @"Trainee / [^<]*", "Trainee / المتدرب");
            text = Regex.Replace(text, @"Code / [^<]*", "Code / الكود");
            text = Regex.Replace(text, @"Attendance / [^<]*", "Attendance / الحضور والانصراف");
            
            text = Regex.Replace(text, @"Arrived / [^<]*", "Arrived / حضور");
            text = Regex.Replace(text, @"Left / [^<]*", "Left / انصراف");
            
            text = Regex.Replace(text, @"Check-In / [^<]*", "Check-In / الحضور");
            text = Regex.Replace(text, @"Check-Out / [^<]*", "Check-Out / الانصراف");
            
            File.WriteAllText(file, text, Encoding.UTF8);
            Console.WriteLine("Fixed " + file);
        }
    }
}
