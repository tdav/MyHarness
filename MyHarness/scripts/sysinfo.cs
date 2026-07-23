// Пример 2: сведения о системе и дисках.
// Показывает, что скриптам доступен весь BCL (Environment, DriveInfo, LINQ).
// Запуск из рабочей папки: dotnet run scripts\sysinfo.cs

Console.WriteLine("=== Система ===");
Console.WriteLine($"ОС:            {Environment.OSVersion}");
Console.WriteLine($".NET:          {Environment.Version}");
Console.WriteLine($"Машина:        {Environment.MachineName}");
Console.WriteLine($"Пользователь:  {Environment.UserName}");
Console.WriteLine($"Процессоры:    {Environment.ProcessorCount}");
Console.WriteLine($"Текущая папка: {Environment.CurrentDirectory}");

Console.WriteLine();
Console.WriteLine("=== Диски ===");
foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
{
    double totalGb = drive.TotalSize / 1_073_741_824.0;
    double freeGb = drive.AvailableFreeSpace / 1_073_741_824.0;
    Console.WriteLine($"{drive.Name}  всего {totalGb:F1} ГБ, свободно {freeGb:F1} ГБ ({drive.DriveFormat})");
}
