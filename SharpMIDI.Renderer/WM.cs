using Raylib_cs;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static class WindowManager
    {
        private const int PAD = 25;
        
        // Dynamic window dimensions that update with resize
        private static int currentWidth = 1280;
        private static int currentHeight = 720;
        
        // Pre-allocated buffers for UI strings to reduce GC pressure
        private static readonly System.Text.StringBuilder tickStr = new(256);
        private static readonly System.Text.StringBuilder debugStr = new(128);

        // Window state
        private static bool vsync = true, controls = true; // controls on by default as a splash text
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
            // Set resizable flag before window creation
            Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
            Raylib.InitWindow(currentWidth, currentHeight, "SharpMIDI");
            
            // Initialize renderer components
            NoteRenderer.Initialize(currentHeight, PAD);
            
            // Optimize for performance
            int targetFPS = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
            Raylib.SetTargetFPS(vsync ? targetFPS : 0);

            while (!Raylib.WindowShouldClose() && IsRunning)
            {
                // Update window dimensions if changed
                UpdateWindowDimensions();
                
                HandleInput();
                float tick = (float)MIDIClock.GetTick();
                int rectCount = NoteRenderer.BuildVisibleRectangles(tick, currentWidth, currentHeight, PAD);

                Raylib.BeginDrawing();
                Raylib.ClearBackground(Raylib_cs.Color.Black);
                
                NoteRenderer.DrawRectangles(rectCount);
                // Draw center line
                Raylib.DrawLine(currentWidth >> 1, 0, currentWidth >> 1, currentHeight, Raylib_cs.Color.Red);

                DrawUI(tick, rectCount);

                Raylib.EndDrawing();
            }

            Raylib.CloseWindow();
            IsRunning = false;
        }

        private static void UpdateWindowDimensions()
        {
            int newWidth = Raylib.GetScreenWidth();
            int newHeight = Raylib.GetScreenHeight();
            
            // Only update if dimensions actually changed to avoid unnecessary work
            if (newWidth != currentWidth || newHeight != currentHeight)
            {
                currentWidth = newWidth;
                currentHeight = newHeight;
                
                // Reinitialize renderer with new dimensions
                NoteRenderer.Initialize(currentHeight, PAD);
            }
        }

        private static void HandleInput()
        {
            // Zoom controls with better scaling
            if (Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressedRepeat(KeyboardKey.Up))
                NoteRenderer.Window = Math.Max(100f, NoteRenderer.Window * 0.9f);
            if (Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressedRepeat(KeyboardKey.Down))
                NoteRenderer.Window = Math.Min(100000f, NoteRenderer.Window * 1.111111f);
            
            // Seeking controls
            if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressedRepeat(KeyboardKey.Right)) 
                MIDIClock.time += MIDIClock.ppq;
            if (Raylib.IsKeyPressed(KeyboardKey.W) || Raylib.IsKeyPressedRepeat(KeyboardKey.W)) 
                MIDIClock.time += Math.Round(1 / MIDIClock.ticklen, 5);
            // Note: Backward seeking disabled to prevent audio issues
            //if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressedRepeat(KeyboardKey.Left)) 
            //    MIDIClock.time -= 1000;
            
            // Toggle controls
            if (Raylib.IsKeyPressed(KeyboardKey.G)) 
                NoteRenderer.EnableGlow = !NoteRenderer.EnableGlow;
            if (Raylib.IsKeyPressed(KeyboardKey.D)) 
                Debug = !Debug;
            if (Raylib.IsKeyPressed(KeyboardKey.V)) 
            { 
                vsync = !vsync; 
                Raylib.SetTargetFPS(vsync ? Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor()) : 0); 
            }
            if (Raylib.IsKeyPressed(KeyboardKey.F)) 
                Raylib.ToggleBorderlessWindowed();
            if (Raylib.IsKeyPressed(KeyboardKey.C)) controls = !controls;
            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
                IsRunning = false;
        }

        private static void DrawUI(float tick, int rectCount)
        {
            lock (NoteProcessor.ReadyLock)
            {
                // Reuse StringBuilder to reduce allocations
                tickStr.Clear();
                tickStr.Append("Tick: ").Append((int)tick)
                       .Append(" | Tempo: ").Append(MIDIPlayer.bpm.ToString("F1"))
                       .Append(" | Zoom: ").Append((int)NoteRenderer.Window)
                       .Append(" | Glow: ").Append(NoteRenderer.EnableGlow ? "ON" : "OFF")
                       .Append(" | FPS: ").Append(Raylib.GetFPS());

                Raylib.DrawText(tickStr.ToString(), 10, 5, 16, Raylib_cs.Color.Green);

                if (Debug)
                {
                    debugStr.Clear();
                    debugStr.Append("Visible: ").Append(rectCount)
                            .Append(" | Memory: ").Append(Form1.toMemoryText(GC.GetTotalMemory(false)));
                    Raylib.DrawText(debugStr.ToString(), 10, 25, 16, Raylib_cs.Color.SkyBlue);
                }

                if (controls)
                {
                    Raylib.DrawText("CONTROLS: Up/Dn = Zoom | G = Glow | V = Vsync | Right = Seek fwd | W = skip further \n C = toggle this text(no bw seeking since sound dies for the amount of ticks we seeked back)", 10, 25, 16, Raylib_cs.Color.White);
                    if(Raylib.GetTime() >= 3.0 && Raylib.GetTime() <= 3.5)
                    controls = false;
                }

                if (!NoteProcessor.IsReady)
                    Raylib.DrawText("No MIDI loaded.", 10, currentHeight - 20, 16, Raylib_cs.Color.Yellow);
                else
                    Raylib.DrawText(Starter.filename ?? "Unknown", 10, currentHeight - 20, 16, Raylib_cs.Color.SkyBlue);
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