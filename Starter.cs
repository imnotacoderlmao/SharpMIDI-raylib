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
                var _ when bytes < 1024 => $"{bytes} B",
                var _ when bytes < 1048576 => $"{bytes / 1024} KiB",
                _ => $"{bytes / 1048576} MiB",
            };
        }
    }
}
