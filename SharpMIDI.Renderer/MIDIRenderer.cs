using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Raylib_cs;
using System.Numerics;

namespace SharpMIDI
{
    public static unsafe class MIDIRenderer
    {
        private const uint INACTIVE   = 0xFFFFFFFF;
        private const uint BLACK      = 0xFF000000;
        private const int TOTAL_KEYS = 2048;

        private static uint* activeNoteStart;
        private static ushort* activeTrackStart;
        private static uint* persistentActive;   // state at renderTickCursor — base for RenderNewColumn
        private static ushort* persistentTrack;
        private static uint* renderActive;       // scratch copy mutated by RenderRange — never kept
        private static ushort* renderTrack;

        private static int tickGroupCursor;
        private static bool isInitialized;
        private static long renderMsgCursor   = 0;
        private static int renderTickCursor  = 0;
        private static double subPixelRemainder = 0;

        public static readonly uint[] MIDIColors =
        {
            0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFF00,
            0xFFFF00FF, 0xFF00FFFF, 0xFFFF8000, 0xFF8000FF,
            0xFF0080FF, 0xFF80FF00, 0xFFFF0080, 0xFF00FF80,
            0xFF00FA92, 0xFF00FFFF, 0xFFF7DB05, 0xFF4040FF,
        };

        public static float WindowTicks         { get; private set; } = 2000f;
        public static int NotesDrawnLastFrame { get; private set; }

        private static Texture2D renderTex;
        private static uint* texPtr;
        private static ushort* zBuffer;
        private static int texWidth;
        private static int texSize;

        private static double lastCenterTick;
        public static bool forceFullRedraw = true;

        private static uint* colorTable; // [track & 0xFF][channel], 256*16*4 = 16KB

