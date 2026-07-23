// Пример 3: автоматизация — обход файлов рабочей папки, поиск строк TODO
// и запись отчёта в Markdown. Образец скрипта, который что-то ДЕЛАЕТ с файлами.
// Запуск из рабочей папки: dotnet run scripts\todo-report.cs -- .
//   (первый аргумент — папка для сканирования; по умолчанию текущая)

using System.Text;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
string[] extensions = [".cs", ".md", ".txt", ".json", ".xml", ".csproj", ".ps1"];

var report = new StringBuilder();
report.AppendLine("# Отчёт по TODO");
report.AppendLine();
report.AppendLine($"Папка: `{root}`  ");
report.AppendLine($"Дата: {DateTime.Now:yyyy-MM-dd HH:mm}");
report.AppendLine();

int count = 0;
foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
{
    if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
    {
        continue;
    }

    string[] lines;
    try { lines = File.ReadAllLines(file); }
    catch { continue; } // пропускаем нечитаемые файлы

    for (int i = 0; i < lines.Length; i++)
    {
        if (lines[i].Contains("TODO", StringComparison.OrdinalIgnoreCase))
        {
            report.AppendLine($"- `{Path.GetRelativePath(root, file)}:{i + 1}` — {lines[i].Trim()}");
            count++;
        }
    }
}

report.AppendLine();
report.AppendLine($"**Всего найдено: {count}**");

var output = Path.Combine(root, "todo-report.md");
File.WriteAllText(output, report.ToString(), Encoding.UTF8);
Console.WriteLine($"Найдено TODO: {count}. Отчёт записан: {output}");
