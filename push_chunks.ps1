Write-Host "Initializing Git..."
git init
git branch -M main
git remote add origin https://github.com/omarreda1234/system-exam.git
git config http.postBuffer 524288000
git config http.version HTTP/1.1

# Stage 1: Config and Scripts
Write-Host "Stage 1: Pushing config and script files..."
git add .gitignore .github/ cols.txt powerbi_views.sql powerbi_views_aggregates.sql fix_buttons.ps1
git commit -m "Stage 1: Config and workflows"
git push -u origin main --force

# Stage 2: Code (Controllers, Models, Services, etc.)
Write-Host "Stage 2: Pushing backend C# code..."
git add exam/Exam/Exam/Controllers/
git add exam/Exam/Exam/Models/
git add exam/Exam/Exam/Services/
git add exam/Exam/Exam/DTOs/
git add exam/Exam/Exam/Data/
git add exam/Exam/Exam/Hubs/
git add exam/Exam/Exam/Middlewares/
git add exam/Exam/Exam/Migrations/
git add exam/Exam/Exam/MyContext/
git add exam/Exam/Exam/Program.cs
git add exam/Exam/Exam/Exam.csproj
git add exam/Exam/Exam/appsettings.json
git add exam/Exam/Exam/appsettings.Production.json
git add exam/Exam/Exam/appsettings.Development.json
git add exam/Exam/Exam/bundleconfig.json
git add exam/Exam/Exam.slnx
git add exam/update_add_category.sql exam/update_categories.sql
git commit -m "Stage 2: C# Source Code"
git push origin main

# Stage 3: Views & Pages
Write-Host "Stage 3: Pushing Views and Pages..."
git add exam/Exam/Exam/Views/
git add exam/Exam/Exam/Pages/
git add exam/Exam/Pages/
git commit -m "Stage 3: Views and Pages"
git push origin main

# Stage 4: Static assets (wwwroot except images)
Write-Host "Stage 4: Pushing static assets (css, js, libs)..."
git add exam/Exam/Exam/wwwroot/css/
git add exam/Exam/Exam/wwwroot/js/
git add exam/Exam/Exam/wwwroot/lib/
git commit -m "Stage 4: Static Assets"
git push origin main

# Stage 5: Images and SQL files
Write-Host "Stage 5: Pushing images and sql scripts..."
git add exam/Exam/Exam/wwwroot/images/
git add exam/Exam/*.sql
git add exam/Exam/*.txt
git add exam/Exam/*.cs
git add exam/Exam/*.ps1
git add exam/Exam/*.py
git commit -m "Stage 5: Images and database scripts"
git push origin main

Write-Host "All stages pushed successfully!"
