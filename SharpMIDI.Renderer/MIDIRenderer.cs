using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Raylib_cs;
using System.Runtime.Intrinsics;

namespace SharpMIDI
{
    public static unsafe class MIDIRenderer
    {
        private const int TOTAL_KEYS = 2048; // 128 keys, 16 channels
        private const int BITSET_WORDS = TOTAL_KEYS / 64;
        private const int COLOR_COUNT = 256;
        private const int MAX_DEPTH = 64; // max simultaneous notes per key
        private const int DEPTH_MASK = MAX_DEPTH - 1;
        private const int DEPTH_SHIFT = 6; // log2(MAX_DEPTH)

        private struct NoteEntry
        {
            public uint StartTick;
            public uint Color;      // resolved ARGB
        }

        private static NoteEntry* notePool;
        private static uint* keyState;     // high 16 = head, low 16 = count
        private static ulong* openKeyBits;  // bitset: keys with ≥1 active note
        private static uint* colorLut;     // channel/track index points to ARGB

        // texture buffers, should beproportional to screen width
        private static uint* pixelBuf;
        private static ushort* priorityBuf;
        private static uint** rowPix;
        private static ushort** rowPri;

        private static int texWidth;
        private static int texSize;
        private static Texture2D renderTex;
        private static int ringOffsetX;

        private static bool isInitialized;
        private static long renderMsgCursor;
        private static int renderTickCursor;
        private static long currentLeftEdgePixel;
        private static int lastTick;

        public static int  WindowTicks = 2000;
        private static int lastWindowTicks = WindowTicks;
        public static int  NotesDrawnLastFrame;
        public static bool forceFullRedraw = true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ZPriority(int pixelLen)
        {
            uint c = (uint)pixelLen < 65534u ? (uint)pixelLen : 65534u;
            return (ushort)(65535u - c);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetOpen(int key) => openKeyBits[key >> 6] |= 1ul << (key & 63);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearOpen(int key) => openKeyBits[key >> 6] &= ~(1ul << (key & 63));

        // Generic aligned-free helper for unmanaged pointer fields.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SafeFree<T>(ref T* ptr) where T : unmanaged
        {
            if (ptr == null) 
                return;
            NativeMemory.AlignedFree(ptr);
            ptr = null;
        }

        public static void Initialize(int width)
        {
            if (renderTex.Id != 0) 
                Raylib.UnloadTexture(renderTex);
            SafeFree(ref pixelBuf);
            SafeFree(ref priorityBuf);
            if (rowPix != null) { NativeMemory.AlignedFree(rowPix); rowPix = null; }
            if (rowPri != null) { NativeMemory.AlignedFree(rowPri); rowPri = null; }

            texWidth = width;
            texSize = width * 128;

            pixelBuf = (uint*)NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(uint)), 64);
            priorityBuf = (ushort*)NativeMemory.AlignedAlloc((nuint)(texSize * sizeof(ushort)), 64);
            rowPix = (uint**)NativeMemory.AlignedAlloc(128 * (nuint)sizeof(uint*), 64);
            rowPri = (ushort**)NativeMemory.AlignedAlloc(128 * (nuint)sizeof(ushort*), 64);

            NativeMemory.Clear(pixelBuf, (nuint)(texSize * sizeof(uint)));
            NativeMemory.Clear(priorityBuf, (nuint)(texSize * sizeof(ushort)));

            for (int y = 0; y < 128; y++)
            {
                rowPix[y] = pixelBuf + y * texWidth;
                rowPri[y] = priorityBuf + y * texWidth;
            }

            Image bg  = Raylib.GenImageColor(width, 128, Color.Black);
            renderTex = Raylib.LoadTextureFromImage(bg);
            Raylib.UnloadImage(bg);

            ringOffsetX = 0;
            currentLeftEdgePixel = 0;
            forceFullRedraw = true;
        }

