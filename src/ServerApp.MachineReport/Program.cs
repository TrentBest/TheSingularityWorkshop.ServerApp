using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private static int Main()
    {
        var report = BuildReport();
        Console.WriteLine(report);
        Console.WriteLine();

        if (TryCopyToClipboard(report))
        {
            Console.WriteLine("Machine report copied to the clipboard. Paste it into the ServerApp conversation.");
        }
        else
        {
            Console.WriteLine("Clipboard access was unavailable. Select and copy the report above manually.");
        }

        Console.WriteLine("Review the report before sharing it. It includes your computer name and private LAN IP addresses.");
        return 0;
    }

    private static string BuildReport()
    {
        var report = new StringBuilder();
        report.AppendLine("THE SINGULARITY WORKSHOP — SERVERAPP MACHINE REPORT");
        report.AppendLine($"Generated (UTC): {DateTimeOffset.UtcNow:O}");
        report.AppendLine();

        Section(report, "SYSTEM");
        report.AppendLine($"Computer name: {Environment.MachineName}");
        report.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        report.AppendLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
        report.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        report.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        report.AppendLine($"Environment version: {Environment.Version}");
        report.AppendLine($"Logical processors visible to process: {Environment.ProcessorCount}");
        report.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        report.AppendLine($"64-bit process: {Environment.Is64BitProcess}");

        Section(report, "CPU");
        var cpu = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? RunCapture("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                "Get-CimInstance Win32_Processor | ForEach-Object { \"$($_.Name) | cores=$($_.NumberOfCores) | logical=$($_.NumberOfLogicalProcessors)\" }")
            : ReadCpuModel();
        report.AppendLine(string.IsNullOrWhiteSpace(cpu) ? "CPU model: unavailable" : cpu.Trim());

        Section(report, "PHYSICAL MEMORY");
        var memory = GetPhysicalMemory();
        report.AppendLine(memory ?? "Physical memory: unavailable");

        Section(report, "GRAPHICS");
        var graphics = GetGraphics();
        report.AppendLine(string.IsNullOrWhiteSpace(graphics) ? "Graphics adapter details: unavailable" : graphics.Trim());

        Section(report, ".NET SDKs");
        report.AppendLine(FormatCommand("dotnet", "--list-sdks"));
        Section(report, ".NET RUNTIMES");
        report.AppendLine(FormatCommand("dotnet", "--list-runtimes"));

        Section(report, "STORAGE");
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                report.AppendLine($"{drive.Name} | {drive.DriveFormat} | total {FormatBytes(drive.TotalSize)} | free {FormatBytes(drive.AvailableFreeSpace)}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"Storage query unavailable: {ex.GetType().Name}");
        }

        Section(report, "ACTIVE NETWORK INTERFACES");
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToArray();

            if (interfaces.Length == 0)
                report.AppendLine("No active non-loopback network interfaces found.");

            foreach (var adapter in interfaces)
            {
                report.AppendLine($"{adapter.Name} | {adapter.NetworkInterfaceType} | {adapter.Speed / 1_000_000} Mbps");
                foreach (var address in adapter.GetIPProperties().UnicastAddresses
                             .Select(a => a.Address)
                             .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)))
                {
                    report.AppendLine($"  IPv4: {address}");
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"Network query unavailable: {ex.GetType().Name}");
        }

        Section(report, "SERVERAPP COMPATIBILITY CHECK");
        report.AppendLine("Current ServerApp projects target .NET 8.0.");
        report.AppendLine("Building from source requires a compatible .NET 8 SDK.");
        report.AppendLine("Running framework-dependent .NET 8 builds requires the .NET 8 runtime.");
        report.AppendLine("FSM_API's current project file targets net8.0, net6.0, netcoreapp3.1, netstandard2.1, netstandard2.0, and net47.");
        report.AppendLine("This report gathers machine facts; it does not install software or change system settings.");

        Section(report, "PRIVACY");
        report.AppendLine("No usernames, passwords, tokens, serial numbers, MAC addresses, file listings, or public-IP lookups are collected.");
        report.AppendLine("Computer name and private LAN IPv4 addresses are included to help plan a local desktop-to-desktop connection.");

        return report.ToString();
    }

    private static void Section(StringBuilder builder, string title)
    {
        builder.AppendLine();
        builder.AppendLine($"--- {title} ---");
    }

    private static string ReadCpuModel()
    {
        if (File.Exists("/proc/cpuinfo"))
        {
            var line = File.ReadLines("/proc/cpuinfo")
                .FirstOrDefault(l => l.StartsWith("model name", StringComparison.OrdinalIgnoreCase) ||
                                     l.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase));
            if (line is not null)
                return line[(line.IndexOf(':') + 1)..].Trim();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return RunCapture("sysctl", "-n", "machdep.cpu.brand_string");

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty;
    }

    private static string? GetPhysicalMemory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RunCapture("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                "Get-CimInstance Win32_ComputerSystem | ForEach-Object { \"$([math]::Round($_.TotalPhysicalMemory / 1GB, 2)) GiB\" }");

        if (File.Exists("/proc/meminfo"))
        {
            var line = File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
            if (line is not null && long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var kib))
                return $"{kib / 1024d / 1024d:F2} GiB";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var bytes = RunCapture("sysctl", "-n", "hw.memsize").Trim();
            if (long.TryParse(bytes, out var value))
                return $"{value / 1024d / 1024d / 1024d:F2} GiB";
        }

        return null;
    }

    private static string GetGraphics()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RunCapture("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                "Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name }");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return RunCapture("system_profiler", "SPDisplaysDataType");

        return RunCapture("sh", "-c", "lspci 2>/dev/null | grep -Ei 'vga|3d|display' || true");
    }

    private static string FormatCommand(string fileName, params string[] args)
    {
        var output = RunCapture(fileName, args);
        return string.IsNullOrWhiteSpace(output) ? "Unavailable (dotnet command not found or returned no output)." : output.Trim();
    }

    private static string RunCapture(string fileName, params string[] args)
    {
        try
        {
            using var process = new Process { StartInfo = CreateStartInfo(fileName, args, redirectInput: false) };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                process.Kill(entireProcessTree: true);
                return "Query timed out.";
            }
            return process.ExitCode == 0 ? output : string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryCopyToClipboard(string value)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return TryWriteClipboard("powershell.exe", value, "-NoProfile", "-NonInteractive", "-STA", "-Command",
                "$text = [Console]::In.ReadToEnd(); Set-Clipboard -Value $text");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return TryWriteClipboard("pbcopy", value);

        return TryWriteClipboard("wl-copy", value) || TryWriteClipboard("xclip", value, "-selection", "clipboard");
    }

    private static bool TryWriteClipboard(string fileName, string value, params string[] args)
    {
        try
        {
            using var process = new Process { StartInfo = CreateStartInfo(fileName, args, redirectInput: true) };
            process.Start();
            process.StandardInput.Write(value);
            process.StandardInput.Close();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string[] args, bool redirectInput)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            CreateNoWindow = true
        };

        foreach (var argument in args)
            info.ArgumentList.Add(argument);

        return info;
    }

    private static string FormatBytes(long bytes) =>
        $"{bytes / 1024d / 1024d / 1024d:F1} GiB";
}
