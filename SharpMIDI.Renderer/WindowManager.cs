using Raylib_cs;

namespace SharpMIDI.Renderer
{
    public static class WindowManager
    {
        public const int PAD = 20;
        
        private static int currentWidth = 1280;
        private static int currentHeight = 720;
        private static float scrollFactor = 1f;
        private static bool vsync = true;
        private static bool controlsVisible = true;
        private static bool dynamicScroll = false;

        private static readonly System.Text.StringBuilder uiText = new(512);

        public static bool Debug { get; set; }
        public static bool IsRunning { get; private set; }

        public static void StartRenderer()
        {
            if (IsRunning) return;
            IsRunning = true;
            RenderLoop();
        }

        private static void RenderLoop()
        {
            Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
            Raylib.InitWindow(currentWidth, currentHeight, "SharpMIDI");

            int targetFPS = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
            Raylib.SetTargetFPS(vsync ? targetFPS : 0);
            while (IsRunning)
            {
                CheckResize();
                HandleInput();

                int tick = (int)MIDIClock.tick;
                
                if (MIDIPlayer.stopping)
                {
                    tick = 0;
                }

                if (dynamicScroll && MIDIRenderer.WindowTicks != MIDIClock.tickscale * scrollFactor)
                    MIDIRenderer.SetWindow((float)MIDIClock.tickscale * scrollFactor);

                MIDIRenderer.UpdateStreaming(tick);

                Raylib.BeginDrawing();
                Raylib.ClearBackground(Raylib_cs.Color.Black);
                MIDIRenderer.Render(currentWidth, currentHeight, PAD);
                Raylib.DrawLine(currentWidth >> 1, 0, currentWidth >> 1, currentHeight, Raylib_cs.Color.Red);
                DrawUI(tick);
                Raylib.EndDrawing();
            }
            IsRunning = false;
            Raylib.CloseWindow();
        }

        private static void CheckResize()
        {
            int w = Raylib.GetScreenWidth();
            int h = Raylib.GetScreenHeight();

            if (w != currentWidth)
            {
                currentWidth = w;
                currentHeight = h;
                MIDIRenderer.Initialize(currentWidth);
            }
        }

        private static void HandleInput()
        {
            bool up = Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressedRepeat(KeyboardKey.Up);
            bool down = Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressedRepeat(KeyboardKey.Down);

            if (up)
            {
                if (dynamicScroll)
                    scrollFactor = scrollFactor <= 1 ? scrollFactor / 2 : scrollFactor - 0.5f;
                
                float newWindow = Math.Max(100f, MIDIRenderer.WindowTicks * 0.9f);
                MIDIRenderer.SetWindow(newWindow);
            }

            if (down)
            {
                if (dynamicScroll)
                    scrollFactor = scrollFactor <= 1 ? scrollFactor * 2 : scrollFactor + 0.5f;
                
                float newWindow = Math.Min(100000f, MIDIRenderer.WindowTicks * 1.1f);
                MIDIRenderer.SetWindow(newWindow);
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressedRepeat(KeyboardKey.Right))
            {
                MIDIClock.tick += MIDIClock.tickscale;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.S)) dynamicScroll = !dynamicScroll;
            if (Raylib.IsKeyPressed(KeyboardKey.D)) Debug = !Debug;
            if (Raylib.IsKeyPressed(KeyboardKey.C)) controlsVisible = !controlsVisible;
            if (Raylib.IsKeyPressed(KeyboardKey.F)) Raylib.ToggleBorderlessWindowed();
            if (Raylib.IsKeyPressed(KeyboardKey.Escape)) IsRunning = false;

            if (Raylib.IsKeyPressed(KeyboardKey.V))
            {
                vsync = !vsync;
                Raylib.SetTargetFPS(vsync ? Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor()) : 0);
            }
        }

        private static void DrawUI(int tick)
        {
            // Main status line
            uiText.Clear();
            uiText.Append("Tick: ").Append(tick)
                  .Append(" | Tempo: ").Append(MIDIClock.bpm.ToString("F1"))
                  .Append(" | Zoom: ").Append((int)MIDIRenderer.WindowTicks)
                  .Append(" | FPS: ").Append(Raylib.GetFPS());
            Raylib.DrawText(uiText.ToString(), 12, 4, 16, Raylib_cs.Color.Green);

            // Debug info
            if (Debug)
            {
                uiText.Clear();
                uiText.Append("DrawCalls: ").Append(MIDIRenderer.NotesDrawnLastFrame)
                      .Append(" | Mem: ").Append(Form1.toMemoryText(GC.GetTotalMemory(false)))
                      .Append(" | DynaScroll: ").Append(dynamicScroll ? $"({scrollFactor}x)" : "Off");
                Raylib.DrawText(uiText.ToString(), 12, 25, 16, Raylib_cs.Color.SkyBlue);
            }

            // Controls help
            if (controlsVisible)
            {
                Raylib.DrawText("Up/Dn=zoom | V=vsync | Right=seek | C=hide | F=fullscreen | D=debug | S=dynascroll",
                               12, 45, 16, Raylib_cs.Color.White);
                if (Raylib.GetTime() >= 4.0 && Raylib.GetTime() <= 4.5)
                    controlsVisible = false;
            }

            // Status line
            string status = MIDILoader.loaded? Starter.filename : "No MIDI loaded";
            var color = MIDILoader.loaded? Raylib_cs.Color.SkyBlue : Raylib_cs.Color.Yellow;
            Raylib.DrawText(status, 12, currentHeight - 19, 16, color);
        }

        public static void StopRenderer() => IsRunning = false;
    }
}