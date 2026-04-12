using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Raylib_cs;
using System.Numerics;

namespace SharpMIDI
{
    public static unsafe class MIDIRenderer
    {
        private const uint   INACTIVE   = 0xFFFFFFFF;
        private const uint   BLACK      = 0xFF000000;
        private const int    TOTAL_KEYS = 2048;

        private static uint*   activeNoteStart;
        private static ushort* activeTrackStart;
        private static uint*   persistentActive;
        private static ushort* persistentTrack;

        private static int  tickGroupCursor;
        private static bool isInitialized;
        private static long renderMsgCursor  = 0;
        private static int  renderTickCursor = 0;

        private static int    lastColumn = -1;
        private static double lastTick   = 0;

        public static readonly uint[] MIDIColors =
        {
            0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFF00,
            0xFFFF00FF, 0xFF00FFFF, 0xFFFF8000, 0xFF8000FF,
            0xFF0080FF, 0xFF80FF00, 0xFFFF0080, 0xFF00FF80,
            0xFF00FA92, 0xFF00FFFF, 0xFFF7DB05, 0xFF4040FF,
        };

        public static float WindowTicks         { get; private set; } = 2000f;
        public static int   NotesDrawnLastFrame { get; private set; }

        private static Texture2D renderTex;
        private static uint*   texPtr;
        private static ushort* zBuffer;   // per-pixel track index; 0 = unwritten
        private static int     texWidth;
        private static int     texSize;

        public static bool forceFullRedraw = true;
        private static uint* colorTable; // [track & 0xFF][channel], 256*16 entries

