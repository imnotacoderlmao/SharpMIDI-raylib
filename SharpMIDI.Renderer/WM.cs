using System.Runtime.InteropServices;
using System.Diagnostics;
using Raylib_cs;

namespace SharpMIDI.Renderer
{
    public static class WindowManager
    {
        public const int PAD = 20;
        private static float scrollfactor = 1f;
        public static float tick = 0f;
        public static int memusagecallcount = 0;
        public static long memusage = 0;
        static string filepath;
        public static double lastrendernow;
        // Dynamic window dimensions
        private static int currentWidth = 1280;
        private static int currentHeight = 720;

        // Pre-allocated buffers for UI strings
        private static readonly System.Text.StringBuilder tickStr = new(256);
        private static readonly System.Text.StringBuilder debugStr = new(128);
        // Window state
        private static bool vsync = true, controls = false, dynascroll = false;
        public static bool Debug { get; set; } = false;
        public static bool IsRunning { get; private set; } = false;

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
            NoteRenderer.Initialize(currentWidth, currentHeight);

            int targetFPS = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
            Raylib.SetTargetFPS(vsync ? targetFPS : 0);

            while (!Raylib.WindowShouldClose())
            {
                UpdateWindowDimensions();
                HandleInput();
                tick = (float)MIDIClock.tick;

                //performance intensive since this forces a full rebuild every bpm change so hmmmm
                if (dynascroll && NoteRenderer.Window != MIDIClock.tickscale) 
                    NoteRenderer.SetWindow((float)MIDIClock.tickscale * scrollfactor); 

                HandlePlaybackStatus((int)tick);
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

        private static void HandlePlaybackStatus(int clock)
        {
            if (clock > MIDILoader.maxTick)
                MIDIPlayer.stopping = true;
            if (!MIDIPlayer.stopping)
                MIDIPlayer.UpdatePlaybackStats(clock);
        }

        private static void HandleInput()
        {
            if (Raylib.IsFileDropped())
            {
                FilePathList droppedFiles = Raylib.LoadDroppedFiles();
                unsafe
                {
                    filepath = Marshal.PtrToStringUTF8((nint)droppedFiles.Paths[0]);
                }
                Raylib.UnloadDroppedFiles(droppedFiles);
                Task.Run(() => MIDILoader.LoadMIDI(filepath, ushort.MaxValue));
            }
            
            if (Raylib.IsKeyPressed(KeyboardKey.One))
                Sound.InitSynth("KDMAPI");
            if (Raylib.IsKeyPressed(KeyboardKey.Two))
                Sound.InitSynth("XSynth");

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
            
            if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressedRepeat(KeyboardKey.Right))
            { 
                MIDIClock.tick += MIDIClock.tickscale;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Space))
            { 
                if (MIDIPlayer.stopping) Task.Run(MIDIPlayer.StartPlayback);
                if (!MIDIClock.paused) MIDIClock.Stop();
                else MIDIClock.Resume();
            }
            if (Raylib.IsKeyPressed(KeyboardKey.R))
                MIDIPlayer.stopping = true;
            if (Raylib.IsKeyPressed(KeyboardKey.E))
                MIDIClock.skipevents = !MIDIClock.skipevents;

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
            if (Raylib.IsKeyPressed(KeyboardKey.U))
                MIDILoader.UnloadMIDI();
        }

        public static string toMemoryText(long bytes)
        {
            return bytes switch
            {
                var expression when bytes < 1000 => $"{bytes} B",
                var expression when bytes < 1000000 => $"{bytes / 1000} KB",
                _ => $"{bytes / 1000000} MB",
            };
        }

        private static void DrawUI()
        {
            // Main UI
            tickStr.Clear();
            tickStr.Append($"Tick: {(int)tick} | Tempo: {MIDIClock.bpm.ToString("F1")} | Zoom: {(int)NoteRenderer.Window} | FPS: {Raylib.GetFPS()}");
            Raylib.DrawText(tickStr.ToString(), 12, 4, 16, Raylib_cs.Color.Green);
            if (Debug)
            {
                GetMemoryUsage();
                debugStr.Clear();
                debugStr.Append($"DrawOps: {NoteRenderer.NotesDrawnLastFrame} | Memory: {toMemoryText(memusage)}")
                        .Append(" | DynaScroll: ").Append(dynascroll ? $"({scrollfactor}x ticklen)" : "False");
                Raylib.DrawText(debugStr.ToString(), 13, 23, 16, Raylib_cs.Color.SkyBlue);
            }
            if (Timer.Seconds() < 4.0d || controls)
            {
                Raylib.DrawText($"Up/Dn = zoom | V = vsync | Right = seek fwd\nLeft = skip bw (broken) | C = toggle this text | F = fullscreen\nD = debug | S = dynamic scrolling | U = unload midi | E = skip event toggle\nR = reset playback | Space = start, pause continue playback \nto load a midi file drag and drop a file into the window\nremember to init the synth via pressing your number keys\n(1 = KDMAPI, 2 = XSynth)", 12, 45, 16, Raylib_cs.Color.White);
            }
            if (Debug) Raylib.DrawText($"{MIDILoader.loadstatus} | MIDI: @{MIDIPlayer.MIDIFps} fps | Skip events?: {MIDIClock.skipevents}", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
            else Raylib.DrawText($"{MIDILoader.loadstatus}", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
        }
        
        public static void GetMemoryUsage()
        {
            memusagecallcount++;
            if(memusagecallcount % 4 == 0) // should be fine this way so it dosent have to call many times
            {
                Process program = Process.GetCurrentProcess();
                memusage = program.WorkingSet64;
            }
        }
        
        public static void StopRenderer() => IsRunning = false;
    }
}
