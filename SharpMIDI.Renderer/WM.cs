using System.Runtime.InteropServices;
using System.Diagnostics;
using Raylib_cs;

namespace SharpMIDI
{
    public static class WindowManager
    {
        public const int PAD = 20;
        private static float scrollfactor = 1f;
        public static int memusagecallcount = 0;
        public static long memusage = 0;
        static string filepath;
        public static double lastrendernow;

        private static int currentWidth  = 1280;
        private static int currentHeight = 720;

        // WindowTicks state
        private static bool vsync = true, controls = true, dynascroll = false, looping = false;
        public static bool Debug    { get; set; } = false;
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
            MIDIRenderer.Initialize(currentWidth);

            int targetFPS = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
            Raylib.SetTargetFPS(vsync ? targetFPS : 0);

            while (!Raylib.WindowShouldClose())
            {
                UpdateWindowDimensions();
                HandleInput();

                if (dynascroll && MIDIRenderer.WindowTicks != MIDIClock.tickscale)
                    MIDIRenderer.SetWindow((float)MIDIClock.tickscale * scrollfactor);

                //if (!MIDIPlayer.stopping) MIDIPlayer.UpdatePlaybackStats((int)MIDIClock.tick);
                MIDIRenderer.UpdateStreaming(MIDIClock.tick);

                Raylib.BeginDrawing();
                Raylib.ClearBackground(Raylib_cs.Color.Black);
                MIDIRenderer.Render(currentWidth, currentHeight, PAD);
                Raylib.DrawLine(currentWidth >> 1, 0, currentWidth >> 1, currentHeight, Raylib_cs.Color.Red);
                DrawText();
                Raylib.EndDrawing();
            }
            
            MIDILoader.UnloadMIDI();
            Raylib.CloseWindow();
            IsRunning = false;
        }

        private static void UpdateWindowDimensions()
        {
            int newWidth  = Raylib.GetScreenWidth();
            int newHeight = Raylib.GetScreenHeight();

            if (newWidth != currentWidth || newHeight != currentHeight)
            {
                currentWidth  = newWidth;
                currentHeight = newHeight;
                MIDIRenderer.Initialize(currentWidth);
            }
        }

        static async Task PlayMIDIsSequentially(string[] filepaths)
        {
            for (int idx = 0; idx < filepaths.Length && MIDIPlayer.stopping; idx++)
            {
                if (looping && idx == filepaths.Length - 1) idx = 0;
                string path = filepaths[idx];
                await Task.Run(() => MIDILoader.LoadMIDI(path));
                if (idx + 1 < filepaths.Length)
                    Console.WriteLine($"{idx + 1}/{filepaths.Length} played. next in queue: {filepaths[idx + 1]}");
                else
                    Console.WriteLine($"{idx + 1}/{filepaths.Length} played. playlist complete.");
                await Task.Run(MIDIPlayer.StartPlayback);
            }
        }

