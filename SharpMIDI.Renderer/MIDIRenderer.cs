using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SharpMIDI;
using Raylib_cs;
using System.Numerics;

namespace SharpMIDI.Renderer
{
    public static unsafe class MIDIRenderer
    {
        private const uint INACTIVE = 0xFFFFFFFF;
        private const int CHANNELS = 16;
        private const int NOTES = 128;
        private const int TOTAL_KEYS = CHANNELS * NOTES;

        // Active notes: stores start tick for each channel+note combination
        private static uint* activeNoteStart;
        private static bool isInitialized = false;

        public static readonly uint[] MIDIColors =
        {
            0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFF00,
            0xFFFF00FF, 0xFF00FFFF, 0xFFFF8000, 0xFF8000FF,
            0xFF0080FF, 0xFF80FF00, 0xFFFF0080, 0xFF00FF80,
            0xFF00FA92, 0xFF00FFFF, 0xFFF7DB05, 0xFF4040FF,
        };

        private static ulong searchCursor;
        private static ulong playbackCursor;

        public static float WindowTicks { get; private set; } = 1000f;
        public static int NotesDrawnLastFrame;

        // Texture buffer
        private static Texture2D renderTex;
        private static uint* texPtr;
        private static int texWidth;
        private static int texHeight;
        private static int texSize;
        public static void Initialize(int width)
        {
            if (renderTex.Id != 0)
            Raylib.UnloadTexture(renderTex);
        
            if (texPtr != null)
            {
                NativeMemory.AlignedFree(texPtr);
                texPtr = null;
            }
            
            texWidth = width;
            texHeight = 128;
            texSize = width * texHeight;
            
            texPtr = (uint*)NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(uint)), 32);
            ClearFast();
            
