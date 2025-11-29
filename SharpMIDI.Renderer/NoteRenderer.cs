using Raylib_cs;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteRenderer
    {
        private const int TEXTURE_HEIGHT = 128;  // 1 pixel per MIDI note
        
        private static int textureWidth = 2048;
        private static Texture2D streamingTexture;
        
        // Direct RGBA32 buffer for Raylib
        private static uint[]? pixelBuffer;
        private static GCHandle bufferHandle;
        private static uint* pixelPtr;

        // Note Y lookup (flipped) - precomputed
        private static readonly byte[] noteToY = new byte[128];

        private static float currentWindow = 2000f;
        private static float ticksPerPixel;

        private static int lastColumn = -1;
        public static bool forceRedraw = true;
        public static float lastTick = 0;

        public static bool initialized = false;
        public static float Window => currentWindow;
        public static int NotesDrawnLastFrame { get; private set; } = 0;

        // Constants for optimization
        private const uint BLACK_ALPHA = 0xFF000000u;
        
        // Bit unpacking constants (must match NoteProcessor)
        private const uint RELSTART_MASK = 0x1FFu;        // 9 bits
        private const int NOTENUMBER_SHIFT = 9;
        private const uint NOTENUMBER_MASK = 0x7Fu;       // 7 bits
        private const int DURATION_SHIFT = 16;
        private const uint DURATION_MASK = 0xFFu;         // 8 bits
        private const int COLORINDEX_SHIFT = 24;

        static NoteRenderer()
        {
            // Precompute flipped Y coordinates
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

            // Create texture
            var img = Raylib.GenImageColor(textureWidth, TEXTURE_HEIGHT, Raylib_cs.Color.Blank);
            streamingTexture = Raylib.LoadTextureFromImage(img);
            Raylib.UnloadImage(img);

            // Direct RGBA32 buffer
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

            // Prevent backwards jumps
            if (!MIDIPlayer.stopping && tick < lastTick)
                tick = lastTick;
            lastTick = tick;

            float tickPos = tick / ticksPerPixel;
            int newColumn = (int)tickPos % textureWidth;
            if (newColumn < 0) newColumn += textureWidth;

            int delta = lastColumn == -1 ? textureWidth :
                       newColumn >= lastColumn ? newColumn - lastColumn :
                       (textureWidth - lastColumn) + newColumn;

            if (forceRedraw || delta >= textureWidth)
            {
                float startTick = tick - (currentWindow * 0.5f);
                RenderRegion(0, textureWidth, startTick);
                forceRedraw = false;
            }
            else if (delta > 0)
            {
                ScrollLeft(delta);
                float startTick = tick - (currentWindow * 0.5f) + (currentWindow * (textureWidth - delta) / textureWidth);
                RenderRegion(textureWidth - delta, delta, startTick);
            }

            Raylib.UpdateTexture(streamingTexture, pixelPtr);
            lastColumn = newColumn;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ScrollLeft(int pixels)
        {
            if (pixels <= 0) return;

            int keepPixels = textureWidth - pixels;
            int bytesToCopy = keepPixels * sizeof(uint);
            
            // Optimized scrolling using pointer arithmetic and bulk copy
            for (int y = 0; y < TEXTURE_HEIGHT; y++)
            {
                uint* row = pixelPtr + (y * textureWidth);
                
                // Bulk memory move
                Buffer.MemoryCopy(row + pixels, row, bytesToCopy, bytesToCopy);
                
                // Fast clear of right portion using Span
                new Span<uint>(row + keepPixels, pixels).Fill(BLACK_ALPHA);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderRegion(int startX, int width, float startTick)
        {
            if (width <= 0) return;

            NotesDrawnLastFrame = 0;

            // Clear region to black using spans for better performance
            for (int y = 0; y < TEXTURE_HEIGHT; y++)
            {
                new Span<uint>(pixelPtr + (y * textureWidth + startX), width).Fill(BLACK_ALPHA);
            }

            var buckets = NoteProcessor.SortedBuckets;
            if (buckets.Length == 0) return;

            float endTick = startTick + width * ticksPerPixel;
            int bucketSize = NoteProcessor.BucketSize;
            
            // Calculate exact bucket range
            int startBucket = Math.Max(0, (int)(startTick / bucketSize));
            int endBucket = Math.Min(buckets.Length - 1, (int)(endTick / bucketSize));

            if (startBucket >= buckets.Length) return;

            // Precompute reciprocal for division optimization
            float invTicksPerPixel = 1f / ticksPerPixel;

            // Render notes from buckets using spans
            for (int bucketIdx = startBucket; bucketIdx <= endBucket; bucketIdx++)
            {
                var bucket = buckets[bucketIdx];
                if (bucket == null || bucket.Length == 0) continue;

                int bucketStartTick = bucketIdx * bucketSize;

                // Use ReadOnlySpan for zero-copy iteration
                ReadOnlySpan<uint> notes = bucket;
                
                for (int noteIdx = 0; noteIdx < notes.Length; noteIdx++)
                {
                    uint packed = notes[noteIdx];
                    
                    // Inline unpacking for maximum performance
                    int relStart = (int)(packed & RELSTART_MASK);
                    int noteNumber = (int)((packed >> NOTENUMBER_SHIFT) & NOTENUMBER_MASK);
                    int duration = (int)((packed >> DURATION_SHIFT) & DURATION_MASK);
                    int colorIndex = (int)(packed >> COLORINDEX_SHIFT);
                    
                    int absStart = bucketStartTick + relStart;
                    int absEnd = absStart + duration;

                    // Early culling
                    if (absEnd < startTick || absStart > endTick) 
                        continue;

                    // Calculate pixel coordinates using multiplication instead of division
                    float startPx = (absStart - startTick) * invTicksPerPixel;
                    float endPx = (absEnd - startTick) * invTicksPerPixel;

                    int x1 = Math.Max(0, (int)startPx);
                    int x2 = Math.Min(width, (int)endPx + 1);

                    if (x2 <= x1) continue;

                    // Get precomputed RGBA color
                    uint rgbaColor = BLACK_ALPHA | NoteProcessor.trackColors[colorIndex];

                    // Optimized drawing - direct pointer access with span
                    int y = noteToY[noteNumber];
                    uint* rowPtr = pixelPtr + (y * textureWidth + startX + x1);

                    int noteWidth = x2 - x1;
                    
                    // Use span fill for better performance on longer notes
                    if (noteWidth <= 8)
                    {
                        // Unrolled for small widths
                        for (int x = 0; x < noteWidth; x++)
                            rowPtr[x] = rgbaColor;
                    }
                    else
                    {
                        // Use Span.Fill for larger widths
                        new Span<uint>(rowPtr, noteWidth).Fill(rgbaColor);
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

            var src = new Raylib_cs.Rectangle(0, 0, textureWidth, TEXTURE_HEIGHT);
            var dst = new Raylib_cs.Rectangle(0, pad, screenWidth, screenHeight - (pad << 1));
            Raylib.DrawTexturePro(streamingTexture, src, dst, Vector2.Zero, 0f, Raylib_cs.Color.White);
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

            if (Raylib.IsTextureValid(streamingTexture))
                Raylib.UnloadTexture(streamingTexture);

            if (bufferHandle.IsAllocated)
                bufferHandle.Free();

            forceRedraw = true;
        }
    }
}