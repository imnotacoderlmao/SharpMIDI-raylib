using Raylib_cs;
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
        private const int TEXTURE_HEIGHT = 128;  // 1 pixel per MIDI note
        
        private static int textureWidth = 2048;
        private static Texture2D streamingTexture;
        private static uint[] pixelBuffer;
        private static GCHandle bufferHandle;
        private static uint* pixelPtr;

        // Note Y lookup (flipped)
        private static readonly byte[] noteToY = new byte[128];

        private static float currentWindow = 2000f;
        private static float ticksPerPixel;
        private static float pixelsPerTick;

        private static int lastColumn = -1;
        public static bool forceRedraw = true;
        public static float lastTick = 0;

        public static bool initialized = false;
        public static float Window => currentWindow;
        public static int NotesDrawnLastFrame { get; private set; } = 0;

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

            // Create texture
            var img = Raylib.GenImageColor(textureWidth, TEXTURE_HEIGHT, Raylib_cs.Color.Blank);
            streamingTexture = Raylib.LoadTextureFromImage(img);
            Raylib.UnloadImage(img);

            // Setup buffer
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
            pixelsPerTick = textureWidth / currentWindow;
        }

        public static void UpdateStreaming(float tick)
        {
            if (!NoteProcessor.IsReady)
                return;

            // Prevent backwards jumps
            if (!MIDIPlayer.stopping && tick < lastTick)
                tick = lastTick;
            lastTick = tick;

            float tickPos = tick * pixelsPerTick;
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
            
            // Scroll each row
            for (int y = 0; y < TEXTURE_HEIGHT; y++)
            {
                uint* row = pixelPtr + (y * textureWidth);
                
                // Copy left
                for (int x = 0; x < keepPixels; x++)
                    row[x] = row[x + pixels];
                
                // Clear right
                for (int x = keepPixels; x < textureWidth; x++)
                    row[x] = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderRegion(int startX, int width, float startTick)
        {
            if (width <= 0) return;

            NotesDrawnLastFrame = 0;

            // Clear region
            for (int y = 0; y < TEXTURE_HEIGHT; y++)
            {
                uint* row = pixelPtr + (y * textureWidth + startX);
                for (int x = 0; x < width; x++)
                    row[x] = 0;
            }

            var buckets = NoteProcessor.SortedBuckets;
            var bucketCounts = NoteProcessor.BucketCounts;
            if (buckets.Length == 0) return;

            float endTick = startTick + width * ticksPerPixel;
            int bucketSize = NoteProcessor.BucketSize;

            // Find bucket range
            int startBucket = Math.Max(0, (int)(startTick / bucketSize) - 1);
            int endBucket = Math.Min(buckets.Length - 1, (int)(endTick / bucketSize) + 1);

            if (startBucket >= buckets.Length) return;

            // Render notes from buckets
            for (int bucketIdx = startBucket; bucketIdx <= endBucket; bucketIdx++)
            {
                var bucket = buckets[bucketIdx];
                int count = bucketCounts[bucketIdx];

                if (bucket == null || count == 0) continue;

                int bucketStartTick = bucketIdx * bucketSize;

                // Use unsafe fixed block for performance
                fixed (ulong* bucketPtr = bucket)
                {
                    for (int noteIdx = 0; noteIdx < count; noteIdx++)
                    {
                        ulong packedValue = bucketPtr[noteIdx];

                        // Unpack note data
                        int relStart = (int)(packedValue & 0x7FF);
                        int duration = (int)((packedValue >> 11) & 0xFFFF);
                        int absStart = bucketStartTick + relStart;
                        int absEnd = absStart + duration;

                        // Cull notes outside viewport
                        if (absEnd < startTick || absStart > endTick) 
                            continue;

                        // Calculate pixel coordinates
                        float startPx = (absStart - startTick) / ticksPerPixel;
                        float endPx = (absEnd - startTick) / ticksPerPixel;

                        int x1 = Math.Max(0, (int)startPx);
                        int x2 = Math.Min(width, (int)endPx + 1);

                        if (x2 <= x1) continue;

                        // Get note properties
                        int noteNumber = (int)((packedValue >> 27) & 0x7F);
                        int colorIndex = (int)((packedValue >> 34) & 0x0F);

                        // Draw note
                        int y = noteToY[noteNumber];
                        uint color = NoteProcessor.trackColors[colorIndex];
                        uint* rowPtr = pixelPtr + (y * textureWidth + startX + x1);

                        int noteWidth = x2 - x1;
                        for (int x = 0; x < noteWidth; x++)
                        {
                            rowPtr[x] = color;
                        }
                        NotesDrawnLastFrame++;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearBuffer()
        {
            int total = textureWidth * TEXTURE_HEIGHT;
            for (int i = 0; i < total; i++)
                pixelPtr[i] = 0;
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

            //initialized = false;
            forceRedraw = true;
        }
    }
}