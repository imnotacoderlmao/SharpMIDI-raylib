using Raylib_cs;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;
using System.Threading.Tasks;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteRenderer
    {
        private const int WHITE_KEY_HEIGHT = 8;
        private const int TOTAL_TEXTURE_HEIGHT = 128 * WHITE_KEY_HEIGHT;

        // texture dimensions (textureWidth set in Initialize)
        private static int textureWidth = 4096;
        private static int textureHeight = TOTAL_TEXTURE_HEIGHT;

        private static RenderTexture2D streamingTexture;
        private static byte[] pixelBuffer;
        private static GCHandle bufferHandle;
        private static IntPtr bufferPtr;

        // precomputed per-note layout
        private static readonly int[] noteToPixelY = new int[128];
        private static readonly int[] noteHeights = new int[128];

        private static float pixelsPerTick = 1f;
        private static int lastRenderedColumn = 0;

        private static bool initialized = false;
        private static bool forceFullRedraw = true;

        public static float Window { get; private set; } = 2000f;
        public static int RenderedColumns { get; private set; } = 0;

        // small helper struct
        private struct NoteRect { public int X, Y, Width, Height; }

        static NoteRenderer()
        {
            for (int i = 0; i < 128; i++)
            {
                float yStart = TOTAL_TEXTURE_HEIGHT * ((127 - i) / 128f);
                float yEnd = TOTAL_TEXTURE_HEIGHT * ((128 - i) / 128f);
                noteToPixelY[i] = (int)yStart;
                noteHeights[i] = Math.Max(1, (int)(yEnd - yStart));
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

            int bufferSize = textureWidth * textureHeight * 4;
            pixelBuffer = new byte[bufferSize];
            bufferHandle = GCHandle.Alloc(pixelBuffer, GCHandleType.Pinned);
            bufferPtr = bufferHandle.AddrOfPinnedObject();

            // start with cleared texture
            Raylib.BeginTextureMode(streamingTexture);
            Raylib.ClearBackground(Raylib_cs.Color.Black);
            Raylib.EndTextureMode();

            lastRenderedColumn = 0;
            forceFullRedraw = true;
            initialized = true;
        }

        // main per-frame update: scroll and render newly exposed columns
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe void UpdateStreaming(float tick)
        {
            if (!initialized || !NoteProcessor.IsReady) return;

            int newColumn = (int)(tick * pixelsPerTick) % textureWidth;
            int delta = (newColumn - lastRenderedColumn + textureWidth) % textureWidth;

            if (forceFullRedraw || delta >= textureWidth)
            {
                // full redraw
                RenderColumnsOptimized(0, textureWidth, tick - Window * 0.5f);
                UpdateTexture();
                forceFullRedraw = false;
                RenderedColumns = textureWidth;
            }
            else if (delta > 0)
            {
                // scroll buffer (move pixels left by delta)
                ScrollPixelBuffer(delta);

                float startTick = tick - (Window * 0.5f) + ((float)(textureWidth - delta) / textureWidth) * Window;
                RenderColumnsOptimized(textureWidth - delta, delta, startTick);

                // upload entire buffer to GPU in one call (stable)
                UpdateTexture();
                RenderedColumns = delta;
            }
            else
            {
                RenderedColumns = 0;
            }

            lastRenderedColumn = newColumn;
        }

        // scroll pixel buffer left by delta pixels (in-place memcopy per row)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void ScrollPixelBuffer(int delta)
        {
            if (delta <= 0 || delta >= textureWidth) return;

            int bytesPerRow = textureWidth * 4;
            int shiftPixels = textureWidth - delta;
            int shiftBytes = shiftPixels * 4;
            int clearBytes = delta * 4;

            fixed (byte* basePtr = pixelBuffer)
            {
                for (int y = 0; y < textureHeight; y++)
                {
                    byte* rowStart = basePtr + (y * bytesPerRow);
                    byte* src = rowStart + (delta * 4);
                    byte* dst = rowStart;

                    // move the block left
                    Buffer.MemoryCopy(src, dst, shiftBytes, shiftBytes);

                    // clear the newly exposed right columns
                    byte* clearStart = rowStart + shiftBytes;
                    Unsafe.InitBlock(clearStart, 0, (uint)clearBytes);
                }
            }
        }

        // Render columns into pixelBuffer memory for the given startX..startX+width
        // relies on NoteProcessor.AllNotes and NoteProcessor.AllNoteColors
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void RenderColumnsOptimized(int startX, int width, float startTick)
        {
            var notes = NoteProcessor.AllNotes;
            var noteColors = NoteProcessor.AllNoteColors; // per-note ARGB packed uint (precomputed)
            if (notes.Length == 0) return;

            // clear only the region we will draw (startX..startX+width)
            ClearPixelBufferRegion(startX, width);

            float ticksPerPixel = Window / textureWidth;
            float endTick = startTick + (width * ticksPerPixel);
            int startIndex = FindNotesInRange(notes, startTick);

            byte* bufferBytePtr = (byte*)bufferPtr;
            uint* buf32 = (uint*)bufferPtr;

            // iterate notes in sorted order (NoteProcessor already sorts by start and layer)
            for (int i = startIndex; i < notes.Length; i++)
            {
                ref var note = ref notes[i];

                if (note.StartTick > endTick) break;                 // no more visible notes
                if (note.EndTick < startTick) continue;             // note ends before region

                // map note ticks to pixel x
                float startPx = (note.StartTick - startTick) / ticksPerPixel;
                float endPx = (note.EndTick - startTick) / ticksPerPixel;

                int x1 = Math.Max(0, (int)startPx);
                int x2 = Math.Min(width, (int)endPx + 1);
                if (x2 <= x1) continue;

                var r = noteToPixelY[note.NoteNumber];
                var h = noteHeights[note.NoteNumber];

                var rect = new NoteRect
                {
                    X = startX + x1,
                    Y = r,
                    Width = x2 - x1,
                    Height = h
                };

                // use color precomputed by NoteProcessor: per-note full ARGB uint
                // noteColors array is indexed by note index i
                uint packedColor = noteColors[i];

                DrawNoteSIMD(buf32, rect, packedColor);
            }
        }

        // Draw a filled rect quickly using SIMD for wide spans and scalar fallback for remainder
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void DrawNoteSIMD(uint* baseBuf32, NoteRect note, uint colorPattern)
        {
            int maxX = Math.Min(note.X + note.Width, textureWidth);
            int maxY = Math.Min(note.Y + note.Height, textureHeight);
            int x = Math.Max(0, note.X);
            int y = Math.Max(0, note.Y);

            if (maxX <= x || maxY <= y) return;

            int actualWidth = maxX - x;
            int rowStartIndex = y * textureWidth + x;

            // SIMD fill when width large enough
            int vecWidth = Vector<uint>.Count;
            if (actualWidth >= vecWidth)
            {
                Vector<uint> vecColor = new Vector<uint>(colorPattern);

                for (int py = y; py < maxY; py++)
                {
                    uint* rowPtr = baseBuf32 + (py * textureWidth + x);
                    int px = 0;

                    // SIMD blocks
                    for (; px <= actualWidth - vecWidth; px += vecWidth)
                    {
                        Unsafe.WriteUnaligned(rowPtr + px, vecColor);
                    }

                    // remainder
                    for (; px < actualWidth; px++)
                    {
                        rowPtr[px] = colorPattern;
                    }
                }
            }
            else // small widths: simple scalar loop
            {
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

        // only clear the columns we will draw (faster than wiping whole buffer)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ClearPixelBufferRegion(int startX, int width)
        {
            if (width <= 0) return;

            int bytesPerRow = textureWidth * 4;
            int clearBytesPerRow = width * 4;

            fixed (byte* basePtr = pixelBuffer)
            {
                for (int y = 0; y < textureHeight; y++)
                {
                    byte* clearStart = basePtr + (y * bytesPerRow) + (startX * 4);
                    Unsafe.InitBlock(clearStart, 0, (uint)clearBytesPerRow);
                }
            }
        }

        // upload the pinned buffer to GPU (single call)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void UpdateTexture()
        {
            Raylib.UpdateTexture(streamingTexture.Texture, (void*)bufferPtr);
        }

        // find notes that might overlap the visible window (binary search)
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
                else left = mid + 1;
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

            Array.Clear(pixelBuffer, 0, pixelBuffer.Length);
            lastRenderedColumn = 0;
            forceFullRedraw = true;
        }

        public static void Shutdown() => Cleanup();
    }
}
