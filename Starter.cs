namespace SharpMIDI
{
    internal static class Starter
    {
        static void Main()
        {
            WindowManager.StartRenderer();
            MIDILoader.UnloadMIDI();
        }
        public static string toMemoryText(long bytes)
        {
            return bytes switch
            {
                var expression when bytes < 1024 => $"{bytes} B",
                var expression when bytes < 1048576 => $"{bytes / 1024} KiB",
                _ => $"{bytes / 1048576} MiB",
            };
        }
    }
}
