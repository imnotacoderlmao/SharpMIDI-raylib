using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Numerics;
using Raylib_cs;
using rlImGui_cs;
using ImGuiNET;
using SharpMIDI.Renderer;

namespace SharpMIDI
{
    public static class WindowManager
    {
        public const int PAD = 20;
        private static float scrollfactor = 1f;
        public static int WindowTicks = 2000;
        private static int tick;
        private static int memusagecallcount = 0;
        private static long memusage = 0;
        static string filepath;

        private static int currentWidth  = 1280;
        private static int currentHeight = 720;

        private static bool vsync = true;
        private static bool dynascroll = false; 
        private static bool looping = false; 
        private static bool uivisible = false;
        private static bool isborderless = false;
        #if WINDOWS
        private static string selectedwinmmout = "";
        #endif
        public static bool trackcolors = true;
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
            NoteRenderer.Initialize(currentWidth, currentHeight);
            Raylib.SetTargetFPS(Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor()));
            
            rlImGui.Setup(true);
            while (!Raylib.WindowShouldClose())
            {
                tick = MIDIPlayer.curr_tick;
                UpdateWindowDimensions();
                HandleInput();

                if (dynascroll && NoteRenderer.Window != MIDIClock.tickscale)
                    NoteRenderer.SetWindow((int)(MIDIClock.tickscale * scrollfactor));

                Raylib.BeginDrawing();
                
                Raylib.ClearBackground(Raylib_cs.Color.Black);
                NoteRenderer.Render(currentWidth, currentHeight, tick, PAD);
                Raylib.DrawLine(currentWidth >> 1, 0, currentWidth >> 1, currentHeight, Raylib_cs.Color.Red);
                DrawText(); 
                DrawUI();
                
                Raylib.EndDrawing();
            }
            MIDILoader.UnloadMIDI();
            rlImGui.Shutdown();
            NoteRenderer.Cleanup();
            Raylib.CloseWindow();
            IsRunning = false;
        }

        private static void UpdateWindowDimensions()
        {
            if (Raylib.IsWindowResized())
            {
                currentWidth = Raylib.GetScreenWidth();
                currentHeight = Raylib.GetScreenHeight();
                NoteRenderer.Initialize(currentWidth, currentHeight);
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
                        string[] filepaths = new string[droppedFiles.Count];
                        for (int files = 0; files < droppedFiles.Count; files++)
                            filepaths[files] = Marshal.PtrToStringUTF8((nint)droppedFiles.Paths[files]);
                        if (!Sound.issynthinitiated)
                        {
                            MIDILoader.loadstatus = "initialize a synth first to continue with playlist playback";
                        }
                        else
                        {
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

            if (Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressedRepeat(KeyboardKey.Up))
            {
                if (dynascroll)
                {
                    if (scrollfactor <= 1) scrollfactor /= 2;
                    else scrollfactor -= 0.5f;
                }
                NoteRenderer.SetWindow((int)Math.Max(100f, NoteRenderer.Window * 0.9f));
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressedRepeat(KeyboardKey.Down))
            {
                if (dynascroll)
                {
                    if (scrollfactor <= 1) scrollfactor *= 2;
                    else scrollfactor += 0.5f;
                }
                NoteRenderer.SetWindow((int)Math.Max(100000f, NoteRenderer.Window * 1.1f));
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressedRepeat(KeyboardKey.Left))
            {
                if(!MIDIPlayer.stopping)
                    MIDIClock.Skip(-MIDIClock.tickscale, false);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressedRepeat(KeyboardKey.Right))
            {
                if(!MIDIPlayer.stopping) 
                    MIDIClock.Skip(MIDIClock.tickscale, false);
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
            Raylib.DrawText($"Tick: {MIDIPlayer.curr_tick} | Tempo: {MIDIClock.bpm:F1} | Zoom: {(int)NoteRenderer.Window} | FPS: {Raylib.GetFPS()}", 12, 4, 16, Raylib_cs.Color.Green);
            if (Debug)
            {
                GetMemoryUsage();
                string dynascrollstr = dynascroll ? $"({scrollfactor}x ticklen)" : "False";
                Raylib.DrawText($"DrawOps: {NoteRenderer.NotesDrawnLastFrame} | Memory: {Starter.toMemoryText(memusage)} | DynaScroll: {dynascrollstr}", 13, 23, 16, Raylib_cs.Color.SkyBlue);
                Raylib.DrawText($"{MIDILoader.loadstatus} | Skip events?: {MIDIClock.skipevents} | MIDI: @{MIDIPlayer.MIDIFps} fps", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
            }
            else Raylib.DrawText($"{MIDILoader.loadstatus}", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
        }
        
        public static void DrawUI()
        {
            if (!uivisible) return;
            rlImGui.Begin();
            ImGui.SetNextWindowSize(new Vector2(475, 250), ImGuiCond.Once);
            ImGui.Begin("Settings", ImGuiWindowFlags.NoResize);
            if (ImGui.BeginTabBar(string.Empty))
            {
                if (ImGui.BeginTabItem("Renderer"))
                {
                    if (!dynascroll)
                    {
                        if (ImGui.SliderInt("Renderer zoom", ref WindowTicks, 0, 100000))
                            NoteRenderer.SetWindow(WindowTicks);
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        {
                            ImGui.SetTooltip("how many ticks of the midi are visible in the window");
                        }
                    }
                    else
                    {
                        ImGui.SliderFloat("Scroll factor", ref scrollfactor, 0, 10);
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        {
                            ImGui.SetTooltip("in this case its just ticks/sec * scrollfactor, so... how many seconds are visible?");
                        }
                    }
                    if (ImGui.Checkbox("Fullscreen (borderless)", ref isborderless))
                        Raylib.ToggleBorderlessWindowed();
                    if (ImGui.Checkbox("Vsync", ref vsync))
                        Raylib.SetTargetFPS(vsync ? Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor()) : 0);
                    ImGui.Checkbox("Dynamic scrolling", ref dynascroll);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("oo... fake time based... (do not use this in demanding midis it forces a full redraw for now)");
                    }
                    ImGui.Checkbox("Track colors", ref trackcolors);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("Disabling track colors will save around 40%% memory at the cost of visuals being... subpar at least");
                    }
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Playback"))
                {
                    if (ImGui.SliderInt("time", ref tick, 0, MIDILoader.maxTick))
                        MIDIClock.Skip(tick, true);
                    ImGui.SliderInt("Parser buf size (MiB)", ref MIDILoader.parse_buffer_size, 1, 32);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("Changes the parser's buffer. increasing/decreasing may lead to faster parsing. YMMV");
                    }
                    ImGui.Checkbox("Single threaded playback", ref singlethreadplayback);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("While this makes playback half as demanding due to the audio thread being disabled\nyou are at the mercy of whatever synth API youre sending to (way lower throughput)");
                    }
                    ImGui.Checkbox("Limit playback FPS", ref MIDIPlayer.potato_mode);  
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("if you dont think disabling the audio thread is enough to save you from 100%% cpu usage");
                    }
                    ImGui.Checkbox("Playlist looping", ref looping);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("if you want your midi playlist to keep going instead of stopping");
                    }
                    if (ImGui.Checkbox("Event skipping", ref MIDIClock.skipevents))
                    {
                        MIDIClock.throttle = !MIDIClock.skipevents;
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("Toggling this will dictate wether the player will throttle or skip events when stalled.\ndisabling will make the player throttle itself to prevent stalling and go through every event in the midi.\nwhile skipping dosent throttle timing and just skips sending to synth till it dosent stall anymore");
                    }
                    if (MIDIClock.skipevents == false)
                        ImGui.Checkbox("Throttle Playback", ref MIDIClock.throttle);
                    ImGui.Checkbox("Debug stats", ref Debug);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("meh its just extra text on the renderer gui, idk");
                    }
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Synthesizer"))
                {
                    // dear god
                    if (ImGui.Checkbox("Empty", ref initiatedsynth[0]))
                    {
                        Sound.Close();
                        initiatedsynth[0] = true;
                        initiatedsynth[1] = false;
                        initiatedsynth[2] = false;
                    }
                    if (ImGui.Checkbox("KDMAPI", ref initiatedsynth[1]))
                    {
                        Sound.InitSynth("KDMAPI", "");
                        initiatedsynth[0] = false;
                        initiatedsynth[1] = true;
                        initiatedsynth[2] = false;
                    }
                    #if WINDOWS
                    if (ImGui.Checkbox("WinMM", ref initiatedsynth[2]))
                    {
                        initiatedsynth[0] = false;
                        initiatedsynth[1] = false;
                        initiatedsynth[2] = true;
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