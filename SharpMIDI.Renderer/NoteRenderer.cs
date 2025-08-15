using Raylib_cs;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteRenderer
    {
        private const int TOTAL_TEXTURE_HEIGHT = 128;  // 1 pixel per MIDI note (0..127)

        private static int textureWidth = 2048;
        private static int textureHeight = TOTAL_TEXTURE_HEIGHT;

        private static RenderTexture2D streamingTexture;
        private static byte[] pixelBuffer;
        private static GCHandle bufferHandle;
        private static uint* pixelPtr32; // Direct uint pointer for SSE operations

        // Pre-calculated lookup tables
        private static readonly byte[] noteToPixelY = new byte[128];

        private static float pixelsPerTick = 1f;
        private static int lastRenderedColumn = 0;
        private static double lastRenderTick = 0;
        private static double currentTick = 0;

        private static bool initialized = false;
        private static bool forceFullRedraw = true;

        // Cached values to reduce property access
        private static float cachedWindow = 2000f;
        private static float cachedTicksPerPixel;
        private static float invTicksPerPixel; // Pre-calculated inverse

        public static float Window => cachedWindow;
        public static int RenderedColumns { get; private set; } = 0;

        // SSE3 constants
        private static readonly Vector128<uint> ZeroVector128 = Vector128<uint>.Zero;
        private static readonly Vector256<uint> ZeroVector256 = Vector256<uint>.Zero;

        // Compact note structure for better cache performance
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private readonly struct NoteRect
        {
            public readonly ushort X, Y, Width, Height;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public NoteRect(int x, int y, int w, int h)
            {
                X = (ushort)x; 
                Y = (ushort)y; 
                Width = (ushort)w; 
                Height = (ushort)h;
            }
        }

        static NoteRenderer()
        {
            // Pre-calculate lookup tables - notes are vertically flipped
            for (int i = 0; i < 128; i++)
            {
                noteToPixelY[i] = (byte)(127 - i);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Initialize(int width, int height)
        {
            int maxTextureWidth = Math.Min(width, 8192);
            if (initialized && maxTextureWidth == textureWidth) return;

            if (initialized) Cleanup();

            textureWidth = maxTextureWidth;
            textureHeight = TOTAL_TEXTURE_HEIGHT;
            cachedTicksPerPixel = cachedWindow / textureWidth;
            invTicksPerPixel = textureWidth / cachedWindow;
            pixelsPerTick = invTicksPerPixel;

            streamingTexture = Raylib.LoadRenderTexture(textureWidth, textureHeight);

            int bufferSize = textureWidth * textureHeight * 4;
            if (bufferHandle.IsAllocated) bufferHandle.Free();

            pixelBuffer = new byte[bufferSize];
            bufferHandle = GCHandle.Alloc(pixelBuffer, GCHandleType.Pinned);
            pixelPtr32 = (uint*)bufferHandle.AddrOfPinnedObject();

            // Clear texture
            Raylib.BeginTextureMode(streamingTexture);
            Raylib.ClearBackground(Raylib_cs.Color.Black);
            Raylib.EndTextureMode();

            lastRenderedColumn = -1;
            forceFullRedraw = true;
            initialized = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void UpdateStreaming(float tick)
        {
            if (!initialized || !NoteProcessor.IsReady) return;

            if (!MIDIPlayer.stopping && tick < lastRenderTick)
            {
                // Prevent backwards jumps hopefully
                currentTick = lastRenderTick;
            }
            else
            {
                currentTick = tick;
            }
            lastRenderTick = currentTick;
            
            float tickPosition = (float)currentTick * pixelsPerTick;
            int newColumn = (int)tickPosition % textureWidth;
            
            int delta = newColumn >= lastRenderedColumn 
                ? newColumn - lastRenderedColumn
                : (textureWidth - lastRenderedColumn) + newColumn;

            if (delta < 0) delta = 0;
            if (delta > textureWidth) delta = textureWidth;

            if (forceFullRedraw || delta >= textureWidth)
            {
                float startTick = tick - cachedWindow * 0.5f;
                RenderColumnsSSE(0, textureWidth, startTick);
                UpdateTexture();
                forceFullRedraw = false;
                RenderedColumns = textureWidth;
            }
            else if (delta > 0)
            {
                ScrollPixelBufferSSE(delta);
                
                float startTick = tick - (cachedWindow * 0.5f) + (cachedWindow * (textureWidth - delta) / textureWidth);
                RenderColumnsSSE(textureWidth - delta, delta, startTick);
                UpdateTexture();
                RenderedColumns = delta;
            }
            else
            {
                RenderedColumns = 0;
            }

            lastRenderedColumn = newColumn;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ScrollPixelBufferSSE(int delta)
        {
            if (delta <= 0 || delta >= textureWidth) return;

            int pixelsPerRow = textureWidth;
            int shiftPixels = textureWidth - delta;
            int clearPixels = delta;

            // Process rows in batches for better cache utilization
            for (int y = 0; y < textureHeight; y++)
            {
                uint* rowPtr = pixelPtr32 + (y * pixelsPerRow);
                uint* srcPtr = rowPtr + delta;

                // Use AVX2 if available for 8 pixels at once, otherwise SSE3 for 4 pixels
                int vectorPos = 0;

                if (Avx2.IsSupported && shiftPixels >= 8)
                {
                    int avxChunks = shiftPixels / 8;
                    for (int chunk = 0; chunk < avxChunks; chunk++)
                    {
                        var data = Avx2.LoadVector256(srcPtr + vectorPos);
                        Avx2.Store(rowPtr + vectorPos, data);
                        vectorPos += 8;
                    }
                }
                else if (Sse3.IsSupported && shiftPixels >= 4)
                {
                    int sseChunks = shiftPixels / 4;
                    for (int chunk = 0; chunk < sseChunks; chunk++)
                    {
                        var data = Sse2.LoadVector128(srcPtr + vectorPos);
                        Sse2.Store(rowPtr + vectorPos, data);
                        vectorPos += 4;
                    }
                }

                // Handle remaining pixels with scalar operations
                for (int remaining = vectorPos; remaining < shiftPixels; remaining++)
                {
                    rowPtr[remaining] = srcPtr[remaining];
                }

                // Clear the new area using SIMD
                ClearPixelsSSE(rowPtr + shiftPixels, clearPixels);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ClearPixelsSSE(uint* ptr, int pixelCount)
        {
            int vectorPos = 0;

            if (Avx2.IsSupported && pixelCount >= 8)
            {
                int avxChunks = pixelCount / 8;
                for (int chunk = 0; chunk < avxChunks; chunk++)
                {
                    Avx2.Store(ptr + vectorPos, ZeroVector256);
                    vectorPos += 8;
                }
            }
            else if (Sse2.IsSupported && pixelCount >= 4)
            {
                int sseChunks = pixelCount / 4;
                for (int chunk = 0; chunk < sseChunks; chunk++)
                {
                    Sse2.Store(ptr + vectorPos, ZeroVector128);
                    vectorPos += 4;
                }
            }

            // Clear remaining pixels
            for (int remaining = vectorPos; remaining < pixelCount; remaining++)
            {
                ptr[remaining] = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderColumnsSSE(int startX, int width, float startTick)
        {
            var packed = NoteProcessor.AllPackedNotes;
            var buckets = NoteProcessor.BucketOffsets;
            int bucketSize = NoteProcessor.BucketSize;

            if (packed.Length == 0 || buckets.Length == 0) return;

            // Clear region using SSE
            ClearPixelBufferRegionSSE(startX, width);

            float endTick = startTick + (width * cachedTicksPerPixel);
            int viewBucket = Math.Max(0, (int)(startTick / bucketSize));
            int startBucket = Math.Max(0, viewBucket - 1);

            if (startBucket >= buckets.Length - 1) return;

            int noteIndex = buckets[startBucket];
            int totalNotes = NoteProcessor.TotalPackedNotes;

            int currentBucket = startBucket;
            int nextBucketBound = (currentBucket + 1 < buckets.Length) ? buckets[currentBucket + 1] : totalNotes;
            int bucketStartTick = currentBucket * bucketSize;

            // Process notes in larger batches for better cache utilization
            const int batchSize = 256;

            for (; noteIndex < totalNotes; noteIndex += batchSize)
            {
                int batchEnd = Math.Min(noteIndex + batchSize, totalNotes);

                for (int i = noteIndex; i < batchEnd; i++)
                {
                    // Update bucket bounds when needed
                    while (i >= nextBucketBound)
                    {
                        currentBucket++;
                        if (currentBucket + 1 < buckets.Length) 
                            nextBucketBound = buckets[currentBucket + 1];
                        else 
                            nextBucketBound = totalNotes;
                        bucketStartTick = currentBucket * bucketSize;
                    }

                    NoteProcessor.UnpackNoteAt(i, out int relStart, out int duration, out int noteNumber, out int colorIndex, out int trackIndex);

                    int absStart = bucketStartTick + relStart;
                    int absEnd = absStart + duration;

                    // Early termination - notes are sorted by start time
                    if (absStart > endTick) return;
                    if (absEnd < startTick) continue;

                    // Calculate pixel coordinates
                    float startPx = (absStart - startTick) * invTicksPerPixel;
                    float endPx = (absEnd - startTick) * invTicksPerPixel;

                    int x1 = Math.Max(0, (int)startPx);
                    int x2 = Math.Min(width, (int)endPx + 1);
                    if (x2 <= x1) continue;

                    var rect = new NoteRect(startX + x1, noteToPixelY[noteNumber], x2 - x1, 1);
                    uint color = NoteProcessor.trackColors[colorIndex & 0x0F];
                    
                    DrawNoteFastSSE(rect, color);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DrawNoteFastSSE(NoteRect note, uint color)
        {
            if (!initialized || pixelPtr32 == null) return;

            int maxX = Math.Min(note.X + note.Width, textureWidth);
            int maxY = Math.Min(note.Y + note.Height, textureHeight);
            int x = Math.Max((byte)0, note.X);
            int y = Math.Max((byte)0, note.Y);

            if (maxX <= x || maxY <= y) return;

            int actualWidth = maxX - x;

            // For single-pixel height notes (most common case), optimize heavily
            if (note.Height == 1 && actualWidth > 0)
            {
                uint* rowPtr = pixelPtr32 + (y * textureWidth + x);
                FillRowSSE(rowPtr, actualWidth, color);
                return;
            }

            // Multi-row filling (rare case for standard piano roll)
            for (int py = y; py < maxY; py++)
            {
                uint* rowPtr = pixelPtr32 + (py * textureWidth + x);
                FillRowSSE(rowPtr, actualWidth, color);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void FillRowSSE(uint* rowPtr, int width, uint color)
        {
            int vectorPos = 0;

            if (Avx2.IsSupported && width >= 8)
            {
                var colorVec256 = Vector256.Create(color);
                int avxChunks = width / 8;
                
                for (int chunk = 0; chunk < avxChunks; chunk++)
                {
                    Avx2.Store(rowPtr + vectorPos, colorVec256);
                    vectorPos += 8;
                }
            }
            else if (Sse2.IsSupported && width >= 4)
            {
                var colorVec128 = Vector128.Create(color);
                int sseChunks = width / 4;
                
                for (int chunk = 0; chunk < sseChunks; chunk++)
                {
                    Sse2.Store(rowPtr + vectorPos, colorVec128);
                    vectorPos += 4;
                }
            }

            // Handle remaining pixels with manual loop unrolling
            int remaining = width - vectorPos;
            uint* ptr = rowPtr + vectorPos;
            
            // Unroll by 4 for better pipeline utilization
            while (remaining >= 4)
            {
                ptr[0] = color;
                ptr[1] = color;
                ptr[2] = color;
                ptr[3] = color;
                ptr += 4;
                remaining -= 4;
            }
            
            // Handle final pixels
            for (int i = 0; i < remaining; i++)
            {
                ptr[i] = color;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ClearPixelBufferRegionSSE(int startX, int width)
        {
            if (width <= 0) return;

            // Clear multiple rows efficiently using SSE
            for (int y = 0; y < textureHeight; y++)
            {
                uint* clearPtr = pixelPtr32 + (y * textureWidth + startX);
                ClearPixelsSSE(clearPtr, width);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateTexture()
        {
            Raylib.UpdateTexture(streamingTexture.Texture, pixelPtr32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Render(int screenWidth, int screenHeight, int pad)
        {
            if (!initialized) return;

            var src = new Raylib_cs.Rectangle(0, 0, textureWidth, textureHeight);
            var dst = new Raylib_cs.Rectangle(0, pad, screenWidth, screenHeight - (pad << 1));
            Raylib.DrawTexturePro(streamingTexture.Texture, src, dst, Vector2.Zero, 0f, Raylib_cs.Color.White);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetWindow(float newWindow)
        {
            if (newWindow <= 0) return;
            
            cachedWindow = newWindow;
            cachedTicksPerPixel = cachedWindow / textureWidth;
            invTicksPerPixel = textureWidth / cachedWindow;
            pixelsPerTick = invTicksPerPixel;
            forceFullRedraw = true;
            lastRenderedColumn = -1;
        }

        public static void Cleanup()
        {
            if (!initialized) return;

            if (Raylib.IsTextureValid(streamingTexture.Texture))
                Raylib.UnloadRenderTexture(streamingTexture);
            
            if (bufferHandle.IsAllocated)
                bufferHandle.Free();
            
            //initialized = false;
            forceFullRedraw = true;
        }
    }
}