        public static void Initialize(int width)
        {
            if (renderTex.Id != 0) Raylib.UnloadTexture(renderTex);
            if (texPtr  != null)   NativeMemory.AlignedFree(texPtr);
            if (zBuffer != null)   NativeMemory.AlignedFree(zBuffer);

            texWidth = width;
            texSize  = width * 128;

            texPtr  = (uint*)  NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(uint)),   64);
            zBuffer = (ushort*)NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(ushort)), 64);

            renderTex = Raylib.LoadTextureFromImage(Raylib.GenImageColor(width, 128, Raylib_cs.Color.Black));
            Raylib.SetTextureFilter(renderTex, TextureFilter.Point);
            new Span<uint>  (texPtr,  texSize).Fill(BLACK);
            new Span<ushort>(zBuffer, texSize).Clear();

            if (colorTable == null)
                colorTable = (uint*)NativeMemory.AlignedAlloc(256 * 16 * sizeof(uint), 64);
            for (int t = 0; t < 256; t++)
                for (int c = 0; c < 16; c++)
                    colorTable[t * 16 + c] = MIDIColors[(t + c) & 0xF];

            lastColumn      = -1;
            forceFullRedraw = true;
        }

        public static void InitializeForMIDI()
        {
            if (activeNoteStart  != null) NativeMemory.AlignedFree(activeNoteStart);
            if (activeTrackStart != null) NativeMemory.AlignedFree(activeTrackStart);
            if (persistentActive != null) NativeMemory.AlignedFree(persistentActive);
            if (persistentTrack  != null) NativeMemory.AlignedFree(persistentTrack);

            activeNoteStart  = (uint*)  NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(uint),   64);
            activeTrackStart = (ushort*)NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(ushort), 64);
            persistentActive = (uint*)  NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(uint),   64);
            persistentTrack  = (ushort*)NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(ushort), 64);

            new Span<uint>  (activeNoteStart,  TOTAL_KEYS).Fill(INACTIVE);
            new Span<ushort>(activeTrackStart, TOTAL_KEYS).Clear();
            new Span<uint>  (persistentActive, TOTAL_KEYS).Fill(INACTIVE);
            new Span<ushort>(persistentTrack,  TOTAL_KEYS).Clear();

            tickGroupCursor  = 0;
            renderTickCursor = 0;
            renderMsgCursor  = 0;
            lastTick         = 0;
            lastColumn       = -1;
            isInitialized    = true;
            forceFullRedraw  = true;
        }

        public static void ResetForUnload()
        {
            if (activeNoteStart  != null) { NativeMemory.AlignedFree(activeNoteStart);  activeNoteStart  = null; }
            if (activeTrackStart != null) { NativeMemory.AlignedFree(activeTrackStart); activeTrackStart = null; }
            if (persistentActive != null) { NativeMemory.AlignedFree(persistentActive); persistentActive = null; }
            if (persistentTrack  != null) { NativeMemory.AlignedFree(persistentTrack);  persistentTrack  = null; }
            tickGroupCursor = 0;
            isInitialized   = false;
            forceFullRedraw = true;
            lastColumn      = -1;
        }

        public static void SetWindow(float ticks)
        {
            if (Math.Abs(WindowTicks - ticks) > 0.1f)
            {
                WindowTicks     = ticks;
                forceFullRedraw = true;
                lastColumn      = -1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void UpdateStreaming(double tick)
        {
            if (!isInitialized || MIDI.MIDIEventArray == null || MIDI.TickGroupArray == null) return;

            TickGroup[] groups = MIDI.TickGroupArray;
            MIDIEvent*  events = MIDI.MIDIEventArray.Pointer;
            int         gmax   = groups.Length - 2;
            int         itick  = Math.Min((int)tick, gmax);

            long msgIdx = groups[tickGroupCursor].offset;
            while (tickGroupCursor <= itick)
            {
                uint count = groups[tickGroupCursor].count;
                for (uint i = 0; i < count; i++)
                {
                    MIDIEvent e      = events[msgIdx++];
                    uint      msg    = (uint)e.message.Value;
                    byte      status = (byte)(msg & 0xF0);
                    if ((status & 0xE0) != 0x80) continue;
                    uint key    = ((msg & 0xF) << 7) | ((msg >> 8) & 0x7F);
                    bool noteOn = status == 0x90 && ((msg >> 16) & 0x7F) > 0;
                    if (noteOn) { activeNoteStart[key] = (uint)tickGroupCursor; activeTrackStart[key] = e.track; }
                    else          activeNoteStart[key] = INACTIVE;
                }
                tickGroupCursor++;
            }
        }

        public static void ResetToTick(double tick)
        {
            if (!isInitialized || MIDI.MIDIEventArray == null || MIDI.TickGroupArray == null) return;
            tickGroupCursor = 0;
            lastColumn      = -1;
            new Span<uint>  (activeNoteStart,  TOTAL_KEYS).Fill(INACTIVE);
            new Span<ushort>(activeTrackStart, TOTAL_KEYS).Clear();
            UpdateStreaming(tick);
            forceFullRedraw = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ScrollLeft(int pixels)
        {
            if (pixels <= 0 || pixels >= texWidth) return;
            int keep       = texWidth - pixels;
            int keepBytesC = keep * sizeof(uint);
            int keepBytesZ = keep * sizeof(ushort);
            for (int y = 0; y < 128; y++)
            {
                int     offset = y * texWidth;
                uint*   row    = texPtr  + offset;
                ushort* rowZ   = zBuffer + offset;
                Buffer.MemoryCopy(row  + pixels, row,  keepBytesC, keepBytesC);
                Buffer.MemoryCopy(rowZ + pixels, rowZ, keepBytesZ, keepBytesZ);
                new Span<uint>  (row  + keep, pixels).Fill(BLACK);
                new Span<ushort>(rowZ + keep, pixels).Clear();
            }
        }

        // Draw a horizontal note span with z-test.
        // priority is the track index (1-65535); higher wins. 0 is reserved for "empty".
        // Fast path: if the entire span is empty (all z==0), bulk-fill both buffers.
        // Slow path: per-pixel test only when there is actual contention.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawLine(int x1, int x2, int y, uint color, ushort priority)
        {
            if ((uint)y >= 128u || x2 < 0 || x1 >= texWidth) return;
            x1 = Math.Max(0, x1);
            x2 = Math.Min(texWidth - 1, x2);
            if (x1 > x2) return;

            int     offset = y * texWidth + x1;
            int     width  = x2 - x1 + 1;
            uint*   pixels = texPtr  + offset;
            ushort* zBuf   = zBuffer + offset;

            // Check whether anything is already drawn in this span.
            // We need the max z in the span to know if we can win anywhere.
            // Using a simple SIMD-friendly reduce: scan for any zBuf[i] > priority.
            // If none found, we always win — bulk fill. Otherwise per-pixel.
            bool contention = false;
            for (int i = 0; i < width; i++)
            {
                if (zBuf[i] > priority) { contention = true; break; }
            }

            if (!contention)
            {
                // We win everywhere — bulk fill both buffers
                new Span<uint>  (pixels, width).Fill(color);
                new Span<ushort>(zBuf,   width).Fill(priority);
            }
            else
            {
                // Per-pixel only where we win
                for (int i = 0; i < width; i++)
                {
                    if (priority >= zBuf[i]) { pixels[i] = color; zBuf[i] = priority; }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int screenWidth, int screenHeight, int pad)
        {
            if (!isInitialized) return;

            double tick          = MIDIClock.tick;
            double pixelsPerTick = texWidth / (double)WindowTicks;

            int newColumn = (int)(tick * pixelsPerTick) % texWidth;
            if (newColumn < 0) newColumn += texWidth;

            int delta = lastColumn == -1 ? texWidth
                      : newColumn >= lastColumn ? newColumn - lastColumn
                      : (texWidth - lastColumn) + newColumn;

            bool seeked = MIDIPlayer.stopping || forceFullRedraw;

            if (seeked || delta >= texWidth)
            {
                RenderFull(tick, pixelsPerTick);
                forceFullRedraw = false;
            }
            else if (delta > 0)
            {
                ScrollLeft(delta);
                double rightEdge = tick + WindowTicks * 0.5;
                AdvanceAndDraw(tick, pixelsPerTick, delta, rightEdge);
            }

            lastTick   = tick;
            lastColumn = newColumn;
            UpdateAndDraw(screenWidth, screenHeight, pad);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderFull(double centerTick, double pixelsPerTick)
        {
            new Span<uint>  (texPtr,  texSize).Fill(BLACK);
            new Span<ushort>(zBuffer, texSize).Clear();

            if (MIDI.MIDIEventArray == null || MIDI.TickGroupArray == null) return;

            TickGroup[] groups  = MIDI.TickGroupArray;
            MIDIEvent*  events  = MIDI.MIDIEventArray.Pointer;
            int         maxtick = MIDILoader.maxTick - 1;
            long        emax    = (long)MIDI.MIDIEventArray.Length;
            double      halfWin = WindowTicks * 0.5;
            float       ppt     = (float)pixelsPerTick;
            float       left    = (float)(centerTick - halfWin);

            int viewStart   = Math.Max(0, Math.Min((int)(centerTick - halfWin), maxtick));
            int viewEnd     = Math.Max(0, Math.Min((int)(centerTick + halfWin), maxtick));
            int searchStart = Math.Max(0, viewStart - (int)WindowTicks);
            int searchEnd   = Math.Min(groups.Length - 2, viewEnd + (int)WindowTicks);

            new Span<uint>  (persistentActive, TOTAL_KEYS).Fill(INACTIVE);
            new Span<ushort>(persistentTrack,  TOTAL_KEYS).Clear();

            long msgIdx     = groups[searchStart].offset;
            int  activeCount = 0;
            int  drawn      = 0;

            for (int tick = searchStart; tick <= searchEnd; tick++)
            {
                if (tick > viewEnd && activeCount == 0) break;

                uint count = groups[tick].count;
                for (uint i = 0; i < count && msgIdx < emax; i++)
                {
                    MIDIEvent e      = events[msgIdx++];
                    uint      msg    = (uint)e.message.Value;
                    byte      status = (byte)(msg & 0xF0);
                    if ((status & 0xE0) != 0x80) continue;

                    byte channel = (byte)(msg & 0xF);
                    byte note    = (byte)((msg >> 8) & 0x7F);
                    int  key     = (channel << 7) | note;
                    bool noteOn  = status == 0x90 && ((msg >> 16) & 0x7F) > 0;

                    if (!noteOn)
                    {
                        if (persistentActive[key] != INACTIVE)
                        {
                            if (tick >= viewStart || persistentActive[key] <= (uint)viewEnd)
                            {
                                int x1 = (int)((persistentActive[key] - left) * ppt);
                                int x2 = (int)((tick                  - left) * ppt);
                                DrawLine(x1, x2, 127 - note,
                                         colorTable[(persistentTrack[key] & 0xFF) * 16 + channel],
                                         persistentTrack[key]);
                                drawn++;
                            }
                            persistentActive[key] = INACTIVE;
                            activeCount--;
                        }
                    }
                    else
                    {
                        if (persistentActive[key] != INACTIVE)
                        {
                            if (tick >= viewStart || persistentActive[key] <= (uint)viewEnd)
                            {
                                int x1 = (int)((persistentActive[key] - left) * ppt);
                                int x2 = (int)((tick                  - left) * ppt);
                                DrawLine(x1, x2, 127 - note,
                                         colorTable[(persistentTrack[key] & 0xFF) * 16 + channel],
                                         persistentTrack[key]);
                                drawn++;
                            }
                        }
                        else activeCount++;
                        persistentActive[key] = (uint)tick;
                        persistentTrack[key]  = e.track;
                    }
                }
            }

            for (int key = 0; key < TOTAL_KEYS; key++)
            {
                if (persistentActive[key] == INACTIVE) continue;
                int x1 = (int)((persistentActive[key] - left) * ppt);
                int x2 = (int)((viewEnd               - left) * ppt);
                DrawLine(x1, x2, 127 - (key & 0x7F),
                         colorTable[(persistentTrack[key] & 0xFF) * 16 + (key >> 7)],
                         persistentTrack[key]);
                drawn++;
            }

            renderTickCursor = Math.Min(viewEnd + 1, maxtick);
            renderMsgCursor  = groups[renderTickCursor].offset;
            NotesDrawnLastFrame = drawn;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AdvanceAndDraw(double centerTick, double pixelsPerTick,
                                           int scrollPixels, double rightEdge)
        {
            if (MIDI.MIDIEventArray == null || MIDI.TickGroupArray == null) return;

            TickGroup[] groups  = MIDI.TickGroupArray;
            MIDIEvent*  events  = MIDI.MIDIEventArray.Pointer;
            int         maxtick = MIDILoader.maxTick - 1;
            long        emax    = (long)MIDI.MIDIEventArray.Length;
            float       ppt     = (float)pixelsPerTick;
            float       left    = (float)(centerTick - WindowTicks * 0.5);
            int         stripX1 = texWidth - scrollPixels;
            int         tickEnd = Math.Min((int)rightEdge, maxtick);
            int         drawn   = 0;
            long        msgIdx  = renderMsgCursor;

            for (int tick = renderTickCursor; tick <= tickEnd; tick++)
            {
                uint count = groups[tick].count;
                for (uint i = 0; i < count && msgIdx < emax; i++)
                {
                    MIDIEvent e      = events[msgIdx++];
                    uint      msg    = (uint)e.message.Value;
                    byte      status = (byte)(msg & 0xF0);
                    if ((status & 0xE0) != 0x80) continue;

                    byte channel = (byte)(msg & 0xF);
                    byte note    = (byte)((msg >> 8) & 0x7F);
                    int  key     = (channel << 7) | note;
                    bool noteOn  = status == 0x90 && ((msg >> 16) & 0x7F) > 0;

                    if (!noteOn)
                    {
                        if (persistentActive[key] != INACTIVE)
                        {
                            int x1 = Math.Max(stripX1, (int)((persistentActive[key] - left) * ppt));
                            int x2 = (int)((tick - left) * ppt);
                            if (x2 >= stripX1)
                            {
                                DrawLine(x1, x2, 127 - note,
                                         colorTable[(persistentTrack[key] & 0xFF) * 16 + channel],
                                         persistentTrack[key]);
                                drawn++;
                            }
                            persistentActive[key] = INACTIVE;
                        }
                    }
                    else
                    {
                        if (persistentActive[key] != INACTIVE)
                        {
                            int x1 = Math.Max(stripX1, (int)((persistentActive[key] - left) * ppt));
                            int x2 = (int)((tick - left) * ppt);
                            if (x2 >= stripX1)
                            {
                                DrawLine(x1, x2, 127 - note,
                                         colorTable[(persistentTrack[key] & 0xFF) * 16 + channel],
                                         persistentTrack[key]);
                                drawn++;
                            }
                        }
                        persistentActive[key] = (uint)tick;
                        persistentTrack[key]  = e.track;
                    }
                }
            }

            // flush still-open notes to strip right edge
            for (int key = 0; key < TOTAL_KEYS; key++)
            {
                if (persistentActive[key] == INACTIVE) continue;
                int x1 = Math.Max(stripX1, (int)((persistentActive[key] - left) * ppt));
                int x2 = (int)((tickEnd - left) * ppt);
                if (x2 >= stripX1)
                {
                    DrawLine(x1, x2, 127 - (key & 0x7F),
                             colorTable[(persistentTrack[key] & 0xFF) * 16 + (key >> 7)],
                             persistentTrack[key]);
                    drawn++;
                }
            }

            renderMsgCursor     = msgIdx;
            renderTickCursor    = Math.Min(tickEnd + 1, maxtick);
            NotesDrawnLastFrame = drawn;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateAndDraw(int screenWidth, int screenHeight, int pad)
        {
            Raylib.UpdateTexture(renderTex, texPtr);
            Raylib.DrawTexturePro(renderTex,
                new Raylib_cs.Rectangle(0, 0, texWidth, 128),
                new Raylib_cs.Rectangle(0, pad, screenWidth, screenHeight - pad * 2),
                Vector2.Zero, 0f, Raylib_cs.Color.White);
        }

        public static void Dispose()
        {
            if (texPtr           != null) { NativeMemory.AlignedFree(texPtr);           texPtr          = null; }
            if (zBuffer          != null) { NativeMemory.AlignedFree(zBuffer);          zBuffer         = null; }
            if (persistentActive != null) { NativeMemory.AlignedFree(persistentActive); persistentActive = null; }
            if (persistentTrack  != null) { NativeMemory.AlignedFree(persistentTrack);  persistentTrack  = null; }
            if (activeNoteStart  != null) { NativeMemory.AlignedFree(activeNoteStart);  activeNoteStart  = null; }
            if (activeTrackStart != null) { NativeMemory.AlignedFree(activeTrackStart); activeTrackStart = null; }
            if (colorTable       != null) { NativeMemory.AlignedFree(colorTable);       colorTable       = null; }
            if (renderTex.Id != 0)        { Raylib.UnloadTexture(renderTex);            renderTex        = default; }
        }
    }
}