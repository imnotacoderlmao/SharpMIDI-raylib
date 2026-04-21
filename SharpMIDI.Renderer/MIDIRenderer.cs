using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Raylib_cs;

namespace SharpMIDI
{
    public static unsafe class MIDIRenderer
    {
        private const uint INACTIVE = 0xFFFFFFFF;
        private const int TOTAL_KEYS = 2048;
        private const int BITSET_WORDS = TOTAL_KEYS / 64;
        private const int COLOR_SHIFT = 16;
        private const int Z_SHIFT = 48;

        // Key slot layout:
        //   31-0 = tick  (INACTIVE=0xFFFFFFFF when slot is unused)
        //   47-32 = track (ushort)
        //   63-48 = unused
        // Check active:  (uint)slot != INACTIVE
        // Extract tick:  (uint)slot
        // Extract track: (ushort)(slot >> 32)
        // Pack:          (ulong)track << 32 | tick
        private const ulong KEY_INACTIVE = INACTIVE; // only low 32 bits matter for the check
        // activeKeys replaces activeNoteStart + activeTrackStart (one ulong load vs two stores)
        public  static ulong* activeKeys;
        // persistentKeys replaces persistentActive + persistentTrack (one ulong load in hot render loop)
        private static ulong* persistentKeys;
        private static ulong* openKeyBits;

        private static int tickGroupCursor;
        private static bool isInitialized;
        private static long renderMsgCursor = 0;
        private static int renderTickCursor = 0;

        private static int lastColumn = -1;
        private static double lastTick = 0;

        public static readonly uint[] MIDIColors =
        {
            0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFF00,
            0xFFFF00FF, 0xFF00FFFF, 0xFFFF8000, 0xFF8000FF,
            0xFF0080FF, 0xFF80FF00, 0xFFFF0080, 0xFF00FF80,
            0xFF00FA92, 0xFF00FFFF, 0xFFF7DB05, 0xFF4040FF,
        };

        public static float WindowTicks = 2000f;
        public static int NotesDrawnLastFrame;

        private static Texture2D renderTex;
        // bits 63-48 is priority, 47-16 color (in argb), rest unused
        private static ulong* pixelBuf;
        private static uint* uploadBuf;
        private static int texWidth;
        private static int texSize;

        public static bool forceFullRedraw = true;
        private static uint* colorTable;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong PackKey(uint tick, ushort track) => (ulong)track << 32 | tick;