        private static void HandleInput()
        {
            if (Raylib.IsFileDropped())
            {
                unsafe
                {
                    FilePathList droppedFiles = Raylib.LoadDroppedFiles();
                    if (droppedFiles.Count > 1)
                    {
                        Console.Write($"\rmultiple files dropped. playing each sequentially");
                        if (!Sound.issynthinitiated)
                        {
                            MIDILoader.loadstatus = "initialize a synth first to continue with playlist playback";
                            Raylib.UnloadDroppedFiles(droppedFiles);
                        }
                        else
                        {
                            string[] filepaths = new string[droppedFiles.Count];
                            for (int files = 0; files < droppedFiles.Count; files++)
                                filepaths[files] = Marshal.PtrToStringUTF8((nint)droppedFiles.Paths[files]);
                            Raylib.UnloadDroppedFiles(droppedFiles);
                            _ = PlayMIDIsSequentially(filepaths);
                        }
                    }
                    else
                    {
                        filepath = Marshal.PtrToStringUTF8((nint)droppedFiles.Paths[0]);
                        Task.Run(() => MIDILoader.LoadMIDI(filepath));
                        Raylib.UnloadDroppedFiles(droppedFiles);
                    }
                }
            }

            if (Raylib.IsKeyPressed(KeyboardKey.One))
                Sound.InitSynth("KDMAPI");
            
            /*if (Raylib.IsKeyPressed(KeyboardKey.Two))
            {
                List<string> devs = WinMM.GetDevices();
                Sound.InitSynth("WinMM");
            }*/

            if (Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressedRepeat(KeyboardKey.Up))
            {
                if (dynascroll)
                {
                    if (scrollfactor <= 1) scrollfactor /= 2;
                    else scrollfactor -= 0.5f;
                }
                float newWindow = Math.Max(100f, MIDIRenderer.WindowTicks * 0.9f);
                MIDIRenderer.SetWindow(newWindow);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressedRepeat(KeyboardKey.Down))
            {
                if (dynascroll)
                {
                    if (scrollfactor <= 1) scrollfactor *= 2;
                    else scrollfactor += 0.5f;
                }
                float newWindow = Math.Min(100000f, MIDIRenderer.WindowTicks * 1.1f);
                MIDIRenderer.SetWindow(newWindow);
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
            if (Raylib.IsKeyPressed(KeyboardKey.D)) Debug = !Debug;
            if (Raylib.IsKeyPressed(KeyboardKey.V))
            {
                vsync = !vsync;
                Raylib.SetTargetFPS(vsync ? Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor()) : 0);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.F))
                Raylib.ToggleBorderlessWindowed();
            if (Raylib.IsKeyPressed(KeyboardKey.L))
                looping = !looping;
            if (Raylib.IsKeyPressed(KeyboardKey.C))
                controls = !controls;
            if (Raylib.IsKeyPressed(KeyboardKey.U))
                MIDILoader.UnloadMIDI();
        }

        private static void DrawText()
        {
            Raylib.DrawText($"Tick: {(long)MIDIClock.tick} | Tempo: {MIDIClock.bpm:F1} | Zoom: {(int)MIDIRenderer.WindowTicks} | FPS: {Raylib.GetFPS()}", 12, 4, 16, Raylib_cs.Color.Green);
            if (controls)
            {
                Raylib.DrawText(
                    "Up/Dn = zoom | V = vsync | Right = seek fwd\n" +
                    "Left = skip bw (broken) | C = toggle this text | F = fullscreen\n" +
                    "D = debug | S = dynamic scrolling | U = unload midi | E = skip event toggle\n" +
                    "R = reset playback | Space = start, pause continue playback\n" +
                    "to load a midi file drag and drop a file into the window\n" +
                    "remember to init the synth via pressing your number keys\n" +
                    "(1 = KDMAPI)",
                    12, 45, 16, Raylib_cs.Color.White);
                if (Raylib.GetTime() >= 4.0 && Raylib.GetTime() <= 4.5)
                    controls = false;
            }
            if (Debug)
            {
                GetMemoryUsage();
                string dynascrollstr = dynascroll ? $"({scrollfactor}x ticklen)" : "False";
                Raylib.DrawText($"DrawOps: {MIDIRenderer.NotesDrawnLastFrame} | Memory: {Starter.toMemoryText(memusage)} | DynaScroll: {dynascrollstr}", 13, 23, 16, Raylib_cs.Color.SkyBlue);
                Raylib.DrawText($"{MIDILoader.loadstatus} | Skip events?: {MIDIClock.skipevents} | MIDI: @{MIDIPlayer.MIDIFps} fps", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
            }
            else Raylib.DrawText($"{MIDILoader.loadstatus}", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
        }

        public static void GetMemoryUsage()
        {
            memusagecallcount++;
            if (memusagecallcount % 4 == 0)
            {
                Process program = Process.GetCurrentProcess();
                memusage = program.WorkingSet64;
            }
        }

        public static void StopRenderer() => IsRunning = false;
    }
}