using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace Exam.Controllers
{
    [ApiController]
    [Route("api/whatsapp")]
    public class WhatsAppApiController : ControllerBase
    {
        private readonly string _connectionString;

        public WhatsAppApiController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        /// <summary>
        /// Endpoint لجلب شهادات فرع معين لاستخدامها مع Node.js / WhatsApp Bot
        /// GET: /api/whatsapp/certificates?branchName=كفر الشيخ
        /// </summary>
        [HttpGet("certificates")]
        public async Task<IActionResult> GetBranchCertificates([FromQuery] string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                return BadRequest(new { success = false, message = "يرجى تحديد اسم الفرع." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                string query = @"
                    SELECT 
                        U.FullName AS StudentName,
                        U.UserCode,
                        B.BranchName,
                        W.WaveName,
                        UWC.CertificateCode,
                        UWC.Score
                    FROM dbo.UserWaves UW
                    INNER JOIN AspNetUsers U ON UW.UserId = U.Id
                    INNER JOIN TrainingWaves W ON UW.WaveId = W.Id
                    INNER JOIN Branches B ON U.BranchId = B.Id
                    INNER JOIN dbo.UserWaveCertificates UWC ON U.Id = UWC.UserId AND UWC.WaveId = UW.WaveId
                    WHERE B.BranchName LIKE @BranchName AND UWC.CertificateCode IS NOT NULL AND UWC.CertificateCode <> '/'
                    ORDER BY W.StartDate DESC, U.FullName ASC";

                var certificates = (await conn.QueryAsync<dynamic>(query, new { BranchName = $"%{branchName.Trim()}%" })).ToList();

                if (!certificates.Any())
                {
                    return Ok(new
                    {
                        success = false,
                        message = $"عذراً، لم يتم العثور على شهادات صادرة لفرع ({branchName.Trim()})."
                    });
                }

                return Ok(new
                {
                    success = true,
                    branch = branchName.Trim(),
                    totalCount = certificates.Count,
                    data = certificates
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = "حدث خطأ في السيرفر: " + ex.Message });
            }
        }

        /// <summary>
        /// Endpoint لقراءة شاشة Job Monitor الخاصة بالـ Data Director في الفرع
        /// GET: /api/whatsapp/job-monitor?branchIp=172.16.26.100&jobId=TRANS-VPN
        /// </summary>
        [HttpGet("job-monitor")]
        public async Task<IActionResult> GetJobMonitorStatus([FromQuery] string branchIp = "172.16.26.100", [FromQuery] string jobId = "TRANS-VPN")
        {
            if (string.IsNullOrWhiteSpace(branchIp))
            {
                return BadRequest(new { success = false, message = "يرجى تحديد IP الفرع." });
            }

            try
            {
                // قائمة محاكاة مطابقة لشاشة Job Monitor على الفرع (172.16.26.100:16860)
                var jobsList = new List<dynamic>
                {
                    new { PackGuid = "127fe1e0-6401-4475-8120-7f283fa74c43", OldPackId = "7637", OriginHost = "Replication", JobId = "TRANS-VPN", JobStatus = "Done", RowsProcessed = 7637, SourceHost = $"{branchIp}:16860" },
                    new { PackGuid = "ded71274-cc3d-4c00-a123-1a2b3c4d5e6f", OldPackId = "454", OriginHost = "Replication", JobId = "ILE-VPN", JobStatus = "Done", RowsProcessed = 454, SourceHost = $"{branchIp}:16860" },
                    new { PackGuid = "1a9c05d6-b328-4e12-89cd-7f8a9b0c1d2e", OldPackId = "7636", OriginHost = "Kafr210", JobId = "KAFR-HDD", JobStatus = "Processed", RowsProcessed = 7636, SourceHost = $"{branchIp}:16860" },
                    new { PackGuid = "d5c8466a-d3dc-4f89-9b0a-1c2d3e4f5a6b", OldPackId = "319", OriginHost = "Replication", JobId = "DISCOUNT VPN", JobStatus = "Done", RowsProcessed = 319, SourceHost = $"{branchIp}:16860" }
                };

                var filteredJobs = jobsList.Where(j => ((string)j.JobId).Equals(jobId, StringComparison.OrdinalIgnoreCase)).ToList();

                return Ok(new
                {
                    success = true,
                    branchIp = branchIp,
                    requestedJob = jobId,
                    hasError = false,
                    latestStatus = "Done",
                    totalRowsEffected = 7637,
                    jobsCount = jobsList.Count,
                    targetJobDetails = filteredJobs,
                    allActiveJobs = jobsList
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = "خطأ في الاتصال بالفرع: " + ex.Message });
            }
        }
    }
}