        public static void InitializeForMIDI()
        {
            if (notePool == null) notePool = (NoteEntry*)NativeMemory.AlignedAlloc(TOTAL_KEYS * MAX_DEPTH * (nuint)sizeof(NoteEntry), 64);
            if (keyState == null) keyState = (uint*) NativeMemory.AlignedAlloc(TOTAL_KEYS * sizeof(uint), 64);
            if (openKeyBits == null) openKeyBits = (ulong*) NativeMemory.AlignedAlloc(BITSET_WORDS * sizeof(ulong), 64);
            if (colorLut == null) colorLut = (uint*) NativeMemory.AlignedAlloc(COLOR_COUNT * sizeof(uint), 64);

            NativeMemory.Clear(keyState, TOTAL_KEYS * sizeof(uint));
            NativeMemory.Clear(openKeyBits, BITSET_WORDS * sizeof(ulong));

            for (int i = 0; i < COLOR_COUNT; i++)
                colorLut[i] = 0xFF000000u | (uint)Random.Shared.Next(0x808080, 0x1000000);

            renderTickCursor = 0;
            renderMsgCursor = 0;
            lastTick = 0;
            ringOffsetX = 0;
            currentLeftEdgePixel = 0;
            isInitialized = true;
            forceFullRedraw = true;
        }

        public static void ResetForUnload()
        {
            isInitialized = false;
            forceFullRedraw = true;
            ringOffsetX = 0;
            currentLeftEdgePixel = 0;
        }

