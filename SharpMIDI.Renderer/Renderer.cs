using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static class StreamlinedRenderer
    {
        // Legacy compatibility properties and methods
        public static bool debug 
        { 
            get => WindowManager.Debug; 
            set => WindowManager.Debug = value; 
        }
        
        public static bool ready => NoteProcessor.IsReady;
        public static bool run => WindowManager.IsRunning;

        /// <summary>
        /// Processes MIDI tracks and prepares notes for rendering
        /// </summary>
        public static void EnhanceTracksForRendering()
        {
            NoteProcessor.EnhanceTracksForRendering();
        }

        /// <summary>
        /// Starts the rendering window and main loop
        /// </summary>
        public static void StartRenderer()
        {
            WindowManager.StartRenderer();
        }

        /// <summary>
        /// Stops the renderer
        /// </summary>
        public static void StopRenderer()
        {
            WindowManager.StopRenderer();
        }

        /// <summary>
        /// Cleans up note data but keeps renderer running
        /// </summary>
        public static void Cleanup()
        {
            NoteProcessor.Cleanup();
        }

        /// <summary>
        /// Complete shutdown of all renderer components
        /// </summary>
        public static void Shutdown()
        {
            WindowManager.Shutdown();
        }
    }
}