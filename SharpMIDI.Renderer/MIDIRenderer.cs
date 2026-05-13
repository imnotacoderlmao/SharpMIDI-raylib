using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Raylib_cs;
namespace SharpMIDI
{
    public static unsafe class MIDIRenderer
    {
        private const uint INACTIVE = 0xFFFFFFFF;
        private const ulong KEY_INACTIVE = INACTIVE;
        private const int TOTAL_KEYS = 2048;
        private const int BITSET_WORDS = TOTAL_KEYS / 64;
 
        // per-key ring buffer for overlap stuff
        private const int QUEUE_DEPTH = 256;
        private const int QUEUE_MASK = QUEUE_DEPTH - 1;
 
        private static ulong* noteQueues;
        private static uint* keyState;
        private static ulong* openKeyBits;
 
        private static bool isInitialized;
        private static long renderMsgCursor = 0;
        private static int renderTickCursor = 0;
        private static int lastColumn = -1;
        private static int lastTick = 0;
 
        public static readonly uint[] MIDIColors =
        [
            0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFF00,
            0xFFFF00FF, 0xFF00FFFF, 0xFFFF8000, 0xFF8000FF,
            0xFF0080FF, 0xFF80FF00, 0xFFFF0080, 0xFF00FF80,
            0xFF00FA92, 0xFF00FFFF, 0xFFF7DB05, 0xFF4040FF,
        ];
 
        public static int WindowTicks = 2000;
        private static int lastWindowTicks = WindowTicks;
        public static int NotesDrawnLastFrame;
 
        private static Texture2D renderTex;
        private static ulong* pixelBuf;
        private static uint* uploadBuf;
        private static int texWidth;
        private static int texSize;
        public static bool forceFullRedraw = true;
 
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong PackKey(uint tick, ushort track) => (ulong)track << 32 | tick;
 
        // Priority in HIGH dword, ulong >= comparison is z-ordered without shifts (hopefully)
        // Color in LOW dword, blit is (uint)pixel, trivially vectorized.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MakePacked(ushort track, byte channel)
        {
            int priority = (track << 4) | channel;
            uint color = MIDIColors[priority >> 2 & 0xF];
            return ((ulong)priority << 32) | color;
        }
 
