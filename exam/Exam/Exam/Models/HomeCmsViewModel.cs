using System;
using System.Collections.Generic;

namespace Exam.Models
{
    public class HomeCmsViewModel
    {
        public List<HomeSliderImage> SliderImages { get; set; } = new();
        public Dictionary<string, HomeSection> Sections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<HomeSection> CustomSections { get; set; } = new();
        public List<HomeFacultyMember> FacultyMembers { get; set; } = new();
    }

    public class HomeSliderImage
    {
        public int Id { get; set; }
        public string ImageUrl { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; }
    }

    public class HomeSection
    {
        public int Id { get; set; }
        public string SectionKey { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string ContentHtml { get; set; }
        public string Icon { get; set; }
        public string ImageUrl { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsVisible { get; set; }
    }

    public class HomeFacultyMember
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string RoleTitle { get; set; }
        public string Bio { get; set; }
        public string ImageUrl { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; }
    }
}
