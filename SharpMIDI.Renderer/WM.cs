using Raylib_cs;

namespace SharpMIDI.Renderer
{
    public static class WindowManager
    {
        public const int PAD = 20;
        private static float scrollfactor = 1f;
        public static float tick = 0f;
        public static decimal speed = 1;
        // Dynamic window dimensions
        private static int currentWidth = 1280;
        private static int currentHeight = 720;

        // Pre-allocated buffers for UI strings
        private static readonly System.Text.StringBuilder tickStr = new(256);
        private static readonly System.Text.StringBuilder debugStr = new(128);
        // Window state
        private static bool vsync = true, controls = true, dynascroll = false;
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

                // this WILL set the last elapsed time in getelapsed(), which makes throttling useless
                //tick = (float)MIDIClock.GetTick();
                
                tick = (float)MIDIPlayer.clock;
                if (MIDIPlayer.stopping) 
                {
                    tick = 0;
                    NoteRenderer.lastTick = 0;
                    NoteRenderer.forceRedraw = true;
                }

                //performance intensive since this forces a full rebuild every bpm change so hmmmm
                if (dynascroll && NoteRenderer.Window != 1 / MIDIClock.rawticklen) 
                    NoteRenderer.SetWindow((float)(1 / MIDIClock.rawticklen) * scrollfactor); 

                // Update the streaming texture using NoteProcessor data.
                // Lock around NoteProcessor to avoid racing with EnhanceTracksForRendering().
                // NoteProcessor.IsReady prevents spurious updates.
                if (NoteProcessor.IsReady)
                {
                    try
                    {
                        NoteRenderer.UpdateStreaming(tick);
                    }
                    catch (Exception ex)
                    {
                        // Keep renderer alive on unexpected errors; show minimal console info.
                        Console.WriteLine("NoteRenderer.UpdateStreaming error: " + ex.Message);
                    }
                }
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Raylib_cs.Color.Black);

                // Draw the streaming texture to screen while holding the lock for consistency.
                try
                {
                    NoteRenderer.Render(currentWidth, currentHeight, PAD);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("NoteRenderer.Render error: " + ex.Message);
                }

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
                if (dynascroll)
                {
                    if (scrollfactor <= 1) scrollfactor /= 2;
                    else scrollfactor -= 0.5f;
                }
                float newWindow = Math.Max(100f, NoteRenderer.Window * 0.9f);
                NoteRenderer.SetWindow(newWindow);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressedRepeat(KeyboardKey.Down))
            {
                if(dynascroll)
                {
                    if (scrollfactor <= 1) scrollfactor *= 2;
                    else scrollfactor += 0.5f;
                }
                float newWindow = Math.Min(100000f, NoteRenderer.Window * 1.1f);
                NoteRenderer.SetWindow(newWindow);
            }

            // Seeking controls
            if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressedRepeat(KeyboardKey.Right))
            {
                if(speed < 1) speed += 0.05M;
                else MIDIClock.tick += 1 / MIDIClock.rawticklen;
            }
            
            if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressedRepeat(KeyboardKey.Left))
            {
                speed -= 0.05M;
            }
            // Toggle controls
            if (Raylib.IsKeyPressed(KeyboardKey.S)) dynascroll = !dynascroll;
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
            // Main UI
            tickStr.Clear();
            tickStr.Append("Tick: ").Append((int)tick)
                   .Append(" | Tempo: ").Append(MIDIClock.bpm.ToString("F1"))
                   .Append(" | Zoom: ").Append((int)NoteRenderer.Window)
                   //.Append(" | Glow: ").Append("broken")
                   .Append(" | FPS: ").Append(Raylib.GetFPS());
                   if(speed > 1 || speed < 1)tickStr.Append(" | Speed: ").Append(speed).Append('x');   
            Raylib.DrawText(tickStr.ToString(), 12, 4, 16, Raylib_cs.Color.Green);
            if (Debug)
            {
                debugStr.Clear();
                debugStr.Append("DrawOps: ").Append(NoteRenderer.NotesDrawnLastFrame)
                        .Append(" | Memory: ").Append(Form1.toMemoryText(GC.GetTotalMemory(false)))
                        .Append(" | DynaScroll: ").Append(dynascroll ? $"({scrollfactor}x ticklen)" : "False");
                Raylib.DrawText(debugStr.ToString(), 12, 25, 16, Raylib_cs.Color.SkyBlue);
            }
            if (controls)
            {
                Raylib.DrawText($"Up/Dn = zoom | V = vsync | Right = seek fwd | Left = skip bw (broken) | C = toggle this text | F = fullscreen | D = debug | S = dynamic scrolling", 12, 45, 16, Raylib_cs.Color.White);
                if (Raylib.GetTime() >= 4.0 && Raylib.GetTime() <= 4.5)
                    controls = false;
            }
            if (!NoteProcessor.IsReady)
                Raylib.DrawText("No MIDI loaded.", 12, currentHeight - 19, 16, Raylib_cs.Color.Yellow);
            else
                Raylib.DrawText($"{Starter.filename ?? "Unknown"}", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
        }

        public static void StopRenderer() => IsRunning = false;
    }
}