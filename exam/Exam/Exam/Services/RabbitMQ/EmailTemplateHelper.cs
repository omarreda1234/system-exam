using System;

namespace Exam.Services.RabbitMQ
{
    public static class EmailTemplateHelper
    {
        public static string BuildCompanySessionEmail(
            string candidateName,
            string sessionName,
            string companyName,
            string sessionDate,
            string startTime,
            string endTime,
            string branchName,
            string eventType,
            string siteUrl = "https://academy.eltarshouby.com")
        {
            string titleBadge = eventType switch
            {
                "DirectApproval" => "اعتماد وحضور مباشر",
                "BranchManagerApproval" => "اعتماد مدير الفرع",
                "Replacement" => "ترشيح بديل معتمد",
                "FairRotationInjection" => "ترشيح بنظام التناوب الدوري",
                "ExcelImport" => "تسجيل واعتماد إكسيل",
                _ => "دعوة لحضور تدريب"
            };

            return $@"
<div dir='rtl' style='font-family: ""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; background-color: #f8fafc; padding: 30px 15px; margin: 0;'>
    <div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 20px; overflow: hidden; box-shadow: 0 10px 30px rgba(0,0,0,0.06); border: 1px solid #e2e8f0;'>
        <!-- Header Banner -->
        <div style='background: linear-gradient(135deg, #0f766e 0%, #0d9488 50%, #14b8a6 100%); padding: 35px 25px; text-align: center; color: #ffffff;'>
            <div style='display: inline-block; background-color: rgba(255,255,255,0.18); padding: 6px 16px; border-radius: 9999px; font-size: 11px; font-weight: bold; letter-spacing: 0.5px; margin-bottom: 12px; border: 1px solid rgba(255,255,255,0.25);'>
                {titleBadge} ✅
            </div>
            <h1 style='margin: 0; font-size: 22px; font-weight: 800; line-height: 1.4;'>أكاديمية الطرشوبي للتدريب والتطوير</h1>
            <p style='margin: 8px 0 0; font-size: 13px; opacity: 0.92;'>تأكيد اعتماد وترشيح حضور جلسة تدريبية</p>
        </div>

        <!-- Body Content -->
        <div style='padding: 30px 25px; color: #1e293b; line-height: 1.8;'>
            <p style='font-size: 16px; font-weight: bold; color: #0f766e; margin-top: 0;'>
                مرحباً {candidateName}،
            </p>
            <p style='font-size: 14px; color: #334155; margin-bottom: 22px;'>
                يسعدنا إبلاغك بأنه تم اعتماد ترشيحك رسمياً لحضور الجلسة التدريبية المقررة الخاصة بشركة <strong>{companyName}</strong>. نتمنى لك تجربة تعليمية متميزة ومثمرة.
            </p>

            <!-- Details Card -->
            <div style='background-color: #f1f5f9; border-radius: 16px; padding: 20px; margin-bottom: 25px; border-right: 4px solid #0d9488;'>
                <table style='width: 100%; border-collapse: collapse; font-size: 13.5px;'>
                    <tr>
                        <td style='padding: 6px 0; color: #64748b; font-weight: bold; width: 35%;'>عنوان الجلسة:</td>
                        <td style='padding: 6px 0; color: #0f172a; font-weight: 700;'>{sessionName}</td>
                    </tr>
                    <tr>
                        <td style='padding: 6px 0; color: #64748b; font-weight: bold;'>الشركة:</td>
                        <td style='padding: 6px 0; color: #0f172a; font-weight: 700;'>{companyName}</td>
                    </tr>
                    <tr>
                        <td style='padding: 6px 0; color: #64748b; font-weight: bold;'>تاريخ التدريب:</td>
                        <td style='padding: 6px 0; color: #0f766e; font-weight: 700;'>{sessionDate}</td>
                    </tr>
                    <tr>
                        <td style='padding: 6px 0; color: #64748b; font-weight: bold;'>التوقيت:</td>
                        <td style='padding: 6px 0; color: #0f172a;'>من {startTime} إلى {endTime}</td>
                    </tr>
                    <tr>
                        <td style='padding: 6px 0; color: #64748b; font-weight: bold;'>الفرع:</td>
                        <td style='padding: 6px 0; color: #0f172a;'>{branchName}</td>
                    </tr>
                </table>
            </div>

            <!-- Notes -->
            <div style='background-color: #fefce8; border: 1px solid #fef08a; border-radius: 12px; padding: 14px 18px; margin-bottom: 25px; font-size: 12.5px; color: #854d0e;'>
                <strong>ملاحظة هامة:</strong> يرجى التواجد في الموعد المحدد والالتزام بالحضور للاستفادة الكاملة من المادة التدريبية، وتسجيل الحضور بالبصمة أو الكود الخاص بك.
            </div>

            <!-- CTA Button -->
            <div style='text-align: center; margin: 30px 0 10px;'>
                <a href='{siteUrl}' style='display: inline-block; background: linear-gradient(135deg, #0f766e 0%, #0d9488 100%); color: #ffffff; text-decoration: none; padding: 14px 32px; border-radius: 12px; font-size: 14px; font-weight: bold; box-shadow: 0 4px 12px rgba(15, 118, 110, 0.25);'>
                    الدخول إلى منصة الأكاديمية
                </a>
            </div>
        </div>

        <!-- Footer -->
        <div style='background-color: #f8fafc; border-top: 1px solid #e2e8f0; padding: 20px; text-align: center; font-size: 11.5px; color: #94a3b8;'>
            <p style='margin: 0;'>أكاديمية الطرشوبي للتدريب والتطوير المستمر © {DateTime.Now.Year}</p>
            <p style='margin: 4px 0 0;'>هذه رسالة آلية تم إرسالها من خلال وسيط الرسائل السريع (RabbitMQ Message Broker).</p>
        </div>
    </div>
</div>";
        }

