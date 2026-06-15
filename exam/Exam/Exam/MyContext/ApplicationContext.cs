using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Exam.DTOs;
using Exam.Models;

namespace Exam.MyContext
{
    public class ApplicationContext:IdentityDbContext<ApplicationUser>
    {
        public ApplicationContext()
        {
            
        }
        public ApplicationContext(DbContextOptions<ApplicationContext> options):base(options)
        {
            
        }
        public DbSet<Exam.DTOs.LoginDTO> LoginDTO { get; set; } = default!;
        public DbSet<Exam.DTOs.RegisterDTO> RegisterDTO { get; set; } = default!;
       // public DbSet<ApplicationUser> applicationUsers { get; set; } 


    }
}
