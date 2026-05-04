using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Numerics;
using Raylib_cs;
using rlImGui_cs;
using ImGuiNET;

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

        private static bool vsync = true;
        private static bool dynascroll = false; 
        private static bool looping = false; 
        private static bool uivisible = false;
        private static bool isborderless = false;
        static string selectedwinmmout = "";
        public static bool Debug = false;
        public static bool IsRunning { get; private set; } = false;
        public static bool singlethreadplayback = false;
        public static bool[] initiatedsynth = new bool[Sound.synths.Length];

        public static void StartRenderer()
        {
            if (IsRunning) return;
            initiatedsynth[0] = true;
            IsRunning = true;
            RenderLoop();
        }

        private static void RenderLoop()
        {
            Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
            Raylib.InitWindow(currentWidth, currentHeight, "SharpMIDI");
            MIDIRenderer.Initialize(currentWidth);
            Raylib.SetTargetFPS(Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor()));
            
            rlImGui.Setup(true);
            while (!Raylib.WindowShouldClose())
            {
                UpdateWindowDimensions();
                HandleInput();

                if (dynascroll && MIDIRenderer.WindowTicks != MIDIClock.tickscale)
                    MIDIRenderer.WindowTicks = (float)MIDIClock.tickscale * scrollfactor;

                Raylib.BeginDrawing();
                
                Raylib.ClearBackground(Raylib_cs.Color.Black);
                MIDIRenderer.Render(currentWidth, currentHeight, MIDIClock.tick, PAD);
                Raylib.DrawLine(currentWidth >> 1, 0, currentWidth >> 1, currentHeight, Raylib_cs.Color.Red);
                DrawText(); 
                DrawUI(uivisible);
                
                Raylib.EndDrawing();
            }
            MIDILoader.UnloadMIDI();
            rlImGui.Shutdown();
            
            Raylib.CloseWindow();
            IsRunning = false;
        }

        private static void UpdateWindowDimensions()
        {
            int newWidth  = Raylib.GetScreenWidth();
            int newHeight = Raylib.GetScreenHeight();

            if (newWidth != currentWidth || newHeight != currentHeight)
            {
                currentWidth   = newWidth;
                currentHeight  = newHeight;
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
                await Task.Run(() => MIDIPlayer.StartPlayback(singlethreadplayback));
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
                MIDIRenderer.WindowTicks = Math.Max(100f, MIDIRenderer.WindowTicks * 0.9f);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressedRepeat(KeyboardKey.Down))
            {
                if (dynascroll)
                {
                    if (scrollfactor <= 1) scrollfactor *= 2;
                    else scrollfactor += 0.5f;
                }
                MIDIRenderer.WindowTicks = Math.Min(100000f, MIDIRenderer.WindowTicks * 1.1f);
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressedRepeat(KeyboardKey.Left))
            {
                if(!MIDIPlayer.stopping)
                    MIDIClock.tick -= MIDIClock.tickscale;
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressedRepeat(KeyboardKey.Right))
            {
                if(!MIDIPlayer.stopping) 
                    MIDIClock.tick += MIDIClock.tickscale;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Space))
            {
                if (MIDIPlayer.stopping) Task.Run(() => MIDIPlayer.StartPlayback(singlethreadplayback));
                if (!MIDIClock.paused) MIDIClock.Stop();
                else MIDIClock.Resume();
            }
            if (Raylib.IsKeyPressed(KeyboardKey.R))
                MIDIPlayer.stopping = true;

            if (Raylib.IsKeyPressed(KeyboardKey.U))
                MIDILoader.UnloadMIDI();
            
            if (Raylib.IsKeyPressed(KeyboardKey.Q))
                uivisible = !uivisible;
        }

        private static void DrawText()
        {
            Raylib.DrawText($"Tick: {(long)MIDIClock.tick} | Tempo: {MIDIClock.bpm:F1} | Zoom: {(int)MIDIRenderer.WindowTicks} | FPS: {Raylib.GetFPS()}", 12, 4, 16, Raylib_cs.Color.Green);
            if (Debug)
            {
                GetMemoryUsage();
                string dynascrollstr = dynascroll ? $"({scrollfactor}x ticklen)" : "False";
                Raylib.DrawText($"DrawOps: {MIDIRenderer.NotesDrawnLastFrame} | Memory: {Starter.toMemoryText(memusage)} | DynaScroll: {dynascrollstr}", 13, 23, 16, Raylib_cs.Color.SkyBlue);
                Raylib.DrawText($"{MIDILoader.loadstatus} | Skip events?: {MIDIClock.skipevents} | MIDI: @{MIDIPlayer.MIDIFps} fps", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
            }
            else Raylib.DrawText($"{MIDILoader.loadstatus}", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
        }
        
        public static void DrawUI(bool visible)
        {
            if (!visible) return;
            rlImGui.Begin();
            ImGui.SetNextWindowSize(new Vector2(450, 250), ImGuiCond.Always);
            ImGui.Begin("Settings", ImGuiWindowFlags.NoResize);
            if (ImGui.BeginTabBar(string.Empty))
            {
                if (ImGui.BeginTabItem("Renderer"))
                {
                    if (!dynascroll)
                        ImGui.SliderFloat("Renderer zoom", ref MIDIRenderer.WindowTicks, 0, 100000);
                    else
                        ImGui.SliderFloat("Scroll factor", ref scrollfactor, 0, 10);
                    if (ImGui.Checkbox("Fullscreen (borderless)", ref isborderless))
                        Raylib.ToggleBorderlessWindowed();
                    if (ImGui.Checkbox("Vsync", ref vsync))
                        Raylib.SetTargetFPS(vsync ? Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor()) : 0);
                    ImGui.Checkbox("Dynamic scrolling", ref dynascroll);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Playback"))
                {
                    ImGui.Checkbox("Single threaded playback", ref singlethreadplayback);
                    ImGui.Checkbox("Limit playback FPS", ref MIDIPlayer.potato_mode);  
                    ImGui.Checkbox("Playlist looping", ref looping);
                    ImGui.Checkbox("Event skipping", ref MIDIClock.skipevents);
                    ImGui.Checkbox("Debug stats", ref Debug); 
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Synthesizer"))
                {
                    // dear god
                    if (ImGui.Checkbox("Empty", ref initiatedsynth[0]))
                    {
                        Sound.Close();
                        initiatedsynth[1] = false;
                        initiatedsynth[2] = false;
                    }
                    if (ImGui.Checkbox("KDMAPI", ref initiatedsynth[1]))
                    {
                        Sound.InitSynth("KDMAPI", "");
                        initiatedsynth[0] = false;
                        initiatedsynth[2] = false;
                    }
                    #if WINDOWS
                    if (ImGui.Checkbox("WinMM", ref initiatedsynth[2]))
                    {
                        initiatedsynth[0] = false;
                        initiatedsynth[1] = false;
                    }
                    if (initiatedsynth[2] && ImGui.BeginCombo("WinMM Device", selectedwinmmout))
                    {
                        foreach (string i in WinMM.winMMDevices)
                        {
                            bool is_selected = (selectedwinmmout == i);
                            if (ImGui.Selectable(i, is_selected))
                            {
                                selectedwinmmout = i;
                                Sound.InitSynth("WinMM", i);
                            }
                            if (is_selected)
                                ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }
                    #endif
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Controls"))
                {
                    ImGui.Text("Up/dn arrow = zoom");
                    ImGui.Text("Space: Start/Stop playback");
                    ImGui.Text("R = Reset Playback");
                    ImGui.Text("U = Unload MIDI");
                    ImGui.Text("To load a midi file, drag and drop one to the player window");
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            ImGui.End();
            rlImGui.End();
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