using Raylib_cs;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteRenderer
    {
        private const int TOTAL_TEXTURE_HEIGHT = 128;  // 1 pixel per note (0-127)

        // Reduce default texture width for better memory usage
        private static int textureWidth = 2048;  // Reduced from 4096
        private static int textureHeight = TOTAL_TEXTURE_HEIGHT;

        private static RenderTexture2D streamingTexture;
        private static byte[] pixelBuffer;
        private static GCHandle bufferHandle;
        private static IntPtr bufferPtr;

        // Simple 1:1 mapping - each MIDI note maps to exactly 1 pixel row
        private static readonly byte[] noteToPixelY = new byte[128];
        private static readonly byte[] noteHeights = new byte[128];

        private static float pixelsPerTick = 1f;
        private static int lastRenderedColumn = 0;

        private static bool initialized = false;
        private static bool forceFullRedraw = true;

        public static float Window { get; private set; } = 2000f;
        public static int RenderedColumns { get; private set; } = 0;

        // Compact note rect - use shorts instead of ints
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CompactNoteRect 
        { 
            public short X, Y, Width, Height;
            
            public CompactNoteRect(int x, int y, int w, int h)
            {
                X = (short)Math.Clamp(x, short.MinValue, short.MaxValue);
                Y = (short)Math.Clamp(y, short.MinValue, short.MaxValue);
                Width = (short)Math.Clamp(w, 0, short.MaxValue);
                Height = (short)Math.Clamp(h, 0, short.MaxValue);
            }
        }

        static NoteRenderer()
        {
            // Simple 1:1 mapping: MIDI note N maps to pixel row (127-N)
            for (int i = 0; i < 128; i++)
            {
                noteToPixelY[i] = (byte)(127 - i);  // Invert so high notes are at top
                noteHeights[i] = 1;  // Each note is exactly 1 pixel tall
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Initialize(int width, int height)
        {
            // Clamp maximum texture width to prevent excessive memory usage
            int maxTextureWidth = Math.Min(width, 8192); // Cap at 8K width
            
            if (initialized && maxTextureWidth == textureWidth) return;

            if (initialized) Cleanup();

            textureWidth = maxTextureWidth;
            pixelsPerTick = textureWidth / Window;

            streamingTexture = Raylib.LoadRenderTexture(textureWidth, textureHeight);

            int bufferSize = textureWidth * textureHeight * 4;
            
            // Only allocate if we don't have a buffer or need a larger one
            if (pixelBuffer == null || pixelBuffer.Length < bufferSize)
            {
                if (bufferHandle.IsAllocated) bufferHandle.Free();
                
                pixelBuffer = new byte[bufferSize];
                bufferHandle = GCHandle.Alloc(pixelBuffer, GCHandleType.Pinned);
                bufferPtr = bufferHandle.AddrOfPinnedObject();
            }
            else
            {
                // Reuse existing buffer, just clear it
                Array.Clear(pixelBuffer, 0, bufferSize);
            }

            // Start with cleared texture
            Raylib.BeginTextureMode(streamingTexture);
            Raylib.ClearBackground(Raylib_cs.Color.Black);
            Raylib.EndTextureMode();

            lastRenderedColumn = 0;
            forceFullRedraw = true;
            initialized = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe void UpdateStreaming(float tick)
        {
            if (!initialized || !NoteProcessor.IsReady) return;
            
            int newColumn = (int)(tick * pixelsPerTick) % textureWidth;
            int delta = (newColumn - lastRenderedColumn + textureWidth) % textureWidth;

            if (forceFullRedraw || delta >= textureWidth)
            {
                RenderColumnsOptimized(0, textureWidth, tick - Window * 0.5f);
                UpdateTexture();
                forceFullRedraw = false;
                RenderedColumns = textureWidth;
            }
            else if (delta > 0)
            {
                ScrollPixelBuffer(delta);
                float startTick = tick - (Window * 0.5f) + ((float)(textureWidth - delta) / textureWidth) * Window;
                RenderColumnsOptimized(textureWidth - delta, delta, startTick);
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
        private static unsafe void ScrollPixelBuffer(int delta)
        {
            if (delta <= 0 || delta >= textureWidth) return;

            int bytesPerRow = textureWidth * 4;
            int shiftBytes = (textureWidth - delta) * 4;
            int clearBytes = delta * 4;

            fixed (byte* basePtr = pixelBuffer)
            {
                // Process multiple rows at once for better cache efficiency
                const int rowsPerBatch = 4;
                int batchCount = textureHeight / rowsPerBatch;
                int remainingRows = textureHeight % rowsPerBatch;

                // Process batches
                for (int batch = 0; batch < batchCount; batch++)
                {
                    int startY = batch * rowsPerBatch;
                    for (int row = 0; row < rowsPerBatch; row++)
                    {
                        int y = startY + row;
                        byte* rowStart = basePtr + (y * bytesPerRow);
                        byte* src = rowStart + (delta * 4);
                        
                        Buffer.MemoryCopy(src, rowStart, shiftBytes, shiftBytes);
                        Unsafe.InitBlock(rowStart + shiftBytes, 0, (uint)clearBytes);
                    }
                }

                // Handle remaining rows
                for (int y = batchCount * rowsPerBatch; y < textureHeight; y++)
                {
                    byte* rowStart = basePtr + (y * bytesPerRow);
                    byte* src = rowStart + (delta * 4);
                    
                    Buffer.MemoryCopy(src, rowStart, shiftBytes, shiftBytes);
                    Unsafe.InitBlock(rowStart + shiftBytes, 0, (uint)clearBytes);
                }
            }
        }

        // Use bucket-based search instead of binary search
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindNotesInRangeBucketed(NoteProcessor.OptimizedEnhancedNote[] notes, float startTick)
        {
            if (notes.Length == 0) return 0;

            var bucketOffsets = NoteProcessor.BucketOffsets;
            if (bucketOffsets.Length == 0) return 0;

            float lookBack = 16384;
            float searchStartTick = startTick - lookBack;

            int bucketIndex = Math.Max(0, (int)(searchStartTick / NoteProcessor.BucketSize));
            
            if (bucketIndex >= bucketOffsets.Length - 1)
                return notes.Length;

            return bucketOffsets[bucketIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void RenderColumnsOptimized(int startX, int width, float startTick)
        {
            var notes = NoteProcessor.AllNotes;
            var noteColors = NoteProcessor.AllNoteColors;
            if (notes.Length == 0) return;

            ClearPixelBufferRegion(startX, width);

            float ticksPerPixel = Window / textureWidth;
            float endTick = startTick + (width * ticksPerPixel);
            int startIndex = FindNotesInRangeBucketed(notes, startTick);

            // Process notes in batches to improve cache locality
            const int batchSize = 64;
            
            for (int batchStart = startIndex; batchStart < notes.Length; batchStart += batchSize)
            {
                int batchEnd = Math.Min(batchStart + batchSize, notes.Length);
                
                for (int i = batchStart; i < batchEnd; i++)
                {
                    ref var note = ref notes[i];

                    if (note.StartTick > endTick) return; // Early exit - no more visible notes
                    if (note.EndTick < startTick) continue;

                    float startPx = (note.StartTick - startTick) / ticksPerPixel;
                    float endPx = (note.EndTick - startTick) / ticksPerPixel;

                    int x1 = Math.Max(0, (int)startPx);
                    int x2 = Math.Min(width, (int)endPx + 1);
                    if (x2 <= x1) continue;

                    var rect = new CompactNoteRect(
                        startX + x1,
                        noteToPixelY[note.NoteNumber],
                        x2 - x1,
                        noteHeights[note.NoteNumber]
                    );

                    uint packedColor = noteColors[i];
                    DrawNoteSIMD((uint*)bufferPtr, rect, packedColor);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void DrawNoteSIMD(uint* baseBuf32, CompactNoteRect note, uint colorPattern)
        {
            int maxX = Math.Min(note.X + note.Width, textureWidth);
            int maxY = Math.Min(note.Y + note.Height, textureHeight);
            int x = Math.Max((short)0, note.X);
            int y = Math.Max((short)0, note.Y);

            if (maxX <= x || maxY <= y) return;

            int actualWidth = maxX - x;

            // Use SIMD for wider rectangles, simple loop for narrow ones
            int vecWidth = Vector<uint>.Count;
            if (actualWidth >= vecWidth * 2) // Only use SIMD for reasonably wide rectangles
            {
                Vector<uint> vecColor = new Vector<uint>(colorPattern);

                for (int py = y; py < maxY; py++)
                {
                    uint* rowPtr = baseBuf32 + (py * textureWidth + x);
                    int px = 0;

                    for (; px <= actualWidth - vecWidth; px += vecWidth)
                    {
                        Unsafe.WriteUnaligned(rowPtr + px, vecColor);
                    }

                    for (; px < actualWidth; px++)
                    {
                        rowPtr[px] = colorPattern;
                    }
                }
            }
            else
            {
                // Simple scalar fill for narrow rectangles
                for (int py = y; py < maxY; py++)
                {
                    uint* rowPtr = baseBuf32 + (py * textureWidth + x);
                    for (int px = 0; px < actualWidth; px++)
                    {
                        rowPtr[px] = colorPattern;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ClearPixelBufferRegion(int startX, int width)
        {
            if (width <= 0) return;

            int bytesPerRow = textureWidth * 4;
            int clearBytesPerRow = width * 4;

            fixed (byte* basePtr = pixelBuffer)
            {
                // Clear multiple rows at once for better performance
                for (int y = 0; y < textureHeight; y++)
                {
                    byte* clearStart = basePtr + (y * bytesPerRow) + (startX * 4);
                    Unsafe.InitBlock(clearStart, 0, (uint)clearBytesPerRow);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void UpdateTexture()
        {
            Raylib.UpdateTexture(streamingTexture.Texture, (void*)bufferPtr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Render(int screenWidth, int screenHeight, int pad)
        {
            if (!initialized) return;

            Raylib_cs.Rectangle src = new(0, 0, textureWidth, textureHeight);
            Raylib_cs.Rectangle dst = new(0, pad, screenWidth, screenHeight - (pad << 1));
            Raylib.DrawTexturePro(streamingTexture.Texture, src, dst, Vector2.Zero, 0f, Raylib_cs.Color.White);
        }

        public static void SetWindow(float newWindow)
        {
            if (MathF.Abs(Window - newWindow) > 0.1f)
            {
                Window = newWindow;
                pixelsPerTick = textureWidth / Window;
                forceFullRedraw = true;
            }
        }

        public static void Cleanup()
        {
            if (!initialized) return;

            Raylib.UnloadRenderTexture(streamingTexture);
            // Don't free the buffer - keep it for reuse
            // if (bufferHandle.IsAllocated) bufferHandle.Free();
            forceFullRedraw = true;
        }

        public static void Shutdown() 
        {
            if (initialized)
            {
                Raylib.UnloadRenderTexture(streamingTexture);
                if (bufferHandle.IsAllocated) bufferHandle.Free();
                pixelBuffer = null;
                bufferPtr = IntPtr.Zero;
                initialized = false;
            }
        }
    }
}