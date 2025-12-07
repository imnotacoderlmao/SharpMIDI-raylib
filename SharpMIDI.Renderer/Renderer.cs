using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static class MIDIRenderer
    {
        public static bool ready => NoteProcessor.IsReady;
        public static bool run => WindowManager.IsRunning;
        /// <summary>
        /// Starts the rendering window and main loop
        /// </summary>
        public static void StartRenderer()
        {
            WindowManager.StartRenderer();
        }

        /// <summary>
        /// Cleans up note data but keeps renderer running
        /// </summary>
        public static void Cleanup()
        {
            NoteProcessor.Cleanup();
            NoteRenderer.Cleanup();
        }
    }
}