        public static void Initialize(int width)
        {
            if (renderTex.Id != 0) Raylib.UnloadTexture(renderTex);
            if (pixelBuf  != null) NativeMemory.AlignedFree(pixelBuf);
            if (uploadBuf != null) NativeMemory.AlignedFree(uploadBuf);

            texWidth = width;
            texSize  = width * 128;

            pixelBuf  = (ulong*)NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(ulong)), 64);
            uploadBuf = (uint*) NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(uint)),  64);

            new Span<ulong>(pixelBuf,  texSize).Clear();
            new Span<uint> (uploadBuf, texSize).Clear();

            renderTex = Raylib.LoadTextureFromImage(Raylib.GenImageColor(width, 128, Raylib_cs.Color.Black));
            Raylib.SetTextureFilter(renderTex, TextureFilter.Point);

            if (colorTable == null)
                colorTable = (uint*)NativeMemory.AlignedAlloc(256 * 16 * sizeof(uint), 64);
            for (int tick = 0; tick < 256; tick++)
                for (int c = 0; c < 16; c++)
                    colorTable[tick * 16 + c] = MIDIColors[(tick + c) & 0xF];

            lastColumn      = -1;
            forceFullRedraw = true;
        }

        public static void InitializeForMIDI()
        {
            FreePerMIDIBuffers();

            activeKeys = (ulong*)NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(ulong), 64);
            persistentKeys = (ulong*)NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(ulong), 64);
            openKeyBits = (ulong*)NativeMemory.AlignedAlloc(BITSET_WORDS * sizeof(ulong), 64);

            new Span<ulong>(activeKeys, TOTAL_KEYS).Fill(KEY_INACTIVE);
            new Span<ulong>(persistentKeys, TOTAL_KEYS).Fill(KEY_INACTIVE);
            new Span<ulong>(openKeyBits, BITSET_WORDS).Clear();

            tickGroupCursor = 0;
            renderTickCursor = 0;
            renderMsgCursor = 0;
            lastTick = 0;
            lastColumn = -1;
            isInitialized = true;
            forceFullRedraw = true;
        }

        public static void ResetForUnload()
        {
            FreePerMIDIBuffers();
            tickGroupCursor = 0;
            isInitialized = false;
            forceFullRedraw = true;
            lastColumn = -1;
        }

        private static void FreePerMIDIBuffers()
        {
            if (activeKeys != null) { NativeMemory.AlignedFree(activeKeys); activeKeys = null; }
            if (persistentKeys != null) { NativeMemory.AlignedFree(persistentKeys); persistentKeys = null; }
            if (openKeyBits != null) { NativeMemory.AlignedFree(openKeyBits); openKeyBits = null; }
        }

        public static void SetWindow(float ticks)
        {
            if (Math.Abs(WindowTicks - ticks) > 0.1f)
            {
                WindowTicks = ticks;
                forceFullRedraw = true;
                lastColumn = -1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetOpen(int key)   => openKeyBits[key >> 6] |=  (1ul << (key & 63));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearOpen(int key) => openKeyBits[key >> 6] &= ~(1ul << (key & 63));

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void UpdateStreaming(double tick)
        {
            if (!isInitialized || SynthEvent.messages == null || MIDIEvent.TickGroupArray == null) return;

            TickGroup[] groups = MIDIEvent.TickGroupArray;
            byte* messages = (byte*)SynthEvent.messages.Pointer;
            ushort* tracks = SynthEvent.track.Pointer;
            int gmax = groups.Length - 2;
            int itick = Math.Min((int)tick, gmax);
            long msgIdx = groups[tickGroupCursor].offset;

            while (tickGroupCursor <= itick)
            {
                uint count = groups[tickGroupCursor++].count;
                for (uint i = 0; i < count; i++, msgIdx++)
                {
                    byte* synthev = messages + msgIdx * 3;
                    byte status = (byte)(synthev[0] & 0xF0);
                    if ((status - 0x80u) > 0x10u) continue;
                    uint key = ((uint)(synthev[0] & 0xF) << 7) | synthev[1];
                    bool noteOn = status == 0x90 & synthev[2] > 0;
                    activeKeys[key] = noteOn ? PackKey((uint)(tickGroupCursor - 1), tracks[msgIdx]) : KEY_INACTIVE;
                }
            }
        }

        public static void ResetToTick(double tick)
        {
            if (!isInitialized || SynthEvent.messages == null || MIDIEvent.TickGroupArray == null) return;
            tickGroupCursor = 0;
            lastColumn = -1;
            new Span<ulong>(activeKeys, TOTAL_KEYS).Fill(KEY_INACTIVE);
            new Span<ulong>(openKeyBits, BITSET_WORDS).Clear();
            UpdateStreaming(tick);
            forceFullRedraw = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ScrollLeft(int pixels)
        {
            if ((uint)pixels >= (uint)texWidth) return;
            int keep = texWidth - pixels;
            int keepBytes = keep * sizeof(ulong);
            for (int y = 0; y < 128; y++)
            {
                ulong* row = pixelBuf + y * texWidth;
                Buffer.MemoryCopy(row + pixels, row, keepBytes, keepBytes);
                new Span<ulong>(row + keep, pixels).Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawLine(int x1, int x2, int y, ulong packed)
        {
            if ((uint)y >= 128u) return;
            x1 = Math.Max(x1, 0);
            x2 = Math.Min(x2, texWidth - 1);
            if (x1 > x2) return;

            ulong* pixelptr = pixelBuf + y * texWidth + x1;
            ulong* end = pixelptr + (x2 - x1 + 1);
            while (pixelptr < end)
            {
                if (packed >= *pixelptr) *pixelptr = packed;
                pixelptr++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MakePacked(ushort persistent_track, byte channel) =>
            ((ulong)persistent_track << Z_SHIFT) | ((ulong)colorTable[(persistent_track & 0xFF) * 16 + channel] << COLOR_SHIFT);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawNote(uint startTick, int endTick, int note, ulong packed, float leftedge, float pixelspertick)
        {
            int x1 = (int)((startTick - leftedge) * pixelspertick);
            int x2 = (int)((endTick - leftedge) * pixelspertick);
            if (x2 < 0 || x1 >= texWidth) return;
            DrawLine(x1, x2, 127 - note, packed);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawNoteStrip(uint startTick, int endTick, int note, ulong packed, float leftedge, float pixelspertick, int stripX1)
        {
            int x1 = Math.Max(stripX1, (int)((startTick - leftedge) * pixelspertick));
            int x2 = (int)((endTick - leftedge) * pixelspertick);
            if (x2 < stripX1) return;
            DrawLine(x1, x2, 127 - note, packed);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int screenWidth, int screenHeight, int pad)
        {
            if (!isInitialized) return;

            double tick = MIDIClock.tick;
            double pixelsPerTick = texWidth / (double)WindowTicks;

            int newColumn = (int)(tick * pixelsPerTick) % texWidth;
            if (newColumn < 0) newColumn += texWidth;

            int delta = lastColumn == -1 ? texWidth
                      : newColumn >= lastColumn ? newColumn - lastColumn
                      : (texWidth - lastColumn) + newColumn;

            if (MIDIPlayer.stopping || forceFullRedraw || delta >= texWidth)
            {
                RenderFull(tick, pixelsPerTick);
                forceFullRedraw = false;
            }
            else if (delta > 0)
            {
                ScrollLeft(delta);
                AdvanceAndDraw(tick, pixelsPerTick, delta);
            }

            lastTick   = tick;
            lastColumn = newColumn;
            UpdateAndDraw(screenWidth, screenHeight, pad);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderFull(double centerTick, double pixelsPerTick)
        {
            new Span<ulong>(pixelBuf, texSize).Clear();
            if (SynthEvent.messages == null || MIDIEvent.TickGroupArray == null) return;

            TickGroup[] groups = MIDIEvent.TickGroupArray;
            byte* messages = (byte*)SynthEvent.messages.Pointer;
            ushort* tracks = SynthEvent.track.Pointer;
            int maxtick = MIDILoader.maxTick - 1;
            long eventlen = SynthEvent.messages.Length;
            float pixelspertick = (float)pixelsPerTick;
            float leftedge = (float)(centerTick - WindowTicks * 0.5);

            int viewStart = Math.Clamp((int)(centerTick - WindowTicks * 0.5), 0, maxtick);
            int viewEnd = Math.Clamp((int)(centerTick + WindowTicks * 0.5), 0, maxtick);
            int searchStart = Math.Max(0, viewStart - (int)WindowTicks);
            int searchEnd = Math.Min(groups.Length - 2, viewEnd   + (int)WindowTicks);

            new Span<ulong>(persistentKeys, TOTAL_KEYS).Fill(KEY_INACTIVE);
            new Span<ulong>(openKeyBits, BITSET_WORDS).Clear();

            long msgIdx = groups[searchStart].offset;
            int drawn = 0;
            int openCount = 0;

            for (int tick = searchStart; tick <= searchEnd; tick++)
            {
                if (tick > viewEnd && openCount == 0) break;

                uint count = groups[tick].count;
                for (uint i = 0; i < count && msgIdx < eventlen; i++, msgIdx++)
                {
                    byte* synthev = messages + msgIdx * 3;
                    byte status = (byte)(synthev[0] & 0xF0);
                    if ((status - 0x80u) > 0x10u) continue;

                    byte channel = (byte)(synthev[0] & 0xF);
                    byte note = synthev[1];
                    int key = (channel << 7) | note;
                    bool noteOn = status == 0x90 & synthev[2] > 0;
                    ulong slot = persistentKeys[key];
                    uint startTick = (uint)slot;
                    ushort persistent_track = (ushort)(slot >> 32);

                    if (!noteOn)
                    {
                        if (startTick != INACTIVE)
                        {
                            if (tick >= viewStart || startTick <= (uint)viewEnd)
                            { 
                                DrawNote(startTick, tick, note, MakePacked(persistent_track, channel), leftedge, pixelspertick); 
                                drawn++; 
                            }
                            persistentKeys[key] = KEY_INACTIVE;
                            ClearOpen(key);
                            openCount--;
                        }
                    }
                    else
                    {
                        if (startTick != INACTIVE)
                        {
                            if (tick >= viewStart || startTick <= (uint)viewEnd)
                            { 
                                DrawNote(startTick, tick, note, MakePacked(persistent_track, channel), leftedge, pixelspertick); 
                                drawn++; 
                            }
                        }
                        else 
                        { 
                            SetOpen(key); 
                            openCount++; 
                        }
                        persistentKeys[key] = PackKey((uint)tick, tracks[msgIdx]);
                    }
                }
            }

            FlushOpenNotes(leftedge, pixelspertick, viewEnd, -1, ref drawn);
            renderTickCursor = Math.Min(viewEnd + 1, maxtick);
            renderMsgCursor = groups[renderTickCursor].offset;
            NotesDrawnLastFrame = drawn;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AdvanceAndDraw(double centerTick, double pixelsPerTick, int scrollPixels)
        {
            if (SynthEvent.messages == null || MIDIEvent.TickGroupArray == null) return;

            TickGroup[] groups = MIDIEvent.TickGroupArray;
            byte* messages = (byte*)SynthEvent.messages.Pointer;
            ushort* tracks = SynthEvent.track.Pointer;
            int maxtick = MIDILoader.maxTick - 1;
            long eventlen = SynthEvent.messages.Length;
            float pixelspertick = (float)pixelsPerTick;
            float leftedge = (float)(centerTick - WindowTicks * 0.5);
            int stripX1 = texWidth - scrollPixels;
            int tickEnd = Math.Min((int)(centerTick + WindowTicks * 0.5), maxtick);
            long msgIdx = renderMsgCursor;
            int drawn = 0;

            for (int tick = renderTickCursor; tick <= tickEnd; tick++)
            {
                uint count = groups[tick].count;
                if (count == 0) continue;

                for (uint i = 0; i < count && msgIdx < eventlen; i++, msgIdx++)
                {
                    byte* synthev = messages + msgIdx * 3;
                    byte status = (byte)(synthev[0] & 0xF0);
                    if ((status - 0x80u) > 0x10u) continue;

                    byte channel = (byte)(synthev[0] & 0xF);
                    byte note = synthev[1];
                    int key = (channel << 7) | note;
                    bool noteOn = status == 0x90 & synthev[2] > 0;
                    ulong slot = persistentKeys[key];
                    uint startTick = (uint)slot;
                    ushort persistent_track = (ushort)(slot >> 32);

                    if (!noteOn)
                    {
                        if (startTick != INACTIVE)
                        {
                            DrawNoteStrip(startTick, tick, note, MakePacked(persistent_track, channel), leftedge, pixelspertick, stripX1);
                            drawn++;
                            persistentKeys[key] = KEY_INACTIVE;
                            ClearOpen(key);
                        }
                    }
                    else
                    {
                        if (startTick != INACTIVE)
                        { 
                            DrawNoteStrip(startTick, tick, note, MakePacked(persistent_track, channel), leftedge, pixelspertick, stripX1); 
                            drawn++; 
                        }
                        else 
                        {
                            SetOpen(key);
                        }
                        persistentKeys[key] = PackKey((uint)tick, tracks[msgIdx]);
                    }
                }
            }

            FlushOpenNotes(leftedge, pixelspertick, tickEnd, stripX1, ref drawn);
            renderMsgCursor = msgIdx;
            renderTickCursor = Math.Min(tickEnd + 1, maxtick);
            NotesDrawnLastFrame = drawn;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void FlushOpenNotes(float leftedge, float pixelspertick, int tickEnd, int stripX1, ref int drawn)
        {
            for (int w = 0; w < BITSET_WORDS; w++)
            {
                ulong word = openKeyBits[w];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int key = (w << 6) | bit;
                    ulong slot = persistentKeys[key];
                    uint start = (uint)slot;
                    ushort persistent_track = (ushort)(slot >> 32);
                    byte channel = (byte)(key >> 7);
                    byte note = (byte)(key & 0x7F);
                    ulong packed = MakePacked(persistent_track, channel);
                    if (stripX1 >= 0) 
                    {
                        DrawNoteStrip(start, tickEnd, note, packed, leftedge, pixelspertick, stripX1);
                    }
                    else 
                    {
                        DrawNote(start, tickEnd, note, packed, leftedge, pixelspertick);
                    }
                    drawn++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void BlitToUploadBuf()
        {
            ulong* src = pixelBuf;
            uint* dst = uploadBuf;
            for (int i = 0; i < texSize; i++)
                dst[i] = (uint)(src[i] >> COLOR_SHIFT);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateAndDraw(int screenWidth, int screenHeight, int pad)
        {
            BlitToUploadBuf();
            Raylib.UpdateTexture(renderTex, uploadBuf);
            Raylib.DrawTexturePro(renderTex,
                new Raylib_cs.Rectangle(0, 0, texWidth, 128),
                new Raylib_cs.Rectangle(0, pad, screenWidth, screenHeight - pad * 2),
                Vector2.Zero, 0f, Raylib_cs.Color.White);
        }

        public static void Dispose()
        {
            FreePerMIDIBuffers();
            if (pixelBuf != null) { NativeMemory.AlignedFree(pixelBuf); pixelBuf = null; }
            if (uploadBuf != null) { NativeMemory.AlignedFree(uploadBuf); uploadBuf = null; }
            if (colorTable != null) { NativeMemory.AlignedFree(colorTable); colorTable = null; }
            if (renderTex.Id != 0) { Raylib.UnloadTexture(renderTex); renderTex = default; }
        }
    }
}