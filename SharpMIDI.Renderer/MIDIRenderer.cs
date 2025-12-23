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

        private static ulong playbackCursor;

        public static float WindowTicks { get; private set; } = 2000f;
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
            
            playbackCursor = 0;
            isInitialized = true;
            Console.WriteLine("Renderer initialized");
        }

        public static void ResetForUnload()
        {
            if (activeNoteStart != null)
            {
                NativeMemory.AlignedFree(activeNoteStart);
                activeNoteStart = null;
            }
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
        private static void DrawHorizontalLine(int x1, int x2, int y, uint color)
        {
            if (y < 0 || y >= texHeight) return;
            if (x2 < 0 || x1 >= texWidth) return;
            
            x1 = Math.Max(0, x1);
            x2 = Math.Min(texWidth - 1, x2);
            
            int width = x2 - x1 + 1;
            if (width <= 0) return;
            
            uint* rowStart = texPtr + y * texWidth + x1;
            
            // SIMD-accelerated horizontal line
            if (Avx2.IsSupported && width >= 8)
            {
                Vector256<uint> colorVec = Vector256.Create(color);
                int vecCount = width / 8;
                
                for (int i = 0; i < vecCount; i++)
                {
                    Avx.Store(rowStart + i * 8, colorVec);
                }
                
                // Remainder
                int remaining = width - (vecCount * 8);
                if (remaining > 0)
                {
                    uint* rem = rowStart + vecCount * 8;
                    for (int i = 0; i < remaining; i++)
                        rem[i] = color;
                }
            }
            else
            {
                for (int i = 0; i < width; i++)
                    rowStart[i] = color;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int width, int height, int pad)
        {
            ClearFast();

            if (!isInitialized || MIDI.synthEvents == null)
                return;

            if (MIDIPlayer.stopping)
            {
                ResetToTick(0);
                Raylib.UpdateTexture(renderTex, (void*)texPtr);
                Raylib.DrawTexturePro(
                    renderTex,
                    new Raylib_cs.Rectangle(0, 0, texWidth, 128),
                    new Raylib_cs.Rectangle(0, pad, width, height - pad * 2),
                    new Vector2(0, 0), 0.0f, Raylib_cs.Color.White
                );
                return;
            }

            NotesDrawnLastFrame = 0;

            int currentTick = (int)MIDIClock.tick;
            int viewStart = currentTick - (int)(WindowTicks * 0.5f);
            int viewEnd = currentTick + (int)(WindowTicks * 0.5f);

            long* events = MIDI.synthEvents.Pointer;
            ulong len = MIDI.synthEvents.Length;

            // Binary search to find first visible event
            ulong searchStart = 0;
            {
                int targetTick = Math.Max(0, (int)(viewStart - WindowTicks));
                ulong left = 0, right = len > 0 ? len - 1 : 0;
                
                while (left < right)
                {
                    ulong mid = (left + right) >> 1;
                    if ((int)(events[mid] >> 32) < targetTick)
                        left = mid + 1;
                    else
                        right = mid;
                }
                searchStart = left;
            }

            // Precompute conversion factors
            float ticksPerPixel = WindowTicks / texWidth;
            float invTicksPerPixel = texWidth / WindowTicks;
            float centerOffset = WindowTicks * 0.5f;

            // Build active state at viewport start by scanning backwards from searchStart
            uint* renderActive = stackalloc uint[TOTAL_KEYS];
            for (int i = 0; i < TOTAL_KEYS; i++)
                renderActive[i] = INACTIVE;

            // Scan backwards to find notes that are already active at viewStart
            for (ulong i = searchStart; i > 0 && i < len; i--)
            {
                int eventTick = (int)(events[i] >> 32);
                if (eventTick < viewStart - WindowTicks) break;
                
                long raw = events[i];
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
                        if (vel > 0 && renderActive[key] == INACTIVE)
                            renderActive[key] = (uint)eventTick;
                    }
                    else
                    {
                        if (renderActive[key] != INACTIVE)
                            renderActive[key] = INACTIVE;
                    }
                }
            }
            ulong cursor = searchStart;
            
            while (cursor < len)
            {
                long raw = events[cursor];
                int eventTick = (int)(raw >> 32);
                
                // Stop if way past visible area
                if (eventTick > viewEnd + WindowTicks) break;
                
                uint msg = (uint)raw;
                byte statusType = (byte)(msg & 0xF0);
                
                if (statusType == 0x90 || statusType == 0x80)
                {
                    byte channel = (byte)(msg & 0xF);
                    byte note = (byte)((msg >> 8) & 0x7F);
                    int key = (channel << 7) | note;
                    uint color = MIDIColors[channel];
                    int y = 127 - note;
                    
                    if (statusType == 0x90)
                    {
                        byte vel = (byte)((msg >> 16) & 0x7F);
                        
                        if (vel > 0)
                        {
                            // Note On - if there was a previous note, draw it now
                            if (renderActive[key] != INACTIVE)
                            {
                                int noteStart = (int)renderActive[key];
                                int noteEnd = eventTick;
                                
                                // Convert to screen coordinates
                                int x1 = (int)((noteStart - currentTick + centerOffset) * invTicksPerPixel);
                                int x2 = (int)((noteEnd - currentTick + centerOffset) * invTicksPerPixel);
                                
                                if (x2 >= 0 && x1 < texWidth)
                                {
                                    DrawHorizontalLine(x1, x2, y, color);
                                    NotesDrawnLastFrame++;
                                }
                            }
                            
                            renderActive[key] = (uint)eventTick;
                        }
                        else
                        {
                            // Note on with vel=0 is note off
                            if (renderActive[key] != INACTIVE)
                            {
                                int noteStart = (int)renderActive[key];
                                int x1 = (int)((noteStart - currentTick + centerOffset) * invTicksPerPixel);
                                int x2 = (int)((eventTick - currentTick + centerOffset) * invTicksPerPixel);
                                
                                if (x2 >= 0 && x1 < texWidth)
                                {
                                    DrawHorizontalLine(x1, x2, y, color);
                                    NotesDrawnLastFrame++;
                                }
                                
                                renderActive[key] = INACTIVE;
                            }
                        }
                    }
                    else // Note Off
                    {
                        if (renderActive[key] != INACTIVE)
                        {
                            int noteStart = (int)renderActive[key];
                            int x1 = (int)((noteStart - currentTick + centerOffset) * invTicksPerPixel);
                            int x2 = (int)((eventTick - currentTick + centerOffset) * invTicksPerPixel);
                            
                            if (x2 >= 0 && x1 < texWidth)
                            {
                                DrawHorizontalLine(x1, x2, y, color);
                                NotesDrawnLastFrame++;
                            }
                            
                            renderActive[key] = INACTIVE;
                        }
                    }
                }
                
                cursor++;
            }

            // Draw notes that extend past viewport end
            for (int key = 0; key < TOTAL_KEYS; key++)
            {
                if (renderActive[key] != INACTIVE)
                {
                    int noteStart = (int)renderActive[key];
                    int note = key & 0x7F;
                    int channel = key >> 7;
                    uint color = MIDIColors[channel];
                    int y = 127 - note;
                    
                    int x1 = (int)((noteStart - currentTick + centerOffset) * invTicksPerPixel);
                    int x2 = texWidth - 1;
                    
                    if (x1 < texWidth)
                    {
                        DrawHorizontalLine(x1, x2, y, color);
                        NotesDrawnLastFrame++;
                    }
                }
            }
            
            Raylib.UpdateTexture(renderTex, (void*)texPtr);
            Raylib.DrawTexturePro(
                renderTex,
                new Raylib_cs.Rectangle(0, 0, texWidth, 128),
                new Raylib_cs.Rectangle(0, pad, width, height - pad * 2),
                new Vector2(0, 0), 0.0f, Raylib_cs.Color.White
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