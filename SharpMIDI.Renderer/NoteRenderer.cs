using Raylib_cs;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;
using System;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteRenderer
    {
        private const int WHITE_KEY_HEIGHT = 8;
        private const int TOTAL_TEXTURE_HEIGHT = 128 * WHITE_KEY_HEIGHT;

        private static int textureWidth = 4096;
        private static int textureHeight = TOTAL_TEXTURE_HEIGHT;

        private static RenderTexture2D streamingTexture, tempTexture;
        private static byte[] pixelBuffer;
        private static uint[] pixelBuffer32; // For faster 32-bit operations
        private static GCHandle bufferHandle;
        private static IntPtr bufferPtr;

        private static readonly int[] noteToPixelY = new int[128];
        private static readonly int[] noteHeights = new int[128];
        private static readonly uint[] colorCache = new uint[16]; // Pre-computed ARGB colors
        private static readonly byte[][] colorCacheBytes = new byte[16][]; // Pre-computed RGBA bytes

        private static float pixelsPerTick = 1f;
        private static int lastRenderedColumn = 0;

        private static bool initialized = false;
        private static bool forceFullRedraw = true;

        // SIMD optimization helpers
        private static readonly Vector4[] colorVectors = new Vector4[16];
        private static readonly byte[] tempRowBuffer = new byte[16384 * 4]; // For row-wise processing

        public static float Window { get; private set; } = 2000f;
        public static bool EnableGlow { get; set; } = false;
        public static int RenderedColumns { get; private set; } = 0;

        private struct NoteRect
        {
            public int X, Y, Width, Height;
        }

        static NoteRenderer()
        {
            for (int i = 0; i < 128; i++)
            {
                float yStart = TOTAL_TEXTURE_HEIGHT * ((127 - i) / 128f);
                float yEnd = TOTAL_TEXTURE_HEIGHT * ((128 - i) / 128f);
                noteToPixelY[i] = (int)yStart;
                noteHeights[i] = Math.Max(1, (int)(yEnd - yStart));
            }

            InitializeColorCache();
        }

        private static void InitializeColorCache()
        {
            for (int i = 0; i < 16; i++)
            {
                uint fullColor = NoteProcessor.GetTrackColor(i);
                colorCache[i] = fullColor;

                // Pre-compute RGBA byte arrays for direct pixel manipulation
                colorCacheBytes[i] = new byte[4]
                {
                    (byte)(fullColor & 0xFF),         // B
                    (byte)((fullColor >> 8) & 0xFF),  // G  
                    (byte)((fullColor >> 16) & 0xFF), // R
                    255                               // A
                };

                // Pre-compute SIMD vectors
                colorVectors[i] = new Vector4(
                    (fullColor >> 16) & 0xFF, // R
                    (fullColor >> 8) & 0xFF,  // G
                    fullColor & 0xFF,         // B
                    255                       // A
                ) / 255f;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Initialize(int width, int height)
        {
            if (initialized && width == textureWidth) return;

            if (initialized) Cleanup();

            textureWidth = width;
            pixelsPerTick = textureWidth / Window;

            streamingTexture = Raylib.LoadRenderTexture(textureWidth, textureHeight);
            tempTexture = Raylib.LoadRenderTexture(textureWidth, textureHeight);

            int bufferSize = textureWidth * textureHeight * 4;
            pixelBuffer = new byte[bufferSize];
            pixelBuffer32 = new uint[textureWidth * textureHeight];
            bufferHandle = GCHandle.Alloc(pixelBuffer, GCHandleType.Pinned);
            bufferPtr = bufferHandle.AddrOfPinnedObject();

            Raylib.BeginTextureMode(streamingTexture);
            Raylib.ClearBackground(Raylib_cs.Color.Black);
            Raylib.EndTextureMode();

            lastRenderedColumn = 0;
            forceFullRedraw = true;
            initialized = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void UpdateStreaming(float tick)
        {
            if (!initialized || !NoteProcessor.IsReady) return;

            int newColumn = (int)(tick * pixelsPerTick) % textureWidth;
            int delta = (newColumn - lastRenderedColumn + textureWidth) % textureWidth;

            if (forceFullRedraw || delta >= textureWidth)
            {
                RenderFullTexture(tick);
                RenderedColumns = textureWidth;
                forceFullRedraw = false;
            }
            else if (delta > 0)
            {
                ScrollAndRenderColumns(tick, delta);
                RenderedColumns = delta;
            }
            else
            {
                RenderedColumns = 0;
            }

            lastRenderedColumn = newColumn;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ScrollAndRenderColumns(float tick, int delta)
        {
            // First, scroll the pixel buffer in memory
            ScrollPixelBuffer(delta);

            // Then render new columns into the scrolled buffer
            float startTick = tick - (Window * 0.5f) + ((float)(textureWidth - delta) / textureWidth) * Window;
            RenderColumnsOptimized(textureWidth - delta, delta, startTick);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ScrollPixelBuffer(int delta)
        {
            if (delta <= 0 || delta >= textureWidth) return;

            int shiftPixels = textureWidth - delta;
            int bytesPerRow = textureWidth * 4;
            int shiftBytes = shiftPixels * 4;

            // Scroll each row left by delta pixels
            unsafe
            {
                fixed (byte* bufferPtr = pixelBuffer)
                {
                    for (int y = 0; y < textureHeight; y++)
                    {
                        byte* rowStart = bufferPtr + (y * bytesPerRow);
                        byte* srcPtr = rowStart + (delta * 4);
                        byte* dstPtr = rowStart;

                        // Move existing pixels left
                        Buffer.MemoryCopy(srcPtr, dstPtr, shiftBytes, shiftBytes);

                        // Clear the new area on the right
                        byte* clearStart = rowStart + shiftBytes;
                        Unsafe.InitBlock(clearStart, 0, (uint)(delta * 4));
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderFullTexture(float tick)
        {
            float startTick = tick - (Window * 0.5f);
            RenderColumnsOptimized(0, textureWidth, startTick);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderColumnsOptimized(int startX, int width, float startTick)
        {
            var notes = NoteProcessor.AllNotes;
            if (notes.Length == 0) return;

            // Only clear the specific region we're updating (not the entire buffer)
            if (startX == 0 && width == textureWidth)
            {
                // Full redraw - clear everything
                ClearPixelBufferOptimized(startX, width);
            }
            else
            {
                // Partial update - only clear the new columns area
                ClearPixelBufferRegion(startX, width);
            }

            float ticksPerPixel = Window / textureWidth;
            float endTick = startTick + (width * ticksPerPixel);
            int startIndex = FindNotesInRange(notes, startTick);

            // Render notes in their original order to respect layering
            RenderNotesRespectingLayers(notes, startIndex, startTick, endTick, startX, width, ticksPerPixel);

            // Update texture using UpdateTexture instead of Load/Unload cycle
            UpdateTextureOptimized(startX, width);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ClearPixelBufferRegion(int startX, int width)
        {
            // Clear only the specific columns we're about to render
            int bytesPerRow = textureWidth * 4;
            int clearBytesPerRow = width * 4;

            unsafe
            {
                fixed (byte* bufferPtr = pixelBuffer)
                {
                    for (int y = 0; y < textureHeight; y++)
                    {
                        byte* clearStart = bufferPtr + (y * bytesPerRow) + (startX * 4);
                        Unsafe.InitBlock(clearStart, 0, (uint)clearBytesPerRow);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderNotesRespectingLayers(NoteProcessor.OptimizedEnhancedNote[] notes, int startIndex,
            float startTick, float endTick, int startX, int width, float ticksPerPixel)
        {
            // Use the pre-pinned buffer pointer
            byte* bufferPtrLocal = (byte*)bufferPtr;

            int noteIndex = startIndex;
            const int batchSize = 2048;

            while (noteIndex < notes.Length)
            {
                int batchEnd = Math.Min(noteIndex + batchSize, notes.Length);

                for (int i = noteIndex; i < batchEnd; i++)
                {
                    ref var note = ref notes[i];
                    if (note.StartTick > endTick) goto BatchComplete;
                    if (note.EndTick < startTick) continue;

                    float startPx = (note.StartTick - startTick) / ticksPerPixel;
                    float endPx = (note.EndTick - startTick) / ticksPerPixel;

                    int x1 = Math.Max(0, (int)startPx);
                    int x2 = Math.Min(width, (int)endPx + 1);
                    if (x2 <= x1) continue;

                    // Get the color pattern for this note
                    uint colorIndex = note.Color & 0x0F;
                    byte* colorBytes = (byte*)Unsafe.AsPointer(ref colorCacheBytes[colorIndex][0]);
                    uint colorPattern = *(uint*)colorBytes;

                    // Create note rect and render immediately to preserve layering
                    var noteRect = new NoteRect
                    {
                        X = startX + x1,
                        Y = noteToPixelY[note.NoteNumber],
                        Width = x2 - x1,
                        Height = noteHeights[note.NoteNumber]
                    };

                    DrawNoteOptimizedSIMD(bufferPtrLocal, noteRect, colorPattern);
                }

                noteIndex = batchEnd;
            }

        BatchComplete:;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DrawNoteOptimizedSIMD(byte* bufferPtr, NoteRect note, uint colorPattern)
        {
            // Bounds checking
            int maxX = Math.Min(note.X + note.Width, textureWidth);
            int maxY = Math.Min(note.Y + note.Height, textureHeight);
            int x = Math.Max(0, note.X);
            int y = Math.Max(0, note.Y);

            if (maxX <= x || maxY <= y) return;

            int actualWidth = maxX - x;

            // Use different strategies based on width
            if (actualWidth >= 8) // Use SIMD for wider notes
            {
                Vector<uint> colorVec = new Vector<uint>(colorPattern);
                int vectorWidth = Vector<uint>.Count;

                for (int py = y; py < maxY; py++)
                {
                    uint* rowPtr = (uint*)(bufferPtr + (py * textureWidth + x) * 4);
                    int px = 0;

                    // SIMD fill for bulk of the width
                    for (; px <= actualWidth - vectorWidth; px += vectorWidth)
                    {
                        Unsafe.WriteUnaligned(rowPtr + px, colorVec);
                    }

                    // Handle remainder
                    for (; px < actualWidth; px++)
                    {
                        rowPtr[px] = colorPattern;
                    }
                }
            }
            else if (actualWidth > 1) // Use memset-style approach for medium notes
            {
                for (int py = y; py < maxY; py++)
                {
                    uint* rowPtr = (uint*)(bufferPtr + (py * textureWidth + x) * 4);
                    for (int px = 0; px < actualWidth; px++)
                    {
                        rowPtr[px] = colorPattern;
                    }
                }
            }
            else // Single pixel - direct write
            {
                for (int py = y; py < maxY; py++)
                {
                    *((uint*)(bufferPtr + (py * textureWidth + x) * 4)) = colorPattern;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ClearPixelBufferOptimized(int startX, int width)
        {
            if (startX == 0 && width == textureWidth)
            {
                // Clear entire buffer
                Array.Clear(pixelBuffer, 0, pixelBuffer.Length);
            }
            else
            {
                // Clear only specific region using parallel processing for large regions
                int totalPixels = width * textureHeight;
                if (totalPixels > 100000) // Parallel for large regions
                {
                    Parallel.For(0, textureHeight, y =>
                    {
                        int rowStart = (y * textureWidth + startX) * 4;
                        int clearBytes = width * 4;
                        Array.Clear(pixelBuffer, rowStart, clearBytes);
                    });
                }
                else
                {
                    for (int y = 0; y < textureHeight; y++)
                    {
                        int rowStart = (y * textureWidth + startX) * 4;
                        int clearBytes = width * 4;
                        Array.Clear(pixelBuffer, rowStart, clearBytes);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void UpdateTextureOptimized(int startX, int width)
        {
            // Use UpdateTexture instead of LoadTextureFromImage/UnloadTexture cycle
            // Use the pre-pinned buffer
            Raylib.UpdateTexture(streamingTexture.Texture, (void*)bufferPtr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindNotesInRange(NoteProcessor.OptimizedEnhancedNote[] notes, float startTick)
        {
            if (notes.Length == 0) return 0;

            const float overlapRange = 8192f;
            float searchStart = startTick - overlapRange;

            int left = 0, right = notes.Length - 1, result = notes.Length;

            while (left <= right)
            {
                int mid = (left + right) >> 1;
                if (notes[mid].StartTick >= searchStart)
                {
                    result = mid;
                    right = mid - 1;
                }
                else
                {
                    left = mid + 1;
                }
            }

            return result;
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
            Raylib.UnloadRenderTexture(tempTexture);
            if (bufferHandle.IsAllocated) bufferHandle.Free();
            Array.Clear(colorCache, 0, colorCache.Length);
        }

        public static void Shutdown()
        {
            Cleanup();
            initialized = false;
        }
    }
}