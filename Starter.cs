namespace SharpMIDI
{
    internal static class Starter
    {
        [STAThread]
        static void Main()
        {
            Renderer.WindowManager.StartRenderer();
            MIDILoader.UnloadMIDI();
        }
    }
}
