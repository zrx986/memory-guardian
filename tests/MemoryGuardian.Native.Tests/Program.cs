using MemoryGuardian.App;

try
{
    var sample = WindowsMemory.Read();
    if (sample.TotalBytes < 1024UL * 1024 * 1024) throw new Exception("FAIL: Windows total physical memory was not read");
    if (sample.AvailableBytes > sample.TotalBytes) throw new Exception("FAIL: available memory exceeds total");
    if (sample.CommitBytes == 0) throw new Exception("FAIL: system commit was not read");
    if (sample.UsedPercent < 0 || sample.UsedPercent > 100) throw new Exception("FAIL: invalid memory percentage");
    if (!WindowsMemory.IsAdministrator())
    {
        var attempt = WindowsMemory.TrimWorkingSets();
        if (attempt.Success || attempt.Win32Error != 1314) throw new Exception("FAIL: unelevated cleanup was not rejected before calling the kernel");
    }
    var startupEnabled = StartupRegistration.IsEnabled();
    Console.WriteLine("PASS: native physical memory, available bounds, commit, percentage, privilege guard, startup query");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(sample));
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine(e.ToString());
    return 1;
}
