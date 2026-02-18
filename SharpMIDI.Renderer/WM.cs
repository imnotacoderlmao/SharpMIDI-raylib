using Raylib_cs;

namespace SharpMIDI.Renderer
{
    public static class WindowManager
    {
        public const int PAD = 20;
        private static float scrollfactor = 1f;
        public static float tick = 0f;
        public static double lastrendernow;
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
            NoteRenderer.Initialize(currentWidth, currentHeight);

            int targetFPS = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
            Raylib.SetTargetFPS(vsync ? targetFPS : 0);

            while (!Raylib.WindowShouldClose())
            {
                UpdateWindowDimensions();
                HandleInput();

                //tick = UpdateRenderTick();
                tick = (float)MIDIClock.tick;
                if (MIDIPlayer.stopping) 
                {
                    tick = 0;
                    NoteRenderer.lastTick = 0;
                    NoteRenderer.forceRedraw = true;
                }

                //performance intensive since this forces a full rebuild every bpm change so hmmmm
                if (dynascroll && NoteRenderer.Window != MIDIClock.tickscale) 
                    NoteRenderer.SetWindow((float)MIDIClock.tickscale * scrollfactor); 

                NoteRenderer.UpdateStreaming(tick);
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Raylib_cs.Color.Black);
                NoteRenderer.Render(currentWidth, currentHeight, PAD);
                Raylib.DrawLine(currentWidth >> 1, 0, currentWidth >> 1, currentHeight, Raylib_cs.Color.Red);
                DrawUI();

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
                NoteRenderer.Initialize(currentWidth, currentHeight);
            }
        }

        /*
        this is the result of trying to decouple renderer tick from player tick
        public static float UpdateRenderTick()
        {
            if (MIDIClock.paused || MIDIPlayer.stopping) return tick;
            double localnow = Timer.Seconds();
            double advancetime = localnow - lastrendernow;
            if(!MIDIClock.stalled) 
            {
                tick = (float)MIDIClock.tick;
            }
            else
            {
                tick += (float)(advancetime * MIDIClock.tickscale);
            }
            lastrendernow = localnow;
            return tick;
        }*/

        private static void HandleInput()
        {
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
                MIDIClock.tick += MIDIClock.tickscale;
                tick = (float)MIDIClock.tick;
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
        }

        private static void DrawUI()
        {
            // Main UI
            tickStr.Clear();
            tickStr.Append("Tick: ").Append((int)tick)
                   .Append(" | Tempo: ").Append(MIDIClock.bpm.ToString("F1"))
                   .Append(" | Zoom: ").Append((int)NoteRenderer.Window)
                   //.Append(" | Glow: ").Append("broken")
                   .Append(" | FPS: ").Append(Raylib.GetFPS());
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