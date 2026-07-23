// Пример 1: базовый dotnet-скрипт (file-based программа .NET 10).
// Один .cs-файл с top-level statements, без csproj.
// Запуск из рабочей папки: dotnet run scripts\hello.cs -- Иван

var name = args.Length > 0 ? args[0] : "мир";
Console.WriteLine($"Привет, {name}! Сейчас {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
Console.WriteLine($"Аргументов получено: {args.Length}");
