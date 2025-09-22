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
        private static uint[] pixelBuffer;
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

            float tickPos = tick / ticksPerPixel; // Removed unnecessary multiplication and division
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
            
            // Optimized scrolling using pointer arithmetic
            for (int y = 0; y < TEXTURE_HEIGHT; y++)
            {
                uint* row = pixelPtr + (y * textureWidth);
                
                // Use memmove equivalent for better performance
                Buffer.MemoryCopy(row + pixels, row, keepPixels * sizeof(uint), keepPixels * sizeof(uint));
                
                // Clear right portion
                uint* clearStart = row + keepPixels;
                for (int x = 0; x < pixels; x++)
                    clearStart[x] = BLACK_ALPHA;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderRegion(int startX, int width, float startTick)
        {
            if (width <= 0) return;

            NotesDrawnLastFrame = 0;

            // Clear region to black - optimized bulk clear
            int clearSize = width * sizeof(uint);
            for (int y = 0; y < TEXTURE_HEIGHT; y++)
            {
                uint* row = pixelPtr + (y * textureWidth + startX);
                for (int x = 0; x < width; x++) // Simple loop is often faster than memset for small widths
                    row[x] = BLACK_ALPHA;
            }

            var buckets = NoteProcessor.SortedBuckets;
            if (buckets.Length == 0) return;

            float endTick = startTick + width * ticksPerPixel;
            int bucketSize = NoteProcessor.BucketSize;

            // Find bucket range with bounds checking
            int startBucket = Math.Max(0, (int)(startTick / bucketSize) - 1);
            int endBucket = Math.Min(buckets.Length - 1, (int)(endTick / bucketSize) + 1);

            if (startBucket >= buckets.Length) return;

            // Render notes from buckets - optimized unpacking
            for (int bucketIdx = startBucket; bucketIdx <= endBucket; bucketIdx++)
            {
                var bucket = buckets[bucketIdx];
                if (bucket == null || bucket.Count == 0) continue;

                int bucketStartTick = bucketIdx * bucketSize;

                // Direct access to List<uint> with optimized unpacking
                var bucketArray = bucket;
                int count = bucketArray.Count;
                
                for (int noteIdx = 0; noteIdx < count; noteIdx++)
                {
                    uint packed = bucketArray[noteIdx];

                    // Inline unpacking for maximum performance
                    int relStart = (int)(packed & 0x7FFu);
                    int duration = (int)((packed >> 11) & 0x1FFu);    // Updated for 9-bit duration
                    int noteNumber = (int)((packed >> 20) & 0x7Fu);   // Updated bit position
                    int colorIndex = (int)((packed >> 27) & 0x1Fu);   // Updated for 5-bit color
                    
                    int absStart = bucketStartTick + relStart;
                    int absEnd = absStart + duration;

                    // Early culling
                    if (absEnd < startTick || absStart > endTick) 
                        continue;

                    // Calculate pixel coordinates with single division
                    float startPx = (absStart - startTick) / ticksPerPixel;
                    float endPx = (absEnd - startTick) / ticksPerPixel;

                    int x1 = Math.Max(0, (int)startPx);
                    int x2 = Math.Min(width, (int)endPx + 1);

                    if (x2 <= x1) continue;

                    // Get precomputed RGBA color (now 32 colors available)
                    uint rgbaColor = BLACK_ALPHA | NoteProcessor.trackColors[colorIndex];

                    // Optimized drawing - direct pointer access
                    int y = noteToY[noteNumber];
                    uint* rowPtr = pixelPtr + (y * textureWidth + startX + x1);

                    int noteWidth = x2 - x1;
                    
                    // Unrolled loop for small widths, regular loop for larger ones
                    if (noteWidth <= 4)
                    {
                        switch (noteWidth)
                        {
                            case 4: rowPtr[3] = rgbaColor; goto case 3;
                            case 3: rowPtr[2] = rgbaColor; goto case 2;
                            case 2: rowPtr[1] = rgbaColor; goto case 1;
                            case 1: rowPtr[0] = rgbaColor; break;
                        }
                    }
                    else
                    {
                        for (int x = 0; x < noteWidth; x++)
                            rowPtr[x] = rgbaColor;
                    }
                    
                    NotesDrawnLastFrame++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearBuffer()
        {
            int total = textureWidth * TEXTURE_HEIGHT;
            for (int i = 0; i < total; i++)
                pixelPtr[i] = BLACK_ALPHA;
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