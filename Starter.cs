using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SharpMIDI
{
    internal static class Starter
    {
        static void Main()
        {
            WindowManager.StartRenderer();
        }
        public static string toMemoryText(long bytes)
        {
            return bytes switch
            {
                var _ when bytes < 1024 => $"{bytes} Bytes",
                var _ when bytes < 1048576 => $"{bytes / 1024:N0} KiB",
                _ => $"{bytes / 1048576:N0} MiB",
            };
        }
        
    }
    public static class RamReader
    {
        public static ulong GetTotalMemoryInBytes()
        {
            #if WINDOWS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetWindowsMemory();
            }
            #endif
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return GetLinuxMemory();
            }
            return 0;
        }

        private static ulong GetLinuxMemory()
        {
            if (File.Exists("/proc/meminfo"))
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 1 && ulong.TryParse(parts[1], out ulong kb))
                        {
                            return kb * 1024; // Convert to bytes
                        }
                    }
                }
            }
            return 0;
        }
        #if WINDOWS
        private static ulong GetWindowsMemory()
        {
            var output = RunCommand("wmic", "ComputerSystem get TotalPhysicalMemory");
            var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 1 && ulong.TryParse(lines[1].Trim(), out ulong bytes))
            {
                return bytes;
            }
            return 0;
        }
        private static string RunCommand(string filename, string arguments)
        {
            using var process = new Process();
            process.StartInfo.FileName = filename;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            string result = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return result;
        }
        #endif
    }
}

