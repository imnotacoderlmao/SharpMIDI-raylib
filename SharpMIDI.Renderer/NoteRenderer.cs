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
        private const int TOTAL_TEXTURE_HEIGHT = 128;  // 1 pixel per MIDI note

        private static int textureWidth = 2048;
        private static int textureHeight = TOTAL_TEXTURE_HEIGHT;

        // We will use a Texture2D and a pinned uint[] buffer
        private static Texture2D streamingTexture;
        private static uint[] pixelBuffer; // 32-bit ARGB
        private static GCHandle bufferHandle;
        private static uint* pixelPtr; // pinned pointer for intrinsics

        // Note Y lookup (vertical flip)
        private static readonly byte[] noteToPixelY = new byte[128];

        private static float cachedWindow = 2000f;
        private static float cachedTicksPerPixel;
        private static float invTicksPerPixel;
        private static float pixelsPerTick;

        private static int lastRenderedColumn = -1;
        private static float lastRenderTick = 0;
        private static float currentTick = 0;
        private static bool forceFullRedraw = true;
        public static bool initialized = false;

        public static float Window => cachedWindow;
        public static int RenderedColumns { get; private set; } = 0;

        private static readonly Vector128<uint> Zero128 = Vector128<uint>.Zero;
        private static readonly Vector256<uint> Zero256 = Vector256<uint>.Zero;

        static NoteRenderer()
        {
            for (int i = 0; i < 128; i++) noteToPixelY[i] = (byte)(127 - i);
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

            // Create Texture2D from blank Image
            Raylib_cs.Image img = Raylib.GenImageColor(textureWidth, textureHeight, Raylib_cs.Color.Blank);
            streamingTexture = Raylib.LoadTextureFromImage(img);
            Raylib.UnloadImage(img);

            // allocate and pin pixel buffer
            pixelBuffer = new uint[textureWidth * textureHeight];
            bufferHandle = GCHandle.Alloc(pixelBuffer, GCHandleType.Pinned);
            pixelPtr = (uint*)bufferHandle.AddrOfPinnedObject();

            // clear buffer
            ClearAllBuffer();

            // push initial texture data
            Raylib.UpdateTexture(streamingTexture, pixelPtr);

            lastRenderedColumn = -1;
            forceFullRedraw = true;
            initialized = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void UpdateStreaming(float tick)
        {
            if (!NoteProcessor.IsReady || NoteProcessor.BucketOffsets.Length < 2)
                return;

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

            float tickPosition = currentTick * pixelsPerTick;
            int newColumn = (int)tickPosition % textureWidth;
            if (newColumn < 0) newColumn += textureWidth;

            int delta = newColumn >= lastRenderedColumn
                ? newColumn - lastRenderedColumn
                : (textureWidth - lastRenderedColumn) + newColumn;

            if (lastRenderedColumn == -1) delta = textureWidth;

            if (forceFullRedraw || delta >= textureWidth)
            {
                float startTick = currentTick - (cachedWindow * 0.5f);
                RenderColumns(0, textureWidth, startTick);
                UpdateTexture();
                forceFullRedraw = false;
                RenderedColumns = textureWidth;
            }
            else if (delta > 0)
            {
                // scroll left by delta columns and render the newly-exposed right region
                ScrollBuffer(delta);
                float startTick = currentTick - (cachedWindow * 0.5f) + (cachedWindow * (textureWidth - delta) / textureWidth);
                RenderColumns(textureWidth - delta, delta, startTick);
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
        private static void ScrollBuffer(int delta)
        {
            if (delta <= 0 || delta >= textureWidth) return;

            int rows = textureHeight;
            int pixelsPerRow = textureWidth;
            int shift = delta;
            int keepPixels = (textureWidth - shift);

            // For each row, memmove left by delta and clear right area
            for (int y = 0; y < rows; y++)
            {
                uint* row = pixelPtr + (y * pixelsPerRow);
                // use intrinsics to copy chunks
                int pos = 0;
                int count = keepPixels;
                if (Avx2.IsSupported && count >= 8)
                {
                    int chunks = count / 8;
                    for (int c = 0; c < chunks; c++)
                    {
                        var v = Avx2.LoadVector256(row + shift + pos);
                        Avx2.Store(row + pos, v);
                        pos += 8;
                    }
                }
                else if (Sse2.IsSupported && count >= 4)
                {
                    int chunks = count / 4;
                    for (int c = 0; c < chunks; c++)
                    {
                        var v = Sse2.LoadVector128(row + shift + pos);
                        Sse2.Store(row + pos, v);
                        pos += 4;
                    }
                }
                // scalar remainder
                for (int i = pos; i < count; i++) row[i] = row[shift + i];

                // clear rightmost 'shift' pixels
                ClearPixels(row + keepPixels, shift);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearAllBuffer()
        {
            int total = textureWidth * textureHeight;
            if (Avx2.IsSupported)
            {
                int pos = 0;
                var z = Vector256<uint>.Zero;
                int chunks = total / 8;
                for (int i = 0; i < chunks; i++)
                {
                    Avx2.Store(pixelPtr + pos, z);
                    pos += 8;
                }
                for (int i = pos; i < total; i++) pixelPtr[i] = 0;
            }
            else if (Sse2.IsSupported)
            {
                int pos = 0;
                var z = Vector128<uint>.Zero;
                int chunks = total / 4;
                for (int i = 0; i < chunks; i++)
                {
                    Sse2.Store(pixelPtr + pos, z);
                    pos += 4;
                }
                for (int i = pos; i < total; i++) pixelPtr[i] = 0;
            }
            else
            {
                for (int i = 0; i < total; i++) pixelPtr[i] = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearPixels(uint* ptr, int count)
        {
            if (count <= 0) return;
            if (Avx2.IsSupported && count >= 8)
            {
                var z = Vector256<uint>.Zero;
                int chunks = count / 8;
                for (int i = 0; i < chunks; i++) Avx2.Store(ptr + (i * 8), z);
                int pos = chunks * 8;
                for (int i = pos; i < count; i++) ptr[i] = 0;
            }
            else if (Sse2.IsSupported && count >= 4)
            {
                var z = Vector128<uint>.Zero;
                int chunks = count / 4;
                for (int i = 0; i < chunks; i++) Sse2.Store(ptr + (i * 4), z);
                int pos = chunks * 4;
                for (int i = pos; i < count; i++) ptr[i] = 0;
            }
            else
            {
                for (int i = 0; i < count; i++) ptr[i] = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderColumns(int startX, int width, float startTick)
        {
            // safety
            if (startX < 0 || width <= 0) return;

            // clear region
            for (int y = 0; y < textureHeight; y++)
            {
                uint* row = pixelPtr + (y * textureWidth + startX);
                ClearPixels(row, width);
            }

            var packed = NoteProcessor.AllPackedNotes;
            var buckets = NoteProcessor.BucketOffsets;
            int bucketSize = NoteProcessor.BucketSize;
            if (packed.Length == 0 || buckets.Length == 0) return;

            float endTick = startTick + width * cachedTicksPerPixel;
            int viewBucket = Math.Max(0, (int)(startTick / bucketSize));
            int startBucket = Math.Max(0, viewBucket - 1);
            if (startBucket >= buckets.Length - 1) return;

            int totalNotes = NoteProcessor.TotalPackedNotes;
            int noteIndex = buckets[startBucket];
            int currentBucket = startBucket;
            int nextBucketBound = (currentBucket + 1 < buckets.Length) ? buckets[currentBucket + 1] : totalNotes;
            int bucketStartTick = currentBucket * bucketSize;

            // iterate through notes sequentially (buckets are chronological)
            while (noteIndex < totalNotes)
            {
                // move to next bucket if needed
                while (noteIndex >= nextBucketBound)
                {
                    currentBucket++;
                    bucketStartTick = currentBucket * bucketSize;
                    nextBucketBound = (currentBucket + 1 < buckets.Length) ? buckets[currentBucket + 1] : totalNotes;
                }

                // unpack note (8 bytes)
                int byteOffset = noteIndex * 8;
                ulong packedValue;
                fixed (byte* bptr = &packed[byteOffset])
                {
                    packedValue = *(ulong*)bptr;
                }

                int relStart = (int)(packedValue & 0x7FFu);
                int duration = (int)((packedValue >> 11) & 0xFFFFu);
                int noteNumber = (int)((packedValue >> 27) & 0x7Fu);
                int colorIndex = (int)((packedValue >> 34) & 0x0Fu);
                // trackIndex currently stored but unused by renderer
                //int trackIndex = (int)((packedValue >> 38) & 0xFFFFu);

                int absStart = bucketStartTick + relStart;
                int absEnd = absStart + duration;

                // Early termination: notes are chronological
                if (absStart > endTick) break;
                if (absEnd < startTick) { noteIndex++; continue; }

                // Map to pixel X
                float startPx = (absStart - startTick) * invTicksPerPixel;
                float endPx = (absEnd - startTick) * invTicksPerPixel;
                int x1 = Math.Max(0, (int)startPx);
                int x2 = Math.Min(width, (int)endPx + 1);
                if (x2 <= x1) { noteIndex++; continue; }

                int y = noteToPixelY[noteNumber];

                uint color = NoteProcessor.trackColors[colorIndex & 0x0F];

                // draw single-row fast path
                uint* rowPtr = pixelPtr + (y * textureWidth + startX + x1);
                int w = x2 - x1;
                FillRow(rowPtr, w, color);

                noteIndex++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FillRow(uint* rowPtr, int width, uint color)
        {
            if (width <= 0) return;

            if (Avx2.IsSupported && width >= 8)
            {
                var v = Vector256.Create(color);
                int chunks = width / 8;
                for (int i = 0; i < chunks; i++) Avx2.Store(rowPtr + (i * 8), v);
                int pos = chunks * 8;
                for (int i = pos; i < width; i++) rowPtr[i] = color;
            }
            else if (Sse2.IsSupported && width >= 4)
            {
                var v = Vector128.Create(color);
                int chunks = width / 4;
                for (int i = 0; i < chunks; i++) Sse2.Store(rowPtr + (i * 4), v);
                int pos = chunks * 4;
                for (int i = pos; i < width; i++) rowPtr[i] = color;
            }
            else
            {
                for (int i = 0; i < width; i++) rowPtr[i] = color;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateTexture()
        {
            Raylib.UpdateTexture(streamingTexture, pixelPtr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Render(int screenWidth, int screenHeight, int pad)
        {
            if (!initialized) return;

            var src = new Raylib_cs.Rectangle(0, 0, textureWidth, textureHeight);
            var dst = new Raylib_cs.Rectangle(0, pad, screenWidth, screenHeight - (pad << 1));
            Raylib.DrawTexturePro(streamingTexture, src, dst, Vector2.Zero, 0f, Raylib_cs.Color.White);
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

            if (Raylib.IsTextureValid(streamingTexture))
                Raylib.UnloadTexture(streamingTexture);

            if (bufferHandle.IsAllocated)
                bufferHandle.Free();
            forceFullRedraw = true;
        }
    }
}
