using PdfSharpCore.Fonts;
using System.IO;
using System.Reflection;

namespace Exam.Services
{
    public class FileFontResolver : IFontResolver
    {
        public string DefaultFontName => "Arial";

        public byte[] GetFont(string faceName)
        {
            var folder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "fonts");
            string fontPath = faceName switch
            {
                "Great Vibes" => Path.Combine(folder, "GreatVibes-Regular.ttf"),
                "Arial" => Path.Combine(folder, "Arial.ttf"),
                _ => Path.Combine(folder, "Arial.ttf")
            };

            if (File.Exists(fontPath))
                return File.ReadAllBytes(fontPath);

            return null;
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            if (familyName.Equals("Great Vibes", System.StringComparison.OrdinalIgnoreCase))
            {
                return new FontResolverInfo("Great Vibes");
            }

            return new FontResolverInfo("Arial");
        }
    }
}
