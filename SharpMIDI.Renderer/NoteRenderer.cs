using Raylib_cs;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteRenderer
    {
        private const int TEXTURE_HEIGHT = 128; // 1 pixel per MIDI note

        private static int textureWidth = 2048;
        private static Texture2D streamingTexture;

        private static uint[]? pixelBuffer;
        private static GCHandle bufferHandle;
        private static uint* pixelPtr;

        private static readonly byte[] noteToY = new byte[128];
        private static float currentWindow = 2000f;
        private static float ticksPerPixel;

        private static int lastColumn = -1;
        public static bool forceRedraw = true;
        public static float lastTick = 0;
        public static bool initialized = false;
        public static float Window => currentWindow;
        public static int NotesDrawnLastFrame { get; private set; } = 0;

        private const uint BLACK_ALPHA = 0xFF000000u;

        // Bit unpacking (matches NoteProcessor)
        private const uint RELSTART_MASK = 0x7FFu; // 11 bits
        private const int NOTENUMBER_SHIFT = 11;
        private const uint NOTENUMBER_MASK = 0x7Fu; // 7 bits
        private const int DURATION_SHIFT = 18;
        private const uint DURATION_MASK = 0x3FFu; // 10 bits
        private const int COLORINDEX_SHIFT = 28;

        // Precompute bucket start ticks
        private static int[]? bucketStartTicks;

        static NoteRenderer()
        {
            for (int i = 0; i < 128; i++)
                noteToY[i] = (byte)(127 - i);
        }

        public static void Initialize(int width, int height)
        {
            int newWidth = Math.Min(width, 8192);
            if (initialized && newWidth == textureWidth) return;

            if (initialized) Cleanup();

            textureWidth = newWidth;
            UpdateWindowParams();

            var img = Raylib.GenImageColor(textureWidth, TEXTURE_HEIGHT, Raylib_cs.Color.Blank);
            streamingTexture = Raylib.LoadTextureFromImage(img);
            Raylib.UnloadImage(img);

            pixelBuffer = new uint[textureWidth * TEXTURE_HEIGHT];
            bufferHandle = GCHandle.Alloc(pixelBuffer, GCHandleType.Pinned);
            pixelPtr = (uint*)bufferHandle.AddrOfPinnedObject();

            ClearBuffer();
            Raylib.UpdateTexture(streamingTexture, pixelPtr);

            lastColumn = -1;
            forceRedraw = true;
            initialized = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateWindowParams()
        {
            ticksPerPixel = currentWindow / textureWidth;
        }

        public static void UpdateStreaming(float tick)
        {
            if (!NoteProcessor.IsReady) return;

            if (!MIDIPlayer.stopping && tick < lastTick) tick = lastTick;
            lastTick = tick;

            float tickPos = tick / ticksPerPixel;
            int newColumn = (int)tickPos % textureWidth;
            if (newColumn < 0) newColumn += textureWidth;

            int delta = lastColumn == -1 ? textureWidth :
                        newColumn >= lastColumn ? newColumn - lastColumn :
                        (textureWidth - lastColumn) + newColumn;

            if (forceRedraw || delta >= textureWidth)
            {
                RenderRegion(0, textureWidth, tick - currentWindow * 0.5f);
                forceRedraw = false;
            }
            else if (delta > 0)
            {
                ScrollLeft(delta);
                RenderRegion(textureWidth - delta, delta, tick - currentWindow * 0.5f + currentWindow * (textureWidth - delta) / textureWidth);
            }

            Raylib.UpdateTexture(streamingTexture, pixelPtr);
            lastColumn = newColumn;
        }

        private static void ScrollLeft(int pixels)
        {
            if (pixels <= 0) return;

            int keep = textureWidth - pixels;
            int bytesToCopy = keep * sizeof(uint);

            for (int y = 0; y < TEXTURE_HEIGHT; y++)
            {
                uint* row = pixelPtr + y * textureWidth;
                Buffer.MemoryCopy(row + pixels, row, bytesToCopy, bytesToCopy);
                new Span<uint>(row + keep, pixels).Fill(BLACK_ALPHA);
            }
        }

        private static void RenderRegion(int startX, int width, float startTick)
        {
            if (width <= 0 || NoteProcessor.SortedBuckets.Length == 0) return;

            NotesDrawnLastFrame = 0;

            for (int y = 0; y < TEXTURE_HEIGHT; y++)
                new Span<uint>(pixelPtr + y * textureWidth + startX, width).Fill(BLACK_ALPHA);

            var buckets = NoteProcessor.SortedBuckets;
            int bucketSize = NoteProcessor.BucketSize;

            // Precompute bucket start ticks
            if (bucketStartTicks == null || bucketStartTicks.Length != buckets.Length)
            {
                bucketStartTicks = new int[buckets.Length];
                for (int i = 0; i < buckets.Length; i++)
                    bucketStartTicks[i] = i * bucketSize;
            }

            int startBucket = Math.Max(0, (int)(startTick / bucketSize));
            int endBucket = Math.Min(buckets.Length - 1, (int)((startTick + width * ticksPerPixel) / bucketSize));

            float invTicksPerPixel = 1f / ticksPerPixel;

            for (int b = startBucket; b <= endBucket; b++)
            {
                var bucket = buckets[b];
                if (bucket == null || bucket.Length == 0) continue;

                int bucketStartTick = bucketStartTicks[b];
                ReadOnlySpan<uint> notes = bucket;

                for (int n = 0; n < notes.Length; n++)
                {
                    uint packed = notes[n];
                    int relStart = (int)(packed & RELSTART_MASK);
                    int noteNumber = (int)((packed >> NOTENUMBER_SHIFT) & NOTENUMBER_MASK);
                    int duration = (int)((packed >> DURATION_SHIFT) & DURATION_MASK);
                    int colorIndex = (int)((packed >> COLORINDEX_SHIFT) & 0xF);

                    int absStart = bucketStartTick + relStart;
                    int absEnd = absStart + duration;

                    if (absEnd < startTick || absStart > startTick + width * ticksPerPixel)
                        continue;

                    float startPx = (absStart - startTick) * invTicksPerPixel;
                    float endPx = (absEnd - startTick) * invTicksPerPixel;

                    int x1 = Math.Max(0, (int)startPx);
                    int x2 = Math.Min(width, (int)endPx + 1);
                    if (x2 <= x1) continue;

                    uint rgba = BLACK_ALPHA | NoteProcessor.trackColors[colorIndex];
                    int y = noteToY[noteNumber];
                    uint* rowPtr = pixelPtr + y * textureWidth + startX + x1;
                    int noteWidth = x2 - x1;

                    if (noteWidth <= 8)
                    {
                        for (int x = 0; x < noteWidth; x++) rowPtr[x] = rgba;
                    }
                    else
                    {
                        new Span<uint>(rowPtr, noteWidth).Fill(rgba);
                    }

                    NotesDrawnLastFrame++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearBuffer()
        {
            new Span<uint>(pixelPtr, textureWidth * TEXTURE_HEIGHT).Fill(BLACK_ALPHA);
        }

        public static void Render(int screenWidth, int screenHeight, int pad)
        {
            if (!initialized) return;
            Raylib.DrawTexturePro(streamingTexture,
                new Raylib_cs.Rectangle(0, 0, textureWidth, TEXTURE_HEIGHT),
                new Raylib_cs.Rectangle(0, pad, screenWidth, screenHeight - pad * 2),
                Vector2.Zero, 0f, Raylib_cs.Color.White);
        }

        public static void SetWindow(float newWindow)
        {
            if (newWindow <= 0 || newWindow == currentWindow) return;
            currentWindow = newWindow;
            UpdateWindowParams();
            forceRedraw = true;
            lastColumn = -1;
        }

        public static void Cleanup()
        {
            if (!initialized) return;
            if (Raylib.IsTextureValid(streamingTexture)) Raylib.UnloadTexture(streamingTexture);
            if (bufferHandle.IsAllocated) bufferHandle.Free();
            forceRedraw = true;
        }
    }
}