            renderTex = Raylib.LoadTextureFromImage(Raylib.GenImageColor(width, texHeight, Raylib_cs.Color.Black));
            Raylib.SetTextureFilter(renderTex, TextureFilter.Point);
        }

        public static void InitializeForMIDI()
        {
            activeNoteStart = (uint*)NativeMemory.AlignedAlloc((nuint)(TOTAL_KEYS * sizeof(uint)), 32);
            
            // Fast memset using Vector256 if available
            if (Avx2.IsSupported)
            {
                Vector256<uint> inactiveVec = Vector256.Create(INACTIVE);
                int vecCount = TOTAL_KEYS / 8;
                for (int i = 0; i < vecCount; i++)
                {
                    Avx.Store(activeNoteStart + i * 8, inactiveVec);
                }
                for (int i = vecCount * 8; i < TOTAL_KEYS; i++)
                    activeNoteStart[i] = INACTIVE;
            }
            else
            {
                for (int i = 0; i < TOTAL_KEYS; i++)
                    activeNoteStart[i] = INACTIVE;
            }
            
            searchCursor = 0;
            playbackCursor = 0;
            isInitialized = true;
        }

        public static void ResetForUnload()
        {
            if (activeNoteStart != null)
            {
                NativeMemory.AlignedFree(activeNoteStart);
                activeNoteStart = null;
            }
            searchCursor = 0;
            playbackCursor = 0;
            isInitialized = false;
        }
        
        public static void SetWindow(float ticks) => WindowTicks = ticks;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ClearFast()
        {
            const uint BLACK = 0xFF000000;
            
            if (Avx2.IsSupported)
            {
                Vector256<uint> blackVec = Vector256.Create(BLACK);
                int vecCount = texSize / 8;
                
                for (int i = 0; i < vecCount; i++)
                {
                    Avx.Store(texPtr + i * 8, blackVec);
                }
                
                for (int i = vecCount * 8; i < texSize; i++)
                    texPtr[i] = BLACK;
            }
            else
            {
                new Span<uint>(texPtr, texSize).Fill(BLACK);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear() => ClearFast();

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void UpdateStreaming(int tick)
        {
            if (!isInitialized || MIDI.synthEvents == null) return;

            long* events = MIDI.synthEvents.Pointer;
            ulong len = MIDI.synthEvents.Length;

            while (playbackCursor < len && (events[playbackCursor] >> 32) <= tick)
            {
                long raw = events[playbackCursor];
                uint msg = (uint)raw;

                byte statusType = (byte)(msg & 0xF0);
                
                if (statusType != 0x90 && statusType != 0x80)
                {
                    playbackCursor++;
                    continue;
                }

                byte channel = (byte)(msg & 0xF);
                byte note = (byte)((msg >> 8) & 0x7F);
                int key = (channel << 7) | note;
                
                if (statusType == 0x90)
                {
                    byte vel = (byte)((msg >> 16) & 0x7F);
                    activeNoteStart[key] = vel > 0 ? (uint)(raw >> 32) : INACTIVE;
                }
                else
                {
                    activeNoteStart[key] = INACTIVE;
                }

                playbackCursor++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ResetToTick(int tick)
        {
            if (!isInitialized || MIDI.synthEvents == null) return;

            playbackCursor = 0;
            searchCursor = 0;
            
            if (Avx2.IsSupported)
            {
                Vector256<uint> inactiveVec = Vector256.Create(INACTIVE);
                int vecCount = TOTAL_KEYS / 8;
                for (int i = 0; i < vecCount; i++)
                {
                    Avx.Store(activeNoteStart + i * 8, inactiveVec);
                }
                for (int i = vecCount * 8; i < TOTAL_KEYS; i++)
                    activeNoteStart[i] = INACTIVE;
            }
            else
            {
                for (int i = 0; i < TOTAL_KEYS; i++)
                    activeNoteStart[i] = INACTIVE;
            }

            long* events = MIDI.synthEvents.Pointer;
            ulong len = MIDI.synthEvents.Length;

            while (playbackCursor < len && (events[playbackCursor] >> 32) <= tick)
            {
                long raw = events[playbackCursor];
                uint msg = (uint)raw;

                byte statusType = (byte)(msg & 0xF0);
                
                if (statusType != 0x90 && statusType != 0x80)
                {
                    playbackCursor++;
                    continue;
                }

                byte channel = (byte)(msg & 0xF);
                byte note = (byte)((msg >> 8) & 0x7F);
                int key = (channel << 7) | note;
                
                if (statusType == 0x90)
                {
                    byte vel = (byte)((msg >> 16) & 0x7F);
                    activeNoteStart[key] = vel > 0 ? (uint)(raw >> 32) : INACTIVE;
                }
                else
                {
                    activeNoteStart[key] = INACTIVE;
                }

                playbackCursor++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int width, int height, int pad)
        {
            ClearFast();

            if (!isInitialized || MIDI.synthEvents == null)
                return;

            // Reset on stop
            if (MIDIPlayer.stopping)
            {
                ResetToTick(0);
                searchCursor = 0;
                Raylib.UpdateTexture(renderTex, (void*)texPtr);
                Raylib.DrawTexture(renderTex, 0, 0, Raylib_cs.Color.White);
                return;
            }

            NotesDrawnLastFrame = 0;

            int currentTick = (int)MIDIClock.tick;
            int viewStart = currentTick - (int)(WindowTicks * 0.5f);
            int viewEnd = currentTick + (int)(WindowTicks * 0.5f);

            long* events = MIDI.synthEvents.Pointer;
            ulong len = MIDI.synthEvents.Length;

            // Binary search for optimal cursor position
            if (searchCursor > 0 && searchCursor < len)
            {
                int searchTick = (int)(events[searchCursor] >> 32);
                int targetTick = (int)(viewStart - WindowTicks);
                
                if (Math.Abs(searchTick - targetTick) > WindowTicks)
                {
                    ulong left = 0, right = len - 1;
                    while (left < right)
                    {
                        ulong mid = (left + right) >> 1;
                        if ((events[mid] >> 32) < targetTick)
                            left = mid + 1;
                        else
                            right = mid;
                    }
                    searchCursor = left;
                }
            }

            while (searchCursor > 0 && (events[searchCursor] >> 32) > viewStart - WindowTicks)
                searchCursor--;
            
            while (searchCursor < len && (events[searchCursor] >> 32) < viewStart - WindowTicks)
                searchCursor++;

            // Stack-allocated render state
            uint* renderActiveNotes = stackalloc uint[TOTAL_KEYS];
            Buffer.MemoryCopy(activeNoteStart, renderActiveNotes, TOTAL_KEYS * sizeof(uint), TOTAL_KEYS * sizeof(uint));

            // Precompute tick-to-pixel conversion
            float ticksPerPixel = WindowTicks / width;
            float centerOffset = WindowTicks * 0.5f;

            // **COLUMN-BY-COLUMN RENDERING**
            // Process events and render one column at a time
            ulong eventCursor = searchCursor;
            
            // Track which notes should be active based on event scanning
            // We need to look ahead to find note-off events
            for (int x = 0; x < width; x++)
            {
                // Calculate which tick this column represents
                int columnTick = (int)(currentTick - centerOffset + x * ticksPerPixel);
                
                // Process all events up to this column's tick
                while (eventCursor < len)
                {
                    long raw = events[eventCursor];
                    int eventTick = (int)(raw >> 32);
                    
                    if (eventTick > columnTick) break;
                    if (eventTick > viewEnd + WindowTicks) goto EndRender;
                    
                    uint msg = (uint)raw;
                    byte statusType = (byte)(msg & 0xF0);
                    
                    if (statusType == 0x90 || statusType == 0x80)
                    {
                        byte channel = (byte)(msg & 0xF);
                        byte note = (byte)((msg >> 8) & 0x7F);
                        int key = (channel << 7) | note;
                        
                        if (statusType == 0x90)
                        {
                            byte vel = (byte)((msg >> 16) & 0x7F);
                            renderActiveNotes[key] = vel > 0 ? (uint)eventTick : INACTIVE;
                        }
                        else
                        {
                            renderActiveNotes[key] = INACTIVE;
                        }
                    }
                    
                    eventCursor++;
                }
                
                // Draw all active notes in this column
                // Only draw notes that started before or at this tick
                uint* column = texPtr + x;
                
                for (int key = 0; key < TOTAL_KEYS; key++)
                {
                    uint noteStart = renderActiveNotes[key];
                    if (noteStart == INACTIVE) continue;
                    
                    // FIX: Only draw if the note started before or at this column's tick
                    if (noteStart > columnTick) continue;
                    
                    int note = key & 0x7F;
                    int channel = key >> 7;
                    uint color = MIDIColors[channel];
                    int y = 127 - note; 
                    
                    column[y * texWidth] = color;
                    
                    NotesDrawnLastFrame++;
                }
            }
            
            EndRender:
            Raylib.UpdateTexture(renderTex, (void*)texPtr);
            Raylib.DrawTexturePro(
                renderTex,
                new Raylib_cs.Rectangle(0, 0, texWidth, 128),          // source: logical note space
                new Raylib_cs.Rectangle(0, pad, width, height - pad * 2),          // destination: window
                new Vector2(0, 0),
                0.0f,
                Raylib_cs.Color.White
            );
        }

        public static void Dispose()
        {
            if (texPtr != null)
            {
                NativeMemory.AlignedFree(texPtr);
                texPtr = null;
            }
            
            if (activeNoteStart != null)
            {
                NativeMemory.AlignedFree(activeNoteStart);
                activeNoteStart = null;
            }
        }
    }
}