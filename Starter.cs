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
                return 0; // no idea how to get them for now, wmic seems to be outdated
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
        // just pretend something is here
#endif
    }
}

