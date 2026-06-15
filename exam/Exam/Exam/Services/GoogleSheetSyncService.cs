using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Exam.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using Dapper;

namespace Exam.Services
{
    public class GoogleSheetSyncService
    {
        private readonly string _spreadsheetId = "12MjEdtLLTzTwyb-qvKiKAAC9zNfOxCeQ7RSKpCD17OY";
        private readonly string _credentialsPath;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly string _connectionString;

        public GoogleSheetSyncService(IConfiguration configuration, IServiceProvider serviceProvider)
        {
            _credentialsPath = Path.Combine(Directory.GetCurrentDirectory(), "google-credentials.json");
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            
            // Resolve UserManager from scope to avoid disposal issues
            var scope = serviceProvider.CreateScope();
            _userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        }

        public async Task<(int added, int updated, List<string> errors)> SyncUsersAsync()
        {
            int added = 0;
            int updated = 0;
            List<string> errors = new List<string>();

            try
            {
                GoogleCredential credential;
                using (var stream = new FileStream(_credentialsPath, FileMode.Open, FileAccess.Read))
                {
                    credential = GoogleCredential.FromStream(stream).CreateScoped(SheetsService.Scope.SpreadsheetsReadonly);
                }

                var service = new SheetsService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "ExamSystemSync",
                });

                // 1. Get Spreadsheet metadata to find the first sheet name
                var spreadsheet = await service.Spreadsheets.Get(_spreadsheetId).ExecuteAsync();
                var firstSheetName = spreadsheet.Sheets.FirstOrDefault()?.Properties?.Title ?? "Sheet1";

                // 2. Range A2:H (8 Columns as requested)
                var range = $"{firstSheetName}!A2:H"; 
                var request = service.Spreadsheets.Values.Get(_spreadsheetId, range);
                var response = await request.ExecuteAsync();
                var values = response.Values;

                if (values == null || values.Count == 0)
                {
                    errors.Add($"No data found in Google Sheet [{firstSheetName}].");
                    return (0, 0, errors);
                }

                using var conn = new SqlConnection(_connectionString);
                // Cache branches to avoid thousands of DB calls
                var branchCache = (await conn.QueryAsync<dynamic>("SELECT Id, BranchName FROM Branches")).ToDictionary(x => (string)x.BranchName, x => (int)x.Id);

                foreach (var row in values)
                {
                    if (row.Count < 5) continue;

                    string code = row[0]?.ToString()?.Trim();
                    string fullName = row[1]?.ToString()?.Trim();
                    string branchName = row[2]?.ToString()?.Trim();
                    string roleName = row[3]?.ToString()?.Trim();
                    string email = row[4]?.ToString()?.Trim();
                    string password = row.Count > 5 ? row[5]?.ToString()?.Trim() : "User@123";
                    string phone = row.Count > 6 ? row[6]?.ToString()?.Trim() : "";
                    string shiftIdStr = row.Count > 7 ? row[7]?.ToString()?.Trim() : null;

                    if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(fullName)) continue;

                    var user = await _userManager.FindByEmailAsync(email);
                    if (user == null && !string.IsNullOrEmpty(code))
                    {
                        user = _userManager.Users.FirstOrDefault(u => u.UserCode == code);
                    }

                    if (user == null)
                    {
                        user = new ApplicationUser { UserName = fullName, Email = email, UserCode = code, PhoneNumber = phone, EmailConfirmed = true, IsActive = true };
                        var result = await _userManager.CreateAsync(user, string.IsNullOrEmpty(password) ? "User@123" : password);
                        if (result.Succeeded)
                        {
                            if (!string.IsNullOrEmpty(roleName)) await _userManager.AddToRoleAsync(user, roleName);
                            added++;
                        }
                    }
                    else
                    {
                        bool changed = false;
                        if (user.UserName != fullName) { user.UserName = fullName; changed = true; }
                        if (user.UserCode != code) { user.UserCode = code; changed = true; }
                        if (user.PhoneNumber != phone) { user.PhoneNumber = phone; changed = true; }
                        
                        if (changed) await _userManager.UpdateAsync(user);
                        updated++;
                    }

                    // Update Branch & Shift using direct SQL for speed (only if changed or needed)
                    if (user != null)
                    {
                        if (!string.IsNullOrEmpty(branchName) && branchCache.TryGetValue(branchName, out int bId))
                        {
                            await conn.ExecuteAsync("UPDATE AspNetUsers SET BranchId = @bid WHERE Id = @uid AND (BranchId IS NULL OR BranchId <> @bid)", new { bid = bId, uid = user.Id });
                        }

                        if (!string.IsNullOrEmpty(shiftIdStr) && int.TryParse(shiftIdStr, out int sId))
                        {
                            await conn.ExecuteAsync("UPDATE AspNetUsers SET ShiftId = @sid WHERE Id = @uid AND (ShiftId IS NULL OR ShiftId <> @sid)", new { sid = sId, uid = user.Id });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Sync Error: {ex.Message}");
            }

            return (added, updated, errors);
        }
    }
}