        public static void Initialize(int width)
        {
            if (renderTex.Id != 0) Raylib.UnloadTexture(renderTex);
            if (texPtr  != null) NativeMemory.AlignedFree(texPtr);
            if (zBuffer != null) NativeMemory.AlignedFree(zBuffer);

            texWidth = width;
            texSize  = width * 128;

            texPtr  = (uint*) NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(uint)),   64);
            zBuffer = (ushort*)NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(ushort)), 64);

            renderTex = Raylib.LoadTextureFromImage(Raylib.GenImageColor(width, 128, Raylib_cs.Color.Black));
            Raylib.SetTextureFilter(renderTex, TextureFilter.Point);
            new Span<uint>(texPtr, texSize).Fill(BLACK);
            new Span<ushort>(zBuffer, texSize).Clear();

            if (colorTable == null)
                colorTable = (uint*)NativeMemory.AlignedAlloc(256 * 16 * sizeof(uint), 64);
            for (int t = 0; t < 256; t++)
                for (int c = 0; c < 16; c++)
                    colorTable[t * 16 + c] = MIDIColors[(t + c) & 0xF];
        }

        public static void InitializeForMIDI()
        {
            if (activeNoteStart != null) NativeMemory.AlignedFree(activeNoteStart);
            if (activeTrackStart != null) NativeMemory.AlignedFree(activeTrackStart);
            if (persistentActive != null) NativeMemory.AlignedFree(persistentActive);
            if (persistentTrack != null) NativeMemory.AlignedFree(persistentTrack);
            if (renderActive != null) NativeMemory.AlignedFree(renderActive);
            if (renderTrack != null) NativeMemory.AlignedFree(renderTrack);

            activeNoteStart = (uint*) NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(uint), 64);
            activeTrackStart = (ushort*) NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(ushort), 64);
            persistentActive = (uint*) NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(uint), 64);
            persistentTrack = (ushort*) NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(ushort), 64);
            renderActive = (uint*) NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(uint), 64);
            renderTrack = (ushort*) NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(ushort), 64);

            new Span<uint>(activeNoteStart,  TOTAL_KEYS).Fill(INACTIVE);
            new Span<ushort>(activeTrackStart, TOTAL_KEYS).Clear();
            new Span<uint>(persistentActive, TOTAL_KEYS).Fill(INACTIVE);
            new Span<ushort>(persistentTrack,  TOTAL_KEYS).Clear();

            tickGroupCursor = 0;
            renderTickCursor = 0;
            renderMsgCursor = 0;
            subPixelRemainder = 0;
            lastCenterTick = 0;
            isInitialized = true;
            forceFullRedraw = true;
        }

        public static void ResetForUnload()
        {
            if (activeNoteStart != null) { NativeMemory.AlignedFree(activeNoteStart); activeNoteStart = null; }
            if (activeTrackStart != null) { NativeMemory.AlignedFree(activeTrackStart); activeTrackStart = null; }
            if (persistentActive != null) { NativeMemory.AlignedFree(persistentActive); persistentActive = null; }
            if (persistentTrack != null) { NativeMemory.AlignedFree(persistentTrack); persistentTrack = null; }
            if (renderActive != null) { NativeMemory.AlignedFree(renderActive); renderActive = null; }
            if (renderTrack != null) { NativeMemory.AlignedFree(renderTrack); renderTrack = null; }
            tickGroupCursor = 0;
            isInitialized = false;
            forceFullRedraw = true;
            subPixelRemainder = 0;
        }

        public static void SetWindow(float ticks)
        {
            if (Math.Abs(WindowTicks - ticks) > 0.1f)
            {
                WindowTicks = ticks;
                forceFullRedraw = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void UpdateStreaming(int tick)
        {
            if (!isInitialized || MIDI.MIDIEventArray == null || MIDI.TickGroupArray == null) return;

            TickGroup[] groups = MIDI.TickGroupArray;
            MIDIEvent* events = MIDI.MIDIEventArray.Pointer;
            uint* notestart = activeNoteStart;
            ushort* trackstart = activeTrackStart;
            int gmax = groups.Length - 2;

            long msgIdx = groups[tickGroupCursor].offset;
            while (tickGroupCursor <= Math.Min(tick, gmax))
            {
                uint count = groups[tickGroupCursor].count;
                for (uint i = 0; i < count; i++)
                {
                    MIDIEvent e = events[msgIdx++];
                    uint msg = (uint)e.message.Value;
                    byte status = (byte)(msg & 0xF0);
                    if ((status & 0xE0) != 0x80) 
                        continue;
                    uint key = ((msg & 0xF) << 7) | ((msg >> 8) & 0x7F);
                    bool noteOn = status == 0x90 && ((msg >> 16) & 0x7F) > 0;
                    if (noteOn) 
                    { 
                        notestart[key] = (uint)tickGroupCursor; 
                        trackstart[key] = e.track; 
                    }
                    else
                    {
                        notestart[key] = INACTIVE;
                    }
                }
                tickGroupCursor++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ResetToTick(int tick)
        {
            if (!isInitialized || MIDI.MIDIEventArray == null || MIDI.TickGroupArray == null) return;
            tickGroupCursor   = 0;
            subPixelRemainder = 0;
            new Span<uint>  (activeNoteStart,  TOTAL_KEYS).Fill(INACTIVE);
            new Span<ushort>(activeTrackStart, TOTAL_KEYS).Clear();
            UpdateStreaming(tick);
            forceFullRedraw = true;
        }

        // Row-by-row scroll — the texture is strided, a bulk copy bleeds rows into each other
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ScrollLeft(int pixels)
        {
            if (pixels <= 0 || pixels >= texWidth) return;
            int keep = texWidth - pixels;
            int keepBytesC = keep * sizeof(uint);
            int keepBytesZ = keep * sizeof(ushort);
            for (int y = 0; y < 128; y++)
            {
                int offset = y * texWidth;
                uint* row = texPtr  + offset;
                ushort* rowZ = zBuffer + offset;
                Buffer.MemoryCopy(row  + pixels, row,  keepBytesC, keepBytesC);
                Buffer.MemoryCopy(rowZ + pixels, rowZ, keepBytesZ, keepBytesZ);
                new Span<uint>  (row  + keep, pixels).Fill(BLACK);
                new Span<ushort>(rowZ + keep, pixels).Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawLine(int x1, int x2, int y, uint color, ushort priority)
        {
            if ((uint)y >= 128u || x2 < 0 || x1 >= texWidth) return;
            x1 = Math.Max(0, x1);
            x2 = Math.Min(texWidth - 1, x2);
            if (x1 > x2) return;
            int offset = y * texWidth + x1;
            int width  = x2 - x1 + 1;
            uint* pixels = texPtr  + offset;
            ushort* zBuf   = zBuffer + offset;
            for (int i = 0; i < width; i++)
            {
                if (priority >= zBuf[i]) 
                { 
                    pixels[i] = color; 
                    zBuf[i] = priority; 
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawNote(int startTick, int endTick, int note, ushort track, byte channel,
                                     int currentTick, float pixelsPerTick, float centerOffset)
        {
            int x1 = (int)((startTick - currentTick + centerOffset) * pixelsPerTick);
            int x2 = (int)((endTick - currentTick + centerOffset) * pixelsPerTick);
            if (x2 >= 0 && x1 < texWidth)
                DrawLine(x1, x2, 127 - note, colorTable[(track & 0xFF) * 16 + channel], track);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int screenWidth, int screenHeight, int pad)
        {
            if (!isInitialized || MIDI.MIDIEventArray == null || MIDI.TickGroupArray == null)
            {
                if (MIDIPlayer.stopping)
                {
                    new Span<uint>(texPtr,  texSize).Fill(BLACK);
                    new Span<ushort>(zBuffer, texSize).Clear();
                }
                UpdateAndDraw(screenWidth, screenHeight, pad);
                return;
            }

            double currentTick = MIDIClock.tick;
            double tickDelta = currentTick - lastCenterTick;
            double pixelsPerTick = texWidth / (double)WindowTicks;

            // accumulate sub-pixel remainder so scroll is exact regardless of framerate
            double exactPixels = tickDelta * pixelsPerTick + subPixelRemainder;
            int scroll = (int)exactPixels;

            if (MIDIPlayer.stopping || forceFullRedraw)
            {
                RenderFull(currentTick, pixelsPerTick);
                lastCenterTick = currentTick;
                subPixelRemainder = 0;
                forceFullRedraw = false;
            }
            else if (Math.Abs(tickDelta) > WindowTicks * 0.3) // seek
            {
                RenderFull(currentTick, pixelsPerTick);
                lastCenterTick = currentTick;
                subPixelRemainder = 0;
            }
            else if (scroll >= 1)
            {
                ScrollLeft(scroll);
                lastCenterTick += scroll / pixelsPerTick;
                subPixelRemainder = exactPixels - scroll;
                RenderNewColumn(lastCenterTick, pixelsPerTick, scroll);
            }
            else
            {
                subPixelRemainder = exactPixels; // carry forward, nothing to draw yet
            }

            UpdateAndDraw(screenWidth, screenHeight, pad);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderFull(double centerTick, double pixelsPerTick)
        {
            new Span<uint>(texPtr,  texSize).Fill(BLACK);
            new Span<ushort>(zBuffer, texSize).Clear();

            TickGroup[] groups  = MIDI.TickGroupArray;
            MIDIEvent* events  = MIDI.MIDIEventArray.Pointer;
            int maxtick = MIDILoader.maxTick - 1;
            long emax    = (long)MIDI.MIDIEventArray.Length;
            double halfWin = WindowTicks * 0.5;

            int viewStart = Math.Max(0, Math.Min((int)(centerTick - halfWin), maxtick));
            int viewEnd = Math.Max(0, Math.Min((int)(centerTick + halfWin), maxtick));
            int searchStart = Math.Max(0, viewStart - (int)WindowTicks);

            // persistentActive = state at renderTickCursor (right edge of viewport)
            // It must not be touched by RenderRange, so we use renderActive as the scratch copy
            new Span<uint>(persistentActive, TOTAL_KEYS).Fill(INACTIVE);
            new Span<ushort>(persistentTrack, TOTAL_KEYS).Clear();

            // build state at viewStart into renderActive (scratch for RenderRange)
            new Span<uint>(renderActive, TOTAL_KEYS).Fill(INACTIVE);
            new Span<ushort>(renderTrack, TOTAL_KEYS).Clear();
            long viewStartMsgIdx = BuildActiveState(
                groups, events, groups[searchStart].offset, emax,
                searchStart, viewStart, renderActive, renderTrack);

            // build persistentActive up to renderTickCursor (right edge) from the same search base
            renderTickCursor = Math.Min(viewEnd + 1, maxtick);
            BuildActiveState(groups, events, groups[searchStart].offset, emax,
                             searchStart, renderTickCursor, persistentActive, persistentTrack);
            renderMsgCursor = groups[renderTickCursor].offset;

            int drawn = 0;
            RenderRange(groups, events, viewStartMsgIdx, emax, viewStart, viewEnd,
                        renderActive, renderTrack, pixelsPerTick, ref drawn);
            NotesDrawnLastFrame = drawn;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderNewColumn(double centerTick, double pixelsPerTick, int scrollPixels)
        {
            TickGroup[] groups = MIDI.TickGroupArray;
            MIDIEvent* events = MIDI.MIDIEventArray.Pointer;
            int maxtick = MIDILoader.maxTick;
            long eventmax = (long)MIDI.MIDIEventArray.Length;

            double halfWin = WindowTicks * 0.5;
            double rightEdge = centerTick + halfWin;
            double columnStart = rightEdge - scrollPixels / pixelsPerTick;
            int tickStart = Math.Max(0,        Math.Min((int)columnStart, maxtick));
            int tickEnd = Math.Max(tickStart, Math.Min((int)rightEdge,  maxtick));

            // advance persistentActive forward — O(new ticks only)
            long msgIdx = renderMsgCursor;
            for (int t = renderTickCursor; t <= tickEnd; t++)
            {
                uint count = groups[t].count;
                for (uint i = 0; i < count && msgIdx < eventmax; i++)
                {
                    MIDIEvent e = events[msgIdx++];
                    uint msg = (uint)e.message.Value;
                    byte status = (byte)(msg & 0xF0);
                    if ((status & 0xE0) != 0x80) 
                        continue;
                    int key = (int)(((msg & 0xF) << 7) | ((msg >> 8) & 0x7F));
                    bool noteOn = status == 0x90 && ((msg >> 16) & 0x7F) > 0;
                    if (noteOn) 
                    { 
                        persistentActive[key] = (uint)t; 
                        persistentTrack[key] = e.track; 
                    }
                    else          
                    {
                        persistentActive[key] = INACTIVE;
                    }
                }
            }
            renderMsgCursor  = msgIdx;
            renderTickCursor = Math.Min(tickEnd + 1, maxtick);

            // copy persistentActive into renderActive — RenderRangeColumn mutates it, persistent stays clean
            Buffer.MemoryCopy(persistentActive, renderActive, TOTAL_KEYS * sizeof(uint),   TOTAL_KEYS * sizeof(uint));
            Buffer.MemoryCopy(persistentTrack,  renderTrack,  TOTAL_KEYS * sizeof(ushort), TOTAL_KEYS * sizeof(ushort));

            int drawn = 0;
            RenderRangeColumn(groups, events, groups[tickStart].offset, eventmax,
                              tickStart, tickEnd, columnStart, rightEdge,
                              renderActive, renderTrack, pixelsPerTick, ref drawn);
            NotesDrawnLastFrame = drawn;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static long BuildActiveState(TickGroup[] groups, MIDIEvent* events, long msgIdx, long emax,
                                             int startTick, int endTick, uint* active, ushort* track)
        {
            for (int tick = startTick; tick < endTick && tick < groups.Length - 1; tick++)
            {
                uint count = groups[tick].count;
                for (uint i = 0; i < count && msgIdx < emax; i++)
                {
                    MIDIEvent e = events[msgIdx++];
                    uint msg = (uint)e.message.Value;
                    byte status = (byte)(msg & 0xF0);
                    if ((status & 0xE0) != 0x80) 
                        continue;
                    int key = (int)(((msg & 0xF) << 7) | ((msg >> 8) & 0x7F));
                    bool noteOn = status == 0x90 && ((msg >> 16) & 0x7F) > 0;
                    if (noteOn) 
                    { 
                        active[key] = (uint)tick; 
                        track[key] = e.track; 
                    }
                    else 
                    {
                        active[key] = INACTIVE;
                    }
                }
            }
            return msgIdx;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderRange(TickGroup[] groups, MIDIEvent* events, long msgIdx, long eventmax, int viewStart, int viewEnd,
                                        uint* active, ushort* track, double pixelsPerTick, ref int drawn)
        {
            int currentTick = (int)MIDIClock.tick;
            float ppt = (float)pixelsPerTick;
            float centerOffset = WindowTicks * 0.5f;
            int searchEnd = Math.Min(groups.Length - 2, viewEnd + (int)WindowTicks);

            int activeCount = 0;
            for (int k = 0; k < TOTAL_KEYS; k++)
                if (active[k] != INACTIVE) activeCount++;

            for (int tick = viewStart; tick <= searchEnd; tick++)
            {
                if (tick > viewEnd && activeCount == 0) 
                    break;

                uint count = groups[tick].count;
                for (uint i = 0; i < count && msgIdx < eventmax; i++)
                {
                    MIDIEvent e = events[msgIdx++];
                    uint msg = (uint)e.message.Value;
                    byte status = (byte)(msg & 0xF0);
                    if ((status & 0xE0) != 0x80) 
                        continue;

                    byte channel = (byte)(msg & 0xF);
                    byte note = (byte)((msg >> 8) & 0x7F);
                    int  key = (channel << 7) | note;
                    bool noteOn = status == 0x90 && ((msg >> 16) & 0x7F) > 0;

                    if (!noteOn)
                    {
                        if (active[key] != INACTIVE)
                        {
                            DrawNote((int)active[key], tick, note, track[key], channel,
                                     currentTick, ppt, centerOffset);
                            active[key] = INACTIVE;
                            activeCount--;
                            drawn++;
                        }
                    }
                    else
                    {
                        if (active[key] != INACTIVE)
                        {
                            DrawNote((int)active[key], tick, note, track[key], channel,
                                     currentTick, ppt, centerOffset);
                            drawn++;
                        }
                        else activeCount++;
                        active[key] = (uint)tick;
                        track[key]  = e.track;
                    }
                }
            }

            for (int key = 0; key < TOTAL_KEYS; key++)
            {
                if (active[key] == INACTIVE) continue;
                DrawNote((int)active[key], viewEnd, key & 0x7F, track[key], (byte)(key >> 7),
                         currentTick, ppt, centerOffset);
                drawn++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderRangeColumn(TickGroup[] groups, MIDIEvent* events, long msgIdx, long emax, int tickStart, int tickEnd, 
                                 double columnStart, double columnEnd, uint* active, ushort* track, double pixelsPerTick, ref int drawn)
        {
            int currentTick = (int)MIDIClock.tick;
            float ppt = (float)pixelsPerTick;
            float centerOffset = WindowTicks * 0.5f;

            for (int tick = tickStart; tick <= tickEnd && tick < groups.Length - 1; tick++)
            {
                uint count = groups[tick].count;
                for (uint i = 0; i < count && msgIdx < emax; i++)
                {
                    MIDIEvent e = events[msgIdx++];
                    uint msg = (uint)e.message.Value;
                    byte status = (byte)(msg & 0xF0);
                    if ((status & 0xE0) != 0x80) 
                        continue;

                    byte channel = (byte)(msg & 0xF);
                    byte note = (byte)((msg >> 8) & 0x7F);
                    int  key = (channel << 7) | note;
                    bool noteOn = status == 0x90 && ((msg >> 16) & 0x7F) > 0;

                    if (!noteOn)
                    {
                        if (active[key] != INACTIVE)
                        {
                            int startT = (int)active[key];
                            if (tick >= columnStart && startT <= columnEnd)
                            {
                                DrawNote(startT, tick, note, track[key], channel,
                                         currentTick, ppt, centerOffset);
                                drawn++;
                            }
                            active[key] = INACTIVE;
                        }
                    }
                    else
                    {
                        if (active[key] != INACTIVE)
                        {
                            int startT = (int)active[key];
                            if (tick >= columnStart && startT <= columnEnd)
                            {
                                DrawNote(startT, tick, note, track[key], channel,
                                         currentTick, ppt, centerOffset);
                                drawn++;
                            }
                        }
                        active[key] = (uint)tick;
                        track[key]  = e.track;
                    }
                }
            }

            for (int key = 0; key < TOTAL_KEYS; key++)
            {
                if (active[key] == INACTIVE || active[key] > columnEnd) continue;
                DrawNote((int)active[key], tickEnd, key & 0x7F, track[key], (byte)(key >> 7),
                         currentTick, ppt, centerOffset);
                drawn++;
            }
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
            if (texPtr != null) { NativeMemory.AlignedFree(texPtr); texPtr = null; }
            if (zBuffer != null) { NativeMemory.AlignedFree(zBuffer); zBuffer = null; }
            if (persistentActive != null) { NativeMemory.AlignedFree(persistentActive); persistentActive = null; }
            if (persistentTrack != null) { NativeMemory.AlignedFree(persistentTrack); persistentTrack = null; }
            if (renderActive != null) { NativeMemory.AlignedFree(renderActive); renderActive = null; }
            if (renderTrack != null) { NativeMemory.AlignedFree(renderTrack); renderTrack = null; }
            if (activeNoteStart != null) { NativeMemory.AlignedFree(activeNoteStart); activeNoteStart = null; }
            if (activeTrackStart != null) { NativeMemory.AlignedFree(activeTrackStart); activeTrackStart = null; }
            if (colorTable != null) { NativeMemory.AlignedFree(colorTable); colorTable = null; }
            if (renderTex.Id != 0) { Raylib.UnloadTexture(renderTex); renderTex = default; }
        }
    }
}