using System.Drawing.Printing;
using Raylib_cs;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static class WindowManager
    {
        public const int PAD = 22;

        // Dynamic window dimensions
        private static int currentWidth = 1280;
        private static int currentHeight = 720;

        // Pre-allocated buffers for UI strings
        private static readonly System.Text.StringBuilder tickStr = new(256);
        private static readonly System.Text.StringBuilder debugStr = new(128);

        // Window state
        private static bool vsync = true, controls = true;
        public static bool Debug { get; set; } = false;
        public static bool IsRunning { get; private set; } = false;

        public static void StartRenderer()
        {
            if (IsRunning) return;
            IsRunning = true;
            Task.Run(RenderLoop);
        }

        private static void RenderLoop()
        {
            Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
            Raylib.InitWindow(currentWidth, currentHeight, "SharpMIDI");

            // Initialize streaming texture renderer
            NoteRenderer.Initialize(currentWidth, currentHeight);

            int targetFPS = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
            Raylib.SetTargetFPS(vsync ? targetFPS : 0);

            while (!Raylib.WindowShouldClose() && IsRunning)
            {
                UpdateWindowDimensions();
                HandleInput();
                
                float tick = (float)MIDIClock.GetTick();

                // Update the streaming texture (this is where the magic happens!)
                NoteRenderer.UpdateStreaming(tick);
                
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Raylib_cs.Color.Black);

                // Render the streaming texture to screen
                NoteRenderer.Render(currentWidth, currentHeight, PAD);

                // Draw center line
                Raylib.DrawLine(currentWidth >> 1, 0, currentWidth >> 1, currentHeight, Raylib_cs.Color.Red);

                DrawUI(tick);

                Raylib.EndDrawing();
            }

            Raylib.CloseWindow();
            IsRunning = false;
        }

        private static void UpdateWindowDimensions()
        {
            int newWidth = Raylib.GetScreenWidth();
            int newHeight = Raylib.GetScreenHeight();

            if (newWidth != currentWidth || newHeight != currentHeight)
            {
                currentWidth = newWidth;
                currentHeight = newHeight;

                // Reinitialize streaming renderer with new dimensions
                NoteRenderer.Initialize(currentWidth, currentHeight);
            }
        }

        private static void HandleInput()
        {
            // Zoom controls
            if (Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressedRepeat(KeyboardKey.Up))
            {
                float newWindow = Math.Max(100f, NoteRenderer.Window * 0.9f);
                NoteRenderer.SetWindow(newWindow);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressedRepeat(KeyboardKey.Down))
            {
                float newWindow = Math.Min(100000f, NoteRenderer.Window * 1.111111f);
                NoteRenderer.SetWindow(newWindow);
            }

            // Seeking controls
            if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressedRepeat(KeyboardKey.Right))
                MIDIClock.time += MIDIClock.ppq;
            if (Raylib.IsKeyPressed(KeyboardKey.W) || Raylib.IsKeyPressedRepeat(KeyboardKey.W))
                MIDIClock.time += Math.Round(1 / MIDIClock.ticklen, 5);

            // Toggle controls
            /*if (Raylib.IsKeyPressed(KeyboardKey.G))
                NoteRenderer.EnableGlow = !NoteRenderer.EnableGlow;*/
            if (Raylib.IsKeyPressed(KeyboardKey.D))
                Debug = !Debug;
            if (Raylib.IsKeyPressed(KeyboardKey.V))
            {
                vsync = !vsync;
                Raylib.SetTargetFPS(vsync ? Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor()) : 0);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.F))
                Raylib.ToggleBorderlessWindowed();
            if (Raylib.IsKeyPressed(KeyboardKey.C))
                controls = !controls;
            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
                IsRunning = false;
        }

        private static void DrawUI(float tick)
        {
            lock (NoteProcessor.ReadyLock)
            {
                // Main UI
                tickStr.Clear();
                tickStr.Append("Tick: ").Append((int)tick)
                       .Append(" | Tempo: ").Append(MIDIPlayer.bpm.ToString("F1"))
                       .Append(" | Zoom: ").Append((int)NoteRenderer.Window)
                       /*.Append(" | Glow: ").Append(NoteRenderer.EnableGlow ? "ON" : "OFF")*/
                       .Append(" | Glow: ").Append("broken")
                       .Append(" | FPS: ").Append(Raylib.GetFPS());

                Raylib.DrawText(tickStr.ToString(), 10, 5, 16, Raylib_cs.Color.Green);

                if (Debug)
                {
                    debugStr.Clear();
                    debugStr.Append("Drawcalls?: ").Append(NoteRenderer.RenderedColumns)
                            .Append(" | Memory: ").Append(Form1.toMemoryText(GC.GetTotalMemory(false)));
                    Raylib.DrawText(debugStr.ToString(), 10, 25, 16, Raylib_cs.Color.SkyBlue);
                }

                if (controls)
                {
                    Raylib.DrawText($"Up/Dn = Zoom | G = Glow | V = Vsync | Right = Seek fwd | W = skip further | C = toggle this text | F = Fullscreen | D = Debug", 10, 45, 16, Raylib_cs.Color.White);
                    if (Raylib.GetTime() >= 4.0 && Raylib.GetTime() <= 4.5)
                        controls = false;
                }

                if (!NoteProcessor.IsReady)
                    Raylib.DrawText("No MIDI loaded.", 10, currentHeight - 20, 16, Raylib_cs.Color.Yellow);
                else
                    Raylib.DrawText($"{Starter.filename ?? "Unknown"}", 10, currentHeight - 20, 16, Raylib_cs.Color.SkyBlue);
            }
        }

        public static void StopRenderer() => IsRunning = false;

        public static void Shutdown()
        {
            IsRunning = false;
            NoteProcessor.Shutdown();
            NoteRenderer.Shutdown();
        }
    }
}