using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Exam.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Exam.Services
{
    public interface IHomeCmsService
    {
        Task EnsureTablesCreatedAsync();
        Task<HomeCmsViewModel> GetHomeCmsDataAsync(bool activeOnly = true);
        Task<List<HomeSliderImage>> GetSliderImagesAsync(bool activeOnly = false);
        Task<bool> AddSliderImageAsync(IFormFile file, string title, string subtitle);
        Task<bool> DeleteSliderImageAsync(int id, string webRootPath);
        Task<bool> ToggleSliderImageAsync(int id);
        Task<List<HomeSection>> GetSectionsAsync(bool visibleOnly = false);
        Task<bool> SaveSectionAsync(HomeSection section, IFormFile imageFile = null, string webRootPath = null);
        Task<bool> DeleteSectionAsync(int id);
        Task<bool> ToggleSectionVisibilityAsync(int id);
        Task<List<HomeFacultyMember>> GetFacultyMembersAsync(bool activeOnly = false);
        Task<bool> SaveFacultyMemberAsync(HomeFacultyMember faculty, IFormFile imageFile = null, string webRootPath = null);
        Task<bool> DeleteFacultyMemberAsync(int id, string webRootPath = null);
        Task<bool> ToggleFacultyActiveAsync(int id);
    }

    public class HomeCmsService : IHomeCmsService
    {
        private readonly string _connectionString;

        public HomeCmsService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        }

        public async Task EnsureTablesCreatedAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            string sql = @"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'HomeSliderImages')
            BEGIN
                CREATE TABLE dbo.HomeSliderImages (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    ImageUrl NVARCHAR(500) NOT NULL,
                    Title NVARCHAR(250) NULL,
                    Subtitle NVARCHAR(250) NULL,
                    DisplayOrder INT NOT NULL DEFAULT 0,
                    IsActive BIT NOT NULL DEFAULT 1,
                    CreatedAt DATETIME NOT NULL DEFAULT GETDATE()
                );

                INSERT INTO dbo.HomeSliderImages (ImageUrl, Title, Subtitle, DisplayOrder, IsActive)
                VALUES 
                ('/images/LL.jpg', 'Digital Powerhouse', 'Eltarshoubi Academy', 1, 1),
                ('/images/slider2.png', 'Lab Research', 'Eltarshoubi Academy', 2, 1),
                ('/images/slider3.png', 'Team Collaboration', 'Eltarshoubi Academy', 3, 1);
            END;

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'HomeSections')
            BEGIN
                CREATE TABLE dbo.HomeSections (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    SectionKey NVARCHAR(100) NOT NULL,
                    Title NVARCHAR(250) NULL,
                    Subtitle NVARCHAR(250) NULL,
                    ContentHtml NVARCHAR(MAX) NULL,
                    Icon NVARCHAR(100) NULL,
                    ImageUrl NVARCHAR(500) NULL,
                    DisplayOrder INT NOT NULL DEFAULT 0,
                    IsVisible BIT NOT NULL DEFAULT 1
                );

                INSERT INTO dbo.HomeSections (SectionKey, Title, Subtitle, ContentHtml, Icon, ImageUrl, DisplayOrder, IsVisible)
                VALUES
                ('story', 'Our Story', 'Knowledge into Performance', 'Eltarshoubi Training Academy began with a single vision: to bridge the gap between academic theory and clinical excellence. Today, we stand as a global beacon for pharmaceutical education, empowering thousands of clinicians through cutting-edge technology and evidence-based practice.', 'fas fa-history', '', 1, 1),
                ('story_badge', 'Established 2026 Founded In Mansoura', 'Founded in Cairo', '', 'fas fa-history', '', 2, 1),
                ('vision', 'Our Vision', 'Global Pioneer', 'To develop highly qualified pharmaceutical professionals who contribute to enhancing the quality of healthcare in Egypt.', 'fas fa-eye', '', 3, 1),
                ('mission', 'Our Mission', 'Innovation, Safety, Impact', 'Provide hands-on training in pharmaceutical and cosmeceutical formulations.||Enhance personal and soft skills essential for professional success.||Deliver specialized training in customer service, call center operations, and digital systems handling.||Offer continuous development programs for professionals to ensure sustained excellence.||Create summer training opportunities for pharmacy students to integrate them into real work environments.', 'fas fa-rocket', '', 4, 1),
                ('faculty', 'Meet Our Faculty', 'Empowering Pharmacists', 'We believe that building an integrated healthcare community starts with empowering pharmacists through knowledge, skills, and continuous development.', 'fas fa-user-graduate', '', 5, 1);
            END;

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'HomeFacultyMembers')
            BEGIN
                CREATE TABLE dbo.HomeFacultyMembers (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Name NVARCHAR(250) NOT NULL,
                    RoleTitle NVARCHAR(250) NULL,
                    Bio NVARCHAR(1000) NULL,
                    ImageUrl NVARCHAR(500) NULL,
                    DisplayOrder INT NOT NULL DEFAULT 0,
                    IsActive BIT NOT NULL DEFAULT 1,
                    CreatedAt DATETIME NOT NULL DEFAULT GETDATE()
                );
            END;
            ";

            await conn.ExecuteAsync(sql);
        }

        public async Task<HomeCmsViewModel> GetHomeCmsDataAsync(bool activeOnly = true)
        {
            await EnsureTablesCreatedAsync();
            using var conn = new SqlConnection(_connectionString);

            var model = new HomeCmsViewModel();

            string sliderSql = activeOnly 
                ? "SELECT * FROM dbo.HomeSliderImages WHERE IsActive = 1 ORDER BY DisplayOrder ASC, Id ASC"
                : "SELECT * FROM dbo.HomeSliderImages ORDER BY DisplayOrder ASC, Id ASC";
            model.SliderImages = (await conn.QueryAsync<HomeSliderImage>(sliderSql)).ToList();

            string sectionSql = activeOnly
                ? "SELECT * FROM dbo.HomeSections WHERE IsVisible = 1 ORDER BY DisplayOrder ASC, Id ASC"
                : "SELECT * FROM dbo.HomeSections ORDER BY DisplayOrder ASC, Id ASC";
            var sections = (await conn.QueryAsync<HomeSection>(sectionSql)).ToList();

            foreach (var sec in sections)
            {
                if (!string.IsNullOrEmpty(sec.SectionKey) && !model.Sections.ContainsKey(sec.SectionKey))
                {
                    model.Sections[sec.SectionKey] = sec;
                }
                else
                {
                    model.CustomSections.Add(sec);
                }
            }

            string facultySql = activeOnly
                ? "SELECT * FROM dbo.HomeFacultyMembers WHERE IsActive = 1 ORDER BY DisplayOrder ASC, Id ASC"
                : "SELECT * FROM dbo.HomeFacultyMembers ORDER BY DisplayOrder ASC, Id ASC";
            model.FacultyMembers = (await conn.QueryAsync<HomeFacultyMember>(facultySql)).ToList();

            return model;
        }

        public async Task<List<HomeSliderImage>> GetSliderImagesAsync(bool activeOnly = false)
        {
            await EnsureTablesCreatedAsync();
            using var conn = new SqlConnection(_connectionString);
            string sql = activeOnly 
                ? "SELECT * FROM dbo.HomeSliderImages WHERE IsActive = 1 ORDER BY DisplayOrder ASC, Id ASC"
                : "SELECT * FROM dbo.HomeSliderImages ORDER BY DisplayOrder ASC, Id ASC";
            return (await conn.QueryAsync<HomeSliderImage>(sql)).ToList();
        }

        public async Task<bool> AddSliderImageAsync(IFormFile file, string title, string subtitle)
        {
            if (file == null || file.Length == 0) return false;

            string uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "home");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            string fileExt = Path.GetExtension(file.FileName);
            string uniqueFileName = $"slider_{Guid.NewGuid():N}{fileExt}";
            string filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            string relativeUrl = $"/uploads/home/{uniqueFileName}";

            using var conn = new SqlConnection(_connectionString);
            int maxOrder = await conn.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(DisplayOrder), 0) FROM dbo.HomeSliderImages");
            
            string sql = @"
                INSERT INTO dbo.HomeSliderImages (ImageUrl, Title, Subtitle, DisplayOrder, IsActive, CreatedAt)
                VALUES (@ImageUrl, @Title, @Subtitle, @DisplayOrder, 1, GETDATE())";

            int rows = await conn.ExecuteAsync(sql, new { ImageUrl = relativeUrl, Title = title ?? "", Subtitle = subtitle ?? "Eltarshoubi Academy", DisplayOrder = maxOrder + 1 });
            return rows > 0;
        }

        public async Task<bool> DeleteSliderImageAsync(int id, string webRootPath)
        {
            using var conn = new SqlConnection(_connectionString);
            var item = await conn.QueryFirstOrDefaultAsync<HomeSliderImage>("SELECT * FROM dbo.HomeSliderImages WHERE Id = @Id", new { Id = id });
            if (item == null) return false;

            if (!string.IsNullOrEmpty(item.ImageUrl) && item.ImageUrl.StartsWith("/uploads/"))
            {
                string fullPath = Path.Combine(webRootPath, item.ImageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    try { File.Delete(fullPath); } catch { }
                }
            }

            int rows = await conn.ExecuteAsync("DELETE FROM dbo.HomeSliderImages WHERE Id = @Id", new { Id = id });
            return rows > 0;
        }

        public async Task<bool> ToggleSliderImageAsync(int id)
        {
            using var conn = new SqlConnection(_connectionString);
            int rows = await conn.ExecuteAsync("UPDATE dbo.HomeSliderImages SET IsActive = CASE WHEN IsActive = 1 THEN 0 ELSE 1 END WHERE Id = @Id", new { Id = id });
            return rows > 0;
        }

        public async Task<List<HomeSection>> GetSectionsAsync(bool visibleOnly = false)
        {
            await EnsureTablesCreatedAsync();
            using var conn = new SqlConnection(_connectionString);
            string sql = visibleOnly
                ? "SELECT * FROM dbo.HomeSections WHERE IsVisible = 1 ORDER BY DisplayOrder ASC, Id ASC"
                : "SELECT * FROM dbo.HomeSections ORDER BY DisplayOrder ASC, Id ASC";
            return (await conn.QueryAsync<HomeSection>(sql)).ToList();
        }

        public async Task<bool> SaveSectionAsync(HomeSection section, IFormFile imageFile = null, string webRootPath = null)
        {
            await EnsureTablesCreatedAsync();

            if (imageFile != null && imageFile.Length > 0 && !string.IsNullOrEmpty(webRootPath))
            {
                string uploadsFolder = Path.Combine(webRootPath, "uploads", "home");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                string fileExt = Path.GetExtension(imageFile.FileName);
                string uniqueFileName = $"sec_{Guid.NewGuid():N}{fileExt}";
                string filePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(stream);
                }

                section.ImageUrl = $"/uploads/home/{uniqueFileName}";
            }

            using var conn = new SqlConnection(_connectionString);

            if (section.Id > 0)
            {
                string updateSql = @"
                    UPDATE dbo.HomeSections 
                    SET Title = @Title, Subtitle = @Subtitle, ContentHtml = @ContentHtml, 
                        Icon = @Icon, ImageUrl = ISNULL(NULLIF(@ImageUrl, ''), ImageUrl), IsVisible = @IsVisible
                    WHERE Id = @Id";
                return (await conn.ExecuteAsync(updateSql, section)) > 0;
            }
            else
            {
                int maxOrder = await conn.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(DisplayOrder), 0) FROM dbo.HomeSections");
                section.DisplayOrder = maxOrder + 1;
                if (string.IsNullOrEmpty(section.SectionKey)) section.SectionKey = $"custom_{Guid.NewGuid():N}";

                string insertSql = @"
                    INSERT INTO dbo.HomeSections (SectionKey, Title, Subtitle, ContentHtml, Icon, ImageUrl, DisplayOrder, IsVisible)
                    VALUES (@SectionKey, @Title, @Subtitle, @ContentHtml, @Icon, @ImageUrl, @DisplayOrder, @IsVisible)";
                return (await conn.ExecuteAsync(insertSql, section)) > 0;
            }
        }

        public async Task<bool> DeleteSectionAsync(int id)
        {
            using var conn = new SqlConnection(_connectionString);
            int rows = await conn.ExecuteAsync("DELETE FROM dbo.HomeSections WHERE Id = @Id", new { Id = id });
            return rows > 0;
        }

        public async Task<bool> ToggleSectionVisibilityAsync(int id)
        {
            using var conn = new SqlConnection(_connectionString);
            int rows = await conn.ExecuteAsync("UPDATE dbo.HomeSections SET IsVisible = CASE WHEN IsVisible = 1 THEN 0 ELSE 1 END WHERE Id = @Id", new { Id = id });
            return rows > 0;
        }

        public async Task<List<HomeFacultyMember>> GetFacultyMembersAsync(bool activeOnly = false)
        {
            await EnsureTablesCreatedAsync();
            using var conn = new SqlConnection(_connectionString);
            string sql = activeOnly
                ? "SELECT * FROM dbo.HomeFacultyMembers WHERE IsActive = 1 ORDER BY DisplayOrder ASC, Id ASC"
                : "SELECT * FROM dbo.HomeFacultyMembers ORDER BY DisplayOrder ASC, Id ASC";
            return (await conn.QueryAsync<HomeFacultyMember>(sql)).ToList();
        }

        public async Task<bool> SaveFacultyMemberAsync(HomeFacultyMember faculty, IFormFile imageFile = null, string webRootPath = null)
        {
            await EnsureTablesCreatedAsync();

            if (imageFile != null && imageFile.Length > 0 && !string.IsNullOrEmpty(webRootPath))
            {
                string uploadsFolder = Path.Combine(webRootPath, "uploads", "home");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                string fileExt = Path.GetExtension(imageFile.FileName);
                string uniqueFileName = $"faculty_{Guid.NewGuid():N}{fileExt}";
                string filePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(stream);
                }

                faculty.ImageUrl = $"/uploads/home/{uniqueFileName}";
            }

            using var conn = new SqlConnection(_connectionString);

            if (faculty.Id > 0)
            {
                string updateSql = @"
                    UPDATE dbo.HomeFacultyMembers 
                    SET Name = @Name, RoleTitle = @RoleTitle, Bio = @Bio, 
                        ImageUrl = ISNULL(NULLIF(@ImageUrl, ''), ImageUrl), IsActive = @IsActive
                    WHERE Id = @Id";
                return (await conn.ExecuteAsync(updateSql, faculty)) > 0;
            }
            else
            {
                int maxOrder = await conn.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(DisplayOrder), 0) FROM dbo.HomeFacultyMembers");
                faculty.DisplayOrder = maxOrder + 1;

                string insertSql = @"
                    INSERT INTO dbo.HomeFacultyMembers (Name, RoleTitle, Bio, ImageUrl, DisplayOrder, IsActive, CreatedAt)
                    VALUES (@Name, @RoleTitle, @Bio, @ImageUrl, @DisplayOrder, @IsActive, GETDATE())";
                return (await conn.ExecuteAsync(insertSql, faculty)) > 0;
            }
        }

        public async Task<bool> DeleteFacultyMemberAsync(int id, string webRootPath = null)
        {
            using var conn = new SqlConnection(_connectionString);
            var item = await conn.QueryFirstOrDefaultAsync<HomeFacultyMember>("SELECT * FROM dbo.HomeFacultyMembers WHERE Id = @Id", new { Id = id });
            if (item == null) return false;

            if (!string.IsNullOrEmpty(webRootPath) && !string.IsNullOrEmpty(item.ImageUrl) && item.ImageUrl.StartsWith("/uploads/"))
            {
                string fullPath = Path.Combine(webRootPath, item.ImageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    try { File.Delete(fullPath); } catch { }
                }
            }

            int rows = await conn.ExecuteAsync("DELETE FROM dbo.HomeFacultyMembers WHERE Id = @Id", new { Id = id });
            return rows > 0;
        }

        public async Task<bool> ToggleFacultyActiveAsync(int id)
        {
            using var conn = new SqlConnection(_connectionString);
            int rows = await conn.ExecuteAsync("UPDATE dbo.HomeFacultyMembers SET IsActive = CASE WHEN IsActive = 1 THEN 0 ELSE 1 END WHERE Id = @Id", new { Id = id });
            return rows > 0;
        }
    }
}