        public static string BuildWaveEnrollmentEmail(
            string candidateName,
            string waveName,
            string startDate,
            string mode,
            string branchName,
            string siteUrl = "https://academy.eltarshouby.com")
        {
            return $@"
<div dir='rtl' style='font-family: ""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; background-color: #f8fafc; padding: 30px 15px; margin: 0;'>
    <div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 20px; overflow: hidden; box-shadow: 0 10px 30px rgba(0,0,0,0.06); border: 1px solid #e2e8f0;'>
        <!-- Header Banner -->
        <div style='background: linear-gradient(135deg, #4f46e5 0%, #6366f1 50%, #818cf8 100%); padding: 35px 25px; text-align: center; color: #ffffff;'>
            <div style='display: inline-block; background-color: rgba(255,255,255,0.18); padding: 6px 16px; border-radius: 9999px; font-size: 11px; font-weight: bold; letter-spacing: 0.5px; margin-bottom: 12px; border: 1px solid rgba(255,255,255,0.25);'>
                تسكين واعتماد في الدفعة التدريبية 🚀
            </div>
            <h1 style='margin: 0; font-size: 22px; font-weight: 800; line-height: 1.4;'>أكاديمية الطرشوبي للتدريب والتطوير</h1>
            <p style='margin: 8px 0 0; font-size: 13px; opacity: 0.92;'>تهانينا! تم تسكينك واعتماد ترشيحك في الويف</p>
        </div>

        <!-- Body Content -->
        <div style='padding: 30px 25px; color: #1e293b; line-height: 1.8;'>
            <p style='font-size: 16px; font-weight: bold; color: #4f46e5; margin-top: 0;'>
                مرحباً {candidateName}،
            </p>
            <p style='font-size: 14px; color: #334155; margin-bottom: 22px;'>
                يسعدنا إبلاغك بأنه تم اختيارك وتسكينك رسمياً في الدفعة التدريبية: <strong>{waveName}</strong> عبر نظام الترشيح الذكي بالأكاديمية.
            </p>

            <!-- Details Card -->
            <div style='background-color: #f1f5f9; border-radius: 16px; padding: 20px; margin-bottom: 25px; border-right: 4px solid #6366f1;'>
                <table style='width: 100%; border-collapse: collapse; font-size: 13.5px;'>
                    <tr>
                        <td style='padding: 6px 0; color: #64748b; font-weight: bold; width: 35%;'>اسم الدفعة:</td>
                        <td style='padding: 6px 0; color: #0f172a; font-weight: 700;'>{waveName}</td>
                    </tr>
                    <tr>
                        <td style='padding: 6px 0; color: #64748b; font-weight: bold;'>تاريخ البداية:</td>
                        <td style='padding: 6px 0; color: #4f46e5; font-weight: 700;'>{startDate}</td>
                    </tr>
                    <tr>
                        <td style='padding: 6px 0; color: #64748b; font-weight: bold;'>نظام / مكان الحضور:</td>
                        <td style='padding: 6px 0; color: #0f172a;'>{mode}</td>
                    </tr>
                    <tr>
                        <td style='padding: 6px 0; color: #64748b; font-weight: bold;'>الفرع:</td>
                        <td style='padding: 6px 0; color: #0f172a;'>{branchName}</td>
                    </tr>
                </table>
            </div>

            <!-- Notes -->
            <div style='background-color: #eef2ff; border: 1px solid #c7d2fe; border-radius: 12px; padding: 14px 18px; margin-bottom: 25px; font-size: 12.5px; color: #3730a3;'>
                <strong>تنويه:</strong> يرجى التنسيق المسبق مع مدير الفرع لتنظيم الوردية وتأكيد الجاهزية لحضور المحاضرات وأداء الاختبارات المقررة في مواعيدها.
            </div>

            <!-- CTA Button -->
            <div style='text-align: center; margin: 30px 0 10px;'>
                <a href='{siteUrl}' style='display: inline-block; background: linear-gradient(135deg, #4f46e5 0%, #6366f1 100%); color: #ffffff; text-decoration: none; padding: 14px 32px; border-radius: 12px; font-size: 14px; font-weight: bold; box-shadow: 0 4px 12px rgba(79, 70, 229, 0.25);'>
                    الدخول ومتابعة الويف
                </a>
            </div>
        </div>

        <!-- Footer -->
        <div style='background-color: #f8fafc; border-top: 1px solid #e2e8f0; padding: 20px; text-align: center; font-size: 11.5px; color: #94a3b8;'>
            <p style='margin: 0;'>أكاديمية الطرشوبي للتدريب والتطوير المستمر © {DateTime.Now.Year}</p>
            <p style='margin: 4px 0 0;'>تمت المعالجة الآمنة عبر وسيط الرسائل (RabbitMQ Enterprise Broker).</p>
        </div>
    </div>
</div>";
        }
    }
}