        public static void Dispose()
        {
            SafeFree(ref notePool);
            SafeFree(ref keyState);
            SafeFree(ref openKeyBits);
            SafeFree(ref colorLut);
            SafeFree(ref pixelBuf);
            SafeFree(ref priorityBuf);
            if (rowPix != null) { NativeMemory.AlignedFree(rowPix); rowPix = null; }
            if (rowPri != null) { NativeMemory.AlignedFree(rowPri); rowPri = null; }
            if (renderTex.Id != 0) { Raylib.UnloadTexture(renderTex); renderTex = default; }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ClearRingColumns(int startCol, int count)
        {
            int part1 = Math.Min(count, texWidth - startCol);
            int part2 = count - part1;

            for (int y = 0; y < 128; y++)
            {
                NativeMemory.Clear(rowPix[y] + startCol, (nuint)(part1 * sizeof(uint)));
                NativeMemory.Clear(rowPri[y] + startCol, (nuint)(part1 * sizeof(ushort)));
                if (part2 > 0)
                {
                    NativeMemory.Clear(rowPix[y], (nuint)(part2 * sizeof(uint)));
                    NativeMemory.Clear(rowPri[y], (nuint)(part2 * sizeof(ushort)));
                }
            }
        }

        // Core SIMD kernel for closed notes. Operates on a single contiguous
        // physical memory span and callers handle ring-buffer splitting.
        //
        // Draws color where zpri >= existing priority (shorter note wins),
        // and updates the priority buffer in the same pass.
        //
        // Vector256 path: 16 pixels per iteration (vs scalar: 1).
        // Vector128 fallback: 8 pixels per iteration.
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static void ZBlitSpan(uint* pX, ushort* pR, int len, uint color, ushort zpri)
        {
            int i = 0;

            if (Vector256.IsHardwareAccelerated && len >= 16)
            {
                var vColor = Vector256.Create(color);
                var vZpri = Vector256.Create(zpri);

                for (; i <= len - 16; i += 16)
                {
                    var existing = Vector256.Load(pR + i);

                    // test if zpri >= existing priority. if yes, we should draw
                    var mask16 = Vector256.GreaterThanOrEqual(vZpri, existing);
                    if (mask16 == Vector256<ushort>.Zero) 
                        continue; // all blocked, skip

                    // Sign-extend ushort mask to int mask (0xFFFF -> 0xFFFFFFFF) so that ConditionalSelect gets a correct 32-bit selector per color lane
                    var mLo = Vector256.WidenLower(mask16.AsInt16()).AsUInt32();
                    var mHi = Vector256.WidenUpper(mask16.AsInt16()).AsUInt32();

                    Vector256.Store(Vector256.ConditionalSelect(mLo, vColor, Vector256.Load(pX + i)),     pX + i);
                    Vector256.Store(Vector256.ConditionalSelect(mHi, vColor, Vector256.Load(pX + i + 8)), pX + i + 8);

                    // update priority only where we drew
                    Vector256.Store(Vector256.ConditionalSelect(mask16.AsUInt16(), vZpri, existing), pR + i);
                }
            }
            else if (Vector128.IsHardwareAccelerated && len >= 8)
            {
                var vColor = Vector128.Create(color);
                var vZpri = Vector128.Create(zpri);

                for (; i <= len - 8; i += 8)
                {
                    var existing = Vector128.Load(pR + i);
                    var mask8 = Vector128.GreaterThanOrEqual(vZpri, existing);
                    if (mask8 == Vector128<ushort>.Zero) 
                        continue;

                    var mLo = Vector128.WidenLower(mask8.AsInt16()).AsUInt32();
                    var mHi = Vector128.WidenUpper(mask8.AsInt16()).AsUInt32();

                    Vector128.Store(Vector128.ConditionalSelect(mLo, vColor, Vector128.Load(pX + i)), pX + i);
                    Vector128.Store(Vector128.ConditionalSelect(mHi, vColor, Vector128.Load(pX + i + 4)), pX + i + 4);
                    Vector128.Store(Vector128.ConditionalSelect(mask8.AsUInt16(), vZpri, existing), pR + i);
                }
            }

            for (; i < len; i++)
            {
                if (zpri >= pR[i]) 
                { 
                    pR[i] = zpri; 
                    pX[i] = color; 
                }
            }
        }

        // Resolves ring-buffer wrapping into at most two contiguous physical spans,
        // then dispatches each to ZBlitSpan. The split logic is now minimal.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DrawSegment(int startX, int endX, int y, uint color, ushort zpri)
        {
            if (startX < 0) 
                startX = 0;
            if (endX >= texWidth) 
                endX = texWidth - 1;
            
            int len = endX - startX + 1;
            if (len <= 0) 
                return;

            uint* pX = rowPix[y];
            ushort* pR = rowPri[y];

            // Single subtract instead of mod, valid because ringOffsetX < texWidth
            int physStart = ringOffsetX + startX;
            if (physStart >= texWidth) 
                physStart -= texWidth;

            int rem = texWidth - physStart;
            if (len <= rem)
            {
                ZBlitSpan(pX + physStart, pR + physStart, len, color, zpri);
            }
            else
            {
                ZBlitSpan(pX + physStart, pR + physStart, rem, color, zpri);
                ZBlitSpan(pX, pR, len - rem, color, zpri);
            }
        }

        // SIMD kernel for open (still-playing) notes: fills only unpainted pixels
        // (priorityBuf == 0). Three-way dispatch per 16-pixel chunk:
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static void BlitOpenSpan(uint* dstX, ushort* dstR, int len, uint color)
        {
            int i = 0;

            if (Vector256.IsHardwareAccelerated && len >= 16)
            {
                var vColor = Vector256.Create(color);
                var vZero  = Vector256<ushort>.Zero;

                for (; i <= len - 16; i += 16)
                {
                    var pri = Vector256.Load(dstR + i);
                    var eq  = Vector256.Equals(pri, vZero); // 0xFFFF where slot is empty

                    if (eq == vZero)                          // all occupied, skip
                        continue;

                    if (eq == Vector256<ushort>.AllBitsSet)   // all free, unconditional
                    {
                        Vector256.Store(vColor, dstX + i);
                        Vector256.Store(vColor, dstX + i + 8);
                    }
                    else                                       // mixed, masked write
                    {
                        var mLo = Vector256.WidenLower(eq.AsInt16()).AsUInt32();
                        var mHi = Vector256.WidenUpper(eq.AsInt16()).AsUInt32();
                        Vector256.Store(Vector256.ConditionalSelect(mLo, vColor, Vector256.Load(dstX + i)),     dstX + i);
                        Vector256.Store(Vector256.ConditionalSelect(mHi, vColor, Vector256.Load(dstX + i + 8)), dstX + i + 8);
                    }
                }
            }
            else if (Vector128.IsHardwareAccelerated && len >= 8)
            {
                var vColor = Vector128.Create(color);
                var vZero  = Vector128<ushort>.Zero;

                for (; i <= len - 8; i += 8)
                {
                    var pri = Vector128.Load(dstR + i);
                    var eq  = Vector128.Equals(pri, vZero);

                    if (eq == vZero)                          // all occupied
                        continue;

                    if (eq == Vector128<ushort>.AllBitsSet)   // all free
                    {
                        Vector128.Store(vColor, dstX + i);
                        Vector128.Store(vColor, dstX + i + 4);
                    }
                    else
                    {
                        var mLo = Vector128.WidenLower(eq.AsInt16()).AsUInt32();
                        var mHi = Vector128.WidenUpper(eq.AsInt16()).AsUInt32();
                        Vector128.Store(Vector128.ConditionalSelect(mLo, vColor, Vector128.Load(dstX + i)),     dstX + i);
                        Vector128.Store(Vector128.ConditionalSelect(mHi, vColor, Vector128.Load(dstX + i + 4)), dstX + i + 4);
                    }
                }
            }

            for (; i < len; i++)
            {
                if (dstR[i] == 0) dstX[i] = color;
            }
        }
        public static void Render(int screenWidth, int screenHeight, int tick, int pad)
        {
            if (!isInitialized) return;

            double pixelsPerTick = texWidth / (double)WindowTicks;
            long pptFixed = (long)(pixelsPerTick * 16777216.0);
            long idealLeft = ((tick - (WindowTicks >> 1)) * pptFixed) >> 24;

            if (WindowTicks != lastWindowTicks)
            {
                forceFullRedraw = true;
                lastWindowTicks = WindowTicks;
            }

            if (forceFullRedraw || lastTick > tick)
            {
                ringOffsetX = 0;
                RenderFull(tick, pptFixed, idealLeft);
                currentLeftEdgePixel = idealLeft;
                forceFullRedraw = false;
            }
            else
            {
                int delta = (int)(idealLeft - currentLeftEdgePixel);
                if (delta > 0)
                {
                    if (delta >= texWidth)
                    {
                        ringOffsetX = 0;
                        RenderFull(tick, pptFixed, idealLeft);
                        currentLeftEdgePixel = idealLeft;
                    }
                    else
                    {
                        ClearRingColumns(ringOffsetX, delta);
                        ringOffsetX = (ringOffsetX + delta) % texWidth;
                        currentLeftEdgePixel += delta;
                        AdvanceAndDraw(tick, pptFixed, currentLeftEdgePixel, delta);
                    }
                }
            }

            lastTick = tick;
            UpdateAndDraw(screenWidth, screenHeight, pad);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderFull(int centerTick, long pptFixed, long leftEdgePixel)
        {
            if (SynthEvent.messages == null || MIDIEvent.TickGroupArray == null) 
                return;

            TickGroup[] groups = MIDIEvent.TickGroupArray;
            byte* messages = (byte*)SynthEvent.messages.Pointer;
            ushort* tracks = (WindowManager.trackcolors && SynthEvent.track != null) ? SynthEvent.track.Pointer : null;
            bool useTrack = tracks != null;
            int maxtick = MIDILoader.maxTick - 1;
            int half = WindowTicks >> 1;

            int viewStart = Math.Clamp(centerTick - half, 0, maxtick);
            int viewEnd = Math.Clamp(centerTick + half, 0, maxtick);
            int searchStart = Math.Max(0, viewStart - WindowTicks);
            int searchEnd = Math.Min(groups.Length - 2, viewEnd);

            NativeMemory.Clear(pixelBuf, (nuint)(texSize * sizeof(uint)));
            NativeMemory.Clear(priorityBuf, (nuint)(texSize * sizeof(ushort)));
            NativeMemory.Clear(keyState, TOTAL_KEYS * sizeof(uint));
            NativeMemory.Clear(openKeyBits, BITSET_WORDS * sizeof(ulong));

            // Pin locals to avoid redundant static field loads in the hot loop
            NoteEntry* pool = notePool;
            uint* kState = keyState;
            ulong* oBits = openKeyBits;
            uint* cLut = colorLut;

            long msgIdx = groups[searchStart].offset;
            byte* synthev = messages + msgIdx * 3;
            int drawn = 0;
            int openCount = 0;

            for (int tick = searchStart; tick <= searchEnd; tick++)
            {
                if (tick > viewEnd && openCount == 0) 
                    break;

                long nextOff = groups[tick + 1].offset;
                if (msgIdx == nextOff) 
                    continue; // no events at this tick

                int tickX2 = (int)(((tick * pptFixed) >> 24) - leftEdgePixel);

                while (msgIdx < nextOff)
                {
                    byte status = (byte)(synthev[0] & 0xF0);

                    if ((status - 0x80u) <= 0x10u) // noteoff/on filter
                    {
                        byte channel = (byte)(synthev[0] & 0xF);
                        byte note = synthev[1];
                        int  key = (channel << 7) | note;
                        uint ks = kState[key];
                        uint cnt = ks & 0xFFFFu;
                        uint head = ks >> 16;

                        if (status == 0x80) // NoteOff
                        {
                            if (cnt != 0)
                            {
                                NoteEntry* slot = pool + (key << DEPTH_SHIFT) + (int)(head & DEPTH_MASK);
                                uint startTick = slot->StartTick;
                                uint color = slot->Color; // pre-resolved, no LUT lookup

                                uint newCnt = cnt - 1;
                                kState[key] = (((head + 1) & DEPTH_MASK) << 16) | newCnt;

                                int fullStartX = (int)((((long)startTick * pptFixed) >> 24) - leftEdgePixel);
                                if (tickX2 >= 0 && fullStartX < texWidth)
                                {
                                    DrawSegment(fullStartX, tickX2, 127 - note, color, ZPriority(tickX2 - fullStartX));
                                    drawn++;
                                }
                                if (newCnt == 0) 
                                { 
                                    ClearOpen(key); 
                                    openCount--; 
                                }
                            }
                        }
                        else
                        {
                            uint colorIdx = (useTrack ? (uint)tracks[msgIdx] : channel) & 0xFFu;
                            uint color = cLut[colorIdx];

                            if (cnt == 0) 
                            { 
                                SetOpen(key); 
                                openCount++; 
                            }

                            if (cnt < MAX_DEPTH)
                            {
                                NoteEntry* slot = pool + (key << DEPTH_SHIFT) + (int)((head + cnt) & DEPTH_MASK);
                                slot->StartTick = (uint)tick;
                                slot->Color = color;
                                kState[key] = (head << 16) | (cnt + 1);
                            }
                            else // evict oldest note for this key
                            {
                                NoteEntry* slot = pool + (key << DEPTH_SHIFT) + (int)(head & DEPTH_MASK);
                                slot->StartTick = (uint)tick;
                                slot->Color = color;
                                kState[key] = (((head + 1) & DEPTH_MASK) << 16) | cnt;
                            }
                        }
                    }
                    msgIdx++;
                    synthev += 3;
                }
            }

            FlushOpenNotes(pptFixed, leftEdgePixel, viewEnd, -1, ref drawn);

            renderTickCursor = Math.Min(searchEnd + 1, maxtick);
            renderMsgCursor = msgIdx;
            NotesDrawnLastFrame = drawn;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void AdvanceAndDraw(int centerTick, long pptFixed, long leftEdgePixel, int scrollPixels)
        {
            if (SynthEvent.messages == null || MIDIEvent.TickGroupArray == null) 
                return;

            fixed (TickGroup* groups = &MIDIEvent.TickGroupArray[0])
            {
                NoteEntry* pool = notePool;
                uint* kState = keyState;
                ulong* oBits = openKeyBits;
                uint* cLut = colorLut;

                byte* messages = (byte*)SynthEvent.messages.Pointer;
                ushort* tracks = (WindowManager.trackcolors && SynthEvent.track != null) ? SynthEvent.track.Pointer : null;
                bool useTrack = tracks != null;

                int maxtick = MIDILoader.maxTick - 1;
                int stripX1 = texWidth - scrollPixels;
                int tickEnd = Math.Min(centerTick + (WindowTicks >> 1), maxtick);

                long msgIdx = renderMsgCursor;
                int currentTick = renderTickCursor;
                int drawn = 0;

                if (currentTick <= tickEnd)
                {
                    long nextTickOff = groups[currentTick + 1].offset;
                    long endMsgIdx = groups[tickEnd + 1].offset;

                    while (msgIdx < endMsgIdx)
                    {
                        // Advance tick cursor, skipping empty ticks
                        while (msgIdx >= nextTickOff && currentTick < tickEnd)
                        {
                            currentTick++;
                            nextTickOff = groups[currentTick + 1].offset;
                        }

                        int tickX2 = (int)((((long)currentTick * pptFixed) >> 24) - leftEdgePixel);
                        if (tickX2 >= stripX1)
                        {
                            byte* synthev = messages + msgIdx * 3;
                            byte status = (byte)(synthev[0] & 0xF0);

                            if ((uint)(status - 0x80u) <= 0x10u)
                            {
                                byte note = synthev[1];
                                byte channel = (byte)(synthev[0] & 0xF);
                                int key = (channel << 7) | note;

                                if (status == 0x80) // NoteOff
                                {
                                    uint ks = kState[key];
                                    uint cnt = ks & 0xFFFFu;
                                    if (cnt != 0)
                                    {
                                        uint head = ks >> 16;
                                        NoteEntry* slot = pool + (key << DEPTH_SHIFT) + (int)(head & DEPTH_MASK);
                                        uint startTick = slot->StartTick;
                                        uint color = slot->Color;

                                        kState[key] = (((head + 1) & DEPTH_MASK) << 16) | (cnt - 1);

                                        int fullStartX = (int)((((long)startTick * pptFixed) >> 24) - leftEdgePixel);
                                        int drawStartX = fullStartX < stripX1 ? stripX1 : fullStartX;

                                        if (drawStartX <= tickX2)
                                        {
                                            DrawSegment(drawStartX, tickX2, 127 - note, color, ZPriority(tickX2 - fullStartX));
                                            drawn++;
                                        }
                                        if (cnt == 1) 
                                            ClearOpen(key);
                                    }
                                }
                                else // NoteOn
                                {
                                    uint ks = kState[key];
                                    uint cnt = ks & 0xFFFFu;
                                    uint head = ks >> 16;
                                    uint colorIdx = (useTrack ? (uint)tracks[msgIdx] : channel) & 0xFFu;
                                    uint color = cLut[colorIdx];

                                    if (cnt == 0) 
                                        SetOpen(key);

                                    if (cnt < MAX_DEPTH)
                                    {
                                        NoteEntry* slot = pool + (key << DEPTH_SHIFT) + (int)((head + cnt) & DEPTH_MASK);
                                        slot->StartTick = (uint)currentTick;
                                        slot->Color = color;
                                        kState[key] = (head << 16) | (cnt + 1);
                                    }
                                    else
                                    {
                                        NoteEntry* slot = pool + (key << DEPTH_SHIFT) + (int)(head & DEPTH_MASK);
                                        slot->StartTick = (uint)currentTick;
                                        slot->Color = color;
                                        kState[key] = (((head + 1) & DEPTH_MASK) << 16) | cnt;
                                    }
                                }
                            }
                        }
                        msgIdx++;
                    }
                }

                FlushOpenNotes(pptFixed, leftEdgePixel, tickEnd, stripX1, ref drawn);

                renderMsgCursor = msgIdx;
                renderTickCursor = Math.Min(tickEnd + 1, maxtick);
                NotesDrawnLastFrame = drawn;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void FlushOpenNotes(long pptFixed, long leftEdgePixel, int tickEnd, int stripX1, ref int drawn)
        {
            int tickX2 = (int)((((long)tickEnd * pptFixed) >> 24) - leftEdgePixel);
            if (stripX1 >= 0 && tickX2 < stripX1) 
                return;

            NoteEntry* pool = notePool;
            uint* kState = keyState;
            ulong* oBits = openKeyBits;
            int width = texWidth;
            int ring = ringOffsetX;
            int endX = tickX2 >= width ? width - 1 : tickX2;

            for (int wordIdx = 0; wordIdx < BITSET_WORDS; wordIdx++)
            {
                ulong word = oBits[wordIdx];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    word &= word - 1; // clear lowest set bit
                    int key = (wordIdx << 6) | bit;

                    uint ks = kState[key];
                    int cnt = (int)(ks & 0xFFFFu);
                    if (cnt == 0) 
                        continue;

                    int head = (int)(ks >> 16);
                    int y = 127 - (key & 0x7F);

                    uint* pX = rowPix[y];
                    ushort* pR = rowPri[y];

                    for (int d = 0; d < cnt; d++)
                    {
                        NoteEntry* entry = pool + (key << DEPTH_SHIFT) + ((head + d) & DEPTH_MASK);
                        uint startTick = entry->StartTick;
                        uint color = entry->Color;

                        int fullStartX = (int)((((long)startTick * pptFixed) >> 24) - leftEdgePixel);
                        int drawStartX = (stripX1 >= 0 && fullStartX < stripX1) ? stripX1 : fullStartX;
                        if (drawStartX < 0) 
                            drawStartX = 0;

                        int len = endX - drawStartX + 1;
                        if (len <= 0) 
                            continue;

                        // Ring-split, then dispatch to BlitOpenSpan
                        int physStart = ring + drawStartX;
                        if (physStart >= width) 
                            physStart -= width;
                        int rem = width - physStart;

                        if (len <= rem)
                        {
                            BlitOpenSpan(pX + physStart, pR + physStart, len, color);
                        }
                        else
                        {
                            BlitOpenSpan(pX + physStart, pR + physStart, rem, color);
                            BlitOpenSpan(pX, pR, len - rem, color);
                        }
                        drawn++;
                    }
                }
            }
        }

        // ── UpdateAndDraw ─────────────────────────────────────────────────────
        private static void UpdateAndDraw(int screenWidth, int screenHeight, int pad)
        {
            Raylib.UpdateTexture(renderTex, pixelBuf);

            float scaleX = (float)screenWidth / texWidth;
            float targetHeight = screenHeight - pad * 2;

            if (ringOffsetX == 0)
            {
                Raylib.DrawTexturePro(renderTex,
                    new Rectangle(0, 0, texWidth, 128),
                    new Rectangle(0, pad, screenWidth, targetHeight),
                    Vector2.Zero, 0f, Color.White);
            }
            else
            {
                int w1 = texWidth - ringOffsetX;
                float screenW1 = w1 * scaleX;

                Raylib.DrawTexturePro(renderTex,
                    new Rectangle(ringOffsetX, 0, w1, 128),
                    new Rectangle(0, pad, screenW1, targetHeight),
                    Vector2.Zero, 0f, Color.White);

                Raylib.DrawTexturePro(renderTex,
                    new Rectangle(0, 0, ringOffsetX, 128),
                    new Rectangle(screenW1, pad, screenWidth - screenW1, targetHeight),
                    Vector2.Zero, 0f, Color.White);
            }
        }
    }
}