        public static void Initialize(int width)
        {
            if (renderTex.Id != 0) Raylib.UnloadTexture(renderTex);
            if (pixelBuf != null) NativeMemory.AlignedFree(pixelBuf);
            if (uploadBuf != null) NativeMemory.AlignedFree(uploadBuf);

            texWidth = width;
            texSize = width * 128;
 
            pixelBuf = (ulong*)NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(ulong)), 64);
            uploadBuf = (uint*)NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(uint)), 64);
 
            new Span<ulong>(pixelBuf, texSize).Clear();
            new Span<uint>(uploadBuf, texSize).Clear();
 
            renderTex = Raylib.LoadTextureFromImage(Raylib.GenImageColor(width, 128, Raylib_cs.Color.Black));
            Raylib.SetTextureFilter(renderTex, TextureFilter.Point);
            lastColumn = -1;
            forceFullRedraw = true;
        }
 
        public static void InitializeForMIDI()
        {
            FreePerMIDIBuffers();
 
            noteQueues = (ulong*)NativeMemory.AlignedAlloc(TOTAL_KEYS * QUEUE_DEPTH * sizeof(ulong), 64);
            keyState = (uint*)NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(uint), 64);
            openKeyBits = (ulong*)NativeMemory.AlignedAlloc(BITSET_WORDS * sizeof(ulong), 64);
 
            new Span<uint>(keyState, TOTAL_KEYS).Clear();
            new Span<ulong>(openKeyBits, BITSET_WORDS).Clear();
 
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
            isInitialized = false;
            forceFullRedraw = true;
            lastColumn = -1;
        }
 
        private static void FreePerMIDIBuffers()
        {
            if (noteQueues != null) { NativeMemory.AlignedFree(noteQueues); noteQueues = null; }
            if (keyState != null) { NativeMemory.AlignedFree(keyState); keyState = null; }
            if (openKeyBits != null) { NativeMemory.AlignedFree(openKeyBits); openKeyBits = null; }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetOpen(int key)   => openKeyBits[key >> 6] |=  (1ul << (key & 63));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearOpen(int key) => openKeyBits[key >> 6] &= ~(1ul << (key & 63));
 
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
                if (packed >= *pixelptr) 
                    *pixelptr = packed; 
                pixelptr++; 
            }
        }
        
        //private static void DrawNote(uint startTick, int endTick, int note, ulong packed,
        //                             float leftedge, float pixelspertick)
        //{
        //    int x1 = (int)((startTick - leftedge) * pixelspertick);
        //    int x2 = (int)((endTick - leftedge) * pixelspertick);
        //    if (x2 < 0 || x1 >= texWidth) return;
        //    DrawLine(x1, x2, 127 - note, packed);
        //}

        public static void Render(int screenWidth, int screenHeight, int tick, int pad)
        {
            if (!isInitialized) return;
 
            double pixelsPerTick = texWidth / (double)WindowTicks;
            int newColumn = (int)(tick * pixelsPerTick) % texWidth;
            if (newColumn < 0) newColumn += texWidth; 
            
            int delta = lastColumn == -1 ? texWidth
                      : newColumn >= lastColumn ? newColumn - lastColumn
                      : (texWidth - lastColumn) + newColumn;
 
            if (Math.Abs(WindowTicks - lastWindowTicks) > 0.1f)
            {
                forceFullRedraw = true;
                lastColumn = -1;
                lastWindowTicks = WindowTicks;
            }
 
            if (forceFullRedraw || lastTick > tick)
            {
                RenderFull(tick, pixelsPerTick);
                forceFullRedraw = false;
            }
            else if (delta > 0)
            {
                ScrollLeft(delta);
                AdvanceAndDraw(tick, pixelsPerTick, delta);
            }
 
            lastTick = tick;
            lastColumn = newColumn;
            UpdateAndDraw(screenWidth, screenHeight, pad);
        }
 
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderFull(int centerTick, double pixelsPerTick)
        {
            new Span<ulong>(pixelBuf, texSize).Clear();
            if (SynthEvent.messages == null || MIDIEvent.TickGroupArray == null) return;
 
            TickGroup[] groups = MIDIEvent.TickGroupArray;
            byte* messages = (byte*)SynthEvent.messages.Pointer;
            ushort* tracks = (WindowManager.trackcolors && SynthEvent.track != null) ? SynthEvent.track.Pointer : null;
            bool useTrack = tracks != null;
            int maxtick = MIDILoader.maxTick - 1;
            float pixelspertick = (float)pixelsPerTick;
            int leftedge = centerTick - (WindowTicks / 2);
 
            int viewStart = Math.Clamp(centerTick - (WindowTicks / 2), 0, maxtick);
            int viewEnd = Math.Clamp(centerTick + (WindowTicks / 2), 0, maxtick);
            int searchStart = Math.Max(0, viewStart - WindowTicks);
            int searchEnd = Math.Min(groups.Length - 2, viewEnd);
 
            new Span<uint>(keyState, TOTAL_KEYS).Clear();
            new Span<ulong>(openKeyBits, BITSET_WORDS).Clear();
 
            long msgIdx = groups[searchStart].offset;
            byte* synthev = messages + msgIdx * 3;
            int drawn = 0;
            int openCount = 0;
            float tickX2f = (searchStart - leftedge) * pixelspertick;
            for (int tick = searchStart; tick <= searchEnd; tick++, tickX2f += pixelspertick)
            {
                if (tick > viewEnd && openCount == 0) break;
                long nextOff = groups[tick + 1].offset;
                if (msgIdx == nextOff) continue;
                int tickX2 = (int)tickX2f;
                while (msgIdx < nextOff)
                {
                    byte status = (byte)(synthev[0] & 0xF0);
                    if ((status - 0x80u) <= 0x10u)
                    {
                        byte channel = (byte)(synthev[0] & 0xF);
                        byte note = synthev[1];
                        int key = (channel << 7) | note;
                        bool noteOn = status == 0x90 & synthev[2] > 0; 
                        uint ks = keyState[key];
                        uint cnt = ks & 0xFFFFu;
                        uint head = ks >> 16;
                        int qbase = key * QUEUE_DEPTH;
                        if (!noteOn)
                        {
                            if (cnt != 0)
                            {
                                ulong slot = noteQueues[qbase + (int)head];
                                uint newCnt = cnt - 1;
                                keyState[key] = (((head + 1) & QUEUE_MASK) << 16) | newCnt;
                                if (tick >= viewStart || (uint)slot <= (uint)viewEnd)
                                {
                                     int x1 = (int)(((uint)slot - leftedge) * pixelspertick);
                                     if (tickX2 >= 0 && x1 < texWidth)
                                     {
                                         DrawLine(x1, tickX2, 127 - note, MakePacked((ushort)(slot >> 32), channel));
                                         drawn++;
                                     }
                                }
                                if (newCnt == 0) { ClearOpen(key); openCount--; }
                            }
                        }
                        else
                        {
                            ushort trk = useTrack ? tracks[msgIdx] : (ushort)0;
                            if (cnt == 0) { SetOpen(key); openCount++; }
                            if (cnt < QUEUE_DEPTH)
                            {
                                noteQueues[qbase + (int)((head + cnt) & QUEUE_MASK)] = PackKey((uint)tick, trk);
                                keyState[key] = (head << 16) | (cnt + 1);
                            }
                            else // full: overwrite oldest entry, advance head
                            {
                                noteQueues[qbase + (int)head] = PackKey((uint)tick, trk);
                                keyState[key] = (((head + 1) & QUEUE_MASK) << 16) | cnt;
                            }
                        }
                    }
                    msgIdx++;
                    synthev += 3;
                }
            }
 
            FlushOpenNotes(leftedge, pixelspertick, viewEnd, -1, ref drawn);
            renderTickCursor = Math.Min(viewEnd + 1, maxtick);
            renderMsgCursor = msgIdx;
            NotesDrawnLastFrame = drawn;
        }
 
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AdvanceAndDraw(int centerTick, double pixelsPerTick, int scrollPixels)
        {
            if (SynthEvent.messages == null || MIDIEvent.TickGroupArray == null) return;
 
            TickGroup[] groups = MIDIEvent.TickGroupArray;
            byte* messages = (byte*)SynthEvent.messages.Pointer;
            ushort* tracks = (WindowManager.trackcolors && SynthEvent.track != null) ? SynthEvent.track.Pointer : null;
            bool useTrack = tracks != null;
            int maxtick = MIDILoader.maxTick - 1;
            float pixelspertick = (float)pixelsPerTick;
            int leftedge = centerTick - WindowTicks / 2;
            int stripX1 = texWidth - scrollPixels;
            int tickEnd = Math.Min(centerTick + WindowTicks/2, maxtick);
            long msgIdx = renderMsgCursor;
            byte* synthev = messages + msgIdx * 3;
            int drawn = 0;
 
            float tickX2f = (renderTickCursor - leftedge) * pixelspertick;
            for (int tick = renderTickCursor; tick <= tickEnd; tick++, tickX2f += pixelspertick)
            {
                long nextOff = groups[tick + 1].offset;
                if (msgIdx == nextOff) continue; // skip empty tick
                int tickX2 = (int)((tick - leftedge) * pixelspertick);
                bool tickInStrip = tickX2 >= stripX1;
                while (msgIdx < nextOff)
                {
                    byte status = (byte)(synthev[0] & 0xF0);
                    if ((status - 0x80u) <= 0x10u)
                    {
                        byte channel = (byte)(synthev[0] & 0xF);
                        byte note = synthev[1];
                        int key = (channel << 7) | note;
                        bool noteOn = status == 0x90 & synthev[2] > 0;
                        uint ks = keyState[key];
                        uint cnt = ks & 0xFFFFu;
                        uint head = ks >> 16;
                        int qbase = key * QUEUE_DEPTH;
                        if (!noteOn)
                        {
                            if (cnt != 0)
                            {
                                ulong slot = noteQueues[qbase + (int)head];
                                uint newCnt = cnt - 1;
                                keyState[key] = (((head + 1) & (uint)QUEUE_MASK) << 16) | newCnt;
                                if (tickInStrip)
                                {
                                    int x1 = Math.Max(stripX1, (int)(((uint)slot - leftedge) * pixelspertick));
                                    DrawLine(x1, tickX2, 127 - note, MakePacked((ushort)(slot >> 32), channel));
                                    drawn++;
                                }
                                if (newCnt == 0) 
                                    ClearOpen(key);
                            }
                        }
                        else
                        {
                            ushort trk = useTrack ? tracks[msgIdx] : (ushort)0;
                            if (cnt == 0) 
                                SetOpen(key);
                            if (cnt < QUEUE_DEPTH)
                            {
                                noteQueues[qbase + (int)((head + cnt) & (uint)QUEUE_MASK)] = PackKey((uint)tick, trk);
                                keyState[key] = (head << 16) | (cnt + 1);
                            }
                            else
                            {
                                noteQueues[qbase + (int)head] = PackKey((uint)tick, trk);
                                keyState[key] = (((head + 1) & (uint)QUEUE_MASK) << 16) | cnt;
                            }
                        }
                    }
                    msgIdx++;
                    synthev += 3;
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
            int tickX2 = (int)((tickEnd - leftedge) * pixelspertick);
            if (stripX1 >= 0)
            {
                if (tickX2 < stripX1) return;
                for (int wordidx = 0; wordidx < BITSET_WORDS; wordidx++)
                {
                    ulong word = openKeyBits[wordidx];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int key = (wordidx << 6) | bit;
                        uint ks = keyState[key];
                        int cnt = (int)(ks & 0xFFFFu);
                        int head = (int)(ks >> 16);
                        int qbase = key * QUEUE_DEPTH;
                        byte channel = (byte)(key >> 7);
                        byte note = (byte)(key & 0x7F);
                        for (int d = 0; d < cnt; d++)
                        {
                            ulong slot = noteQueues[qbase + ((head + d) & QUEUE_MASK)];
                            int x1 = Math.Max(stripX1, (int)(((uint)slot - leftedge) * pixelspertick));
                            DrawLine(x1, tickX2, 127 - note, MakePacked((ushort)(slot >> 32), channel));
                            drawn++;
                        }
                    }
                }
            }
            else
            {
                if (tickX2 < 0) return;
                for (int wordidx = 0; wordidx < BITSET_WORDS; wordidx++)
                {
                    ulong word = openKeyBits[wordidx];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int key = (wordidx << 6) | bit;
                        uint ks = keyState[key];
                        int cnt = (int)(ks & 0xFFFFu);
                        int head = (int)(ks >> 16);
                        int qbase = key * QUEUE_DEPTH;
                        byte channel = (byte)(key >> 7);
                        byte note = (byte)(key & 0x7F);
                        for (int d = 0; d < cnt; d++)
                        {
                            ulong slot = noteQueues[qbase + ((head + d) & QUEUE_MASK)];
                            int x1 = (int)(((uint)slot - leftedge) * pixelspertick);
                            if (x1 < texWidth)
                                DrawLine(x1, tickX2, 127 - note, MakePacked((ushort)(slot >> 32), channel));
                            drawn++;
                        }
                    }
                }
            }
        }
 
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void BlitToUploadBuf()
        {
            ulong* src = pixelBuf;
            uint* dst = uploadBuf;
            for (int i = 0; i < texSize; i++)
                dst[i] = (uint)src[i];
        }

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
            if (renderTex.Id != 0) { Raylib.UnloadTexture(renderTex); renderTex = default; }
        }
    }
}
