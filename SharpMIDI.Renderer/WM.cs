#pragma warning disable 8601, 8604
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
        static string filepath = string.Empty;

        private static int currentWidth = 1280;
        private static int currentHeight = 720;
        private static int tick;

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
        public static bool singlethreadplayback = false;
        public static int currsynth = 0;

        public static void StartRenderer()
        {
            Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
            Raylib.InitWindow(currentWidth, currentHeight, "SharpMIDI");
            GLNoteRenderer.Initialize();
            Raylib.SetTargetFPS(Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor()));
            
            rlImGui.Setup(true);
            while (!Raylib.WindowShouldClose())
            {
                tick = MIDIPlayer.curr_tick;
                UpdateWindowDimensions();
                HandleInput();

                if (dynascroll && GLNoteRenderer.WindowTicks != MIDIClock.tickscale * scrollfactor)
                    GLNoteRenderer.WindowTicks = (int)(MIDIClock.tickscale * scrollfactor);

                Raylib.BeginDrawing();
                
                Raylib.ClearBackground(Raylib_cs.Color.Black);
                GLNoteRenderer.Render(currentWidth, currentHeight, tick, PAD);
                DrawText(); 
                DrawUI();
                
                Raylib.EndDrawing();
            }
            MIDILoader.UnloadMIDI();
            rlImGui.Shutdown();
            GLNoteRenderer.Dispose();
            Raylib.CloseWindow();
        }

        private static void UpdateWindowDimensions()
        {
            if (Raylib.IsWindowResized())
            {
                currentWidth = Raylib.GetScreenWidth();
                currentHeight = Raylib.GetScreenHeight();
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
                    if (scrollfactor <= 1) 
                        scrollfactor /= 2;
                    else 
                        scrollfactor -= 0.5f;
                }
                else
                    GLNoteRenderer.WindowTicks = (int)Math.Max(100f, GLNoteRenderer.WindowTicks * 0.9f);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressedRepeat(KeyboardKey.Down))
            {
                if (dynascroll)
                {
                    if (scrollfactor <= 1) 
                        scrollfactor *= 2;
                    else 
                        scrollfactor += 0.5f;
                }
                else
                    GLNoteRenderer.WindowTicks = (int)Math.Min(100000f, GLNoteRenderer.WindowTicks * 1.1f);
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
                if (MIDIPlayer.stopping)
                    Task.Run(() => MIDIPlayer.StartPlayback(singlethreadplayback));
                if (!MIDIClock.paused)
                    MIDIClock.Stop();
                else 
                    MIDIClock.Resume();
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
            Raylib.DrawText($"Tick: {MIDIPlayer.curr_tick} | Tempo: {MIDIClock.bpm:F1} | Zoom: {GLNoteRenderer.WindowTicks} | FPS: {Raylib.GetFPS()}", 12, 4, 16, Raylib_cs.Color.Green);
            if (Debug)
            {
                GetMemoryUsage();
                Raylib.DrawText($"Active notes: {GLNoteRenderer.NotesDrawnLastFrame} / {GLNoteRenderer.RingCap} | Memory: {Starter.toMemoryText(GetMemoryUsage())}", 13, 23, 16, Raylib_cs.Color.SkyBlue);
                Raylib.DrawText($"{MIDILoader.loadstatus} | MIDI thread: @{MIDIPlayer.MIDIFps:N0} fps", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
            }
            else 
                Raylib.DrawText($"{MIDILoader.loadstatus}", 12, currentHeight - 19, 16, Raylib_cs.Color.SkyBlue);
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
                        ImGui.SliderInt("Renderer zoom", ref GLNoteRenderer.WindowTicks, 1, 100000);
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        {
                            ImGui.SetTooltip("how many ticks of the midi are visible in the window");
                        }
                    }
                    else
                    {
                        ImGui.SliderFloat("Scroll factor", ref scrollfactor, 0.000001f, 10);
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
                    ImGui.Checkbox("Force cull notes", ref GLNoteRenderer.UseForceCull);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("Force cull notes if held for a long time\n(due to how notes are stored, this prevents the vbo size from balloning too much. will break visuals in some cases though)");
                    }
                    ImGui.Checkbox("Note Transparency", ref GLNoteRenderer.EnableTransparency);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("Note transparency linear to their velocity");
                    }
                    ImGui.Checkbox("Note glow", ref GLNoteRenderer.EnableGlow);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("Brighten notes that are being played");
                    }
                    ImGui.Checkbox("Track colors", ref trackcolors);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("Disabling track colors will save around 25%% memory at the cost of visuals being... subpar at least");
                    }
                    ImGui.Checkbox("Debug stats", ref Debug);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("meh its just extra text on the renderer gui, idk");
                    }
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Playback"))
                {
                    if (ImGui.SliderInt("time", ref tick, 0, MIDILoader.maxTick))
                    {
                        MIDIClock.Skip(tick, true);
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
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Synthesizer"))
                {
                    // no more dear god. genuinely forgot radiobuttons existed at all
                    if (ImGui.RadioButton("Empty", ref currsynth, 0))
                        Sound.Close();
                    if (ImGui.RadioButton("KDMAPI", ref currsynth, 1))
                        Sound.InitSynth("KDMAPI", "");
                    #if WINDOWS
                    ImGui.RadioButton("WinMM", ref currsynth, 2);
                    if (currsynth == 2 && ImGui.BeginCombo("WinMM Device", selectedwinmmout))
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

        public static long GetMemoryUsage() => Process.GetCurrentProcess().WorkingSet64;
    }
}