using Raylib_cs;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteRenderer
    {
        private const int WHITE_KEY_HEIGHT = 8;
        private const int TOTAL_TEXTURE_HEIGHT = 128 * WHITE_KEY_HEIGHT;

        private static int textureWidth = 4096;
        private static int textureHeight = TOTAL_TEXTURE_HEIGHT;

        private static RenderTexture2D streamingTexture, tempTexture;
        private static Texture2D whitePixel;
        private static byte[] pixelBuffer;
        private static GCHandle bufferHandle;
        private static IntPtr bufferPtr;

        private static readonly int[] noteToPixelY = new int[128];
        private static readonly int[] noteHeights = new int[128];
        private static readonly Dictionary<uint, Raylib_cs.Color> colorCache = new(256);

        private static float pixelsPerTick = 1f;
        private static int lastRenderedColumn = 0;

        private static bool initialized = false;
        private static bool forceFullRedraw = true;

        public static float Window { get; private set; } = 2000f;
        public static bool EnableGlow { get; set; } = false;
        public static int RenderedColumns { get; private set; } = 0;

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
            tempTexture = Raylib.LoadRenderTexture(textureWidth, textureHeight);

            Raylib_cs.Image whiteImage = Raylib.GenImageColor(1, 1, Raylib_cs.Color.White);
            whitePixel = Raylib.LoadTextureFromImage(whiteImage);
            Raylib.UnloadImage(whiteImage);

            int bufferSize = textureWidth * textureHeight * 4;
            pixelBuffer = new byte[bufferSize];
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
            Raylib.BeginTextureMode(tempTexture);
            Raylib.ClearBackground(Raylib_cs.Color.Black);

            Raylib_cs.Rectangle src = new(delta, 0, textureWidth - delta, -textureHeight);
            Raylib_cs.Rectangle dst = new(0, 0, textureWidth - delta, textureHeight);
            Raylib.DrawTexturePro(streamingTexture.Texture, src, dst, Vector2.Zero, 0f, Raylib_cs.Color.White);
            Raylib.EndTextureMode();

            (streamingTexture, tempTexture) = (tempTexture, streamingTexture);

            Raylib.BeginTextureMode(streamingTexture);
            float startTick = tick - (Window * 0.5f) + ((float)(textureWidth - delta) / textureWidth) * Window;
            RenderColumns(textureWidth - delta, delta, startTick);
            Raylib.EndTextureMode();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderFullTexture(float tick)
        {
            Raylib.BeginTextureMode(streamingTexture);
            Raylib.ClearBackground(Raylib_cs.Color.Black);
            float startTick = tick - (Window * 0.5f);
            RenderColumns(0, textureWidth, startTick);
            Raylib.EndTextureMode();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RenderColumns(int startX, int width, float startTick)
        {
            var notes = NoteProcessor.AllNotes;
            if (notes.Length == 0) return;

            float ticksPerPixel = Window / textureWidth;
            float endTick = startTick + (width * ticksPerPixel);
            int startIndex = FindNotesInRange(notes, startTick - 200f);

            for (int i = startIndex; i < notes.Length; i++)
            {
                ref var note = ref notes[i];
                if (note.startTime > endTick) break;
                if (note.endTime < startTick) continue;

                float startPx = (note.startTime - startTick) / ticksPerPixel;
                float endPx = (note.endTime - startTick) / ticksPerPixel;

                int x1 = Math.Max(0, (int)startPx);
                int x2 = Math.Min(width, (int)endPx + 1);

                if (x2 <= x1) continue;

                int y = noteToPixelY[note.NoteNumber];
                int h = noteHeights[note.NoteNumber];

                Raylib_cs.Color col = GetNoteColor(note.Color, EnableGlow);
                Raylib.DrawRectangle(startX + x1, y, x2 - x1, h, col);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindNotesInRange(NoteProcessor.OptimizedEnhancedNote[] notes, float startTick)
        {
            int left = 0, right = notes.Length - 1, result = notes.Length;

            while (left <= right)
            {
                int mid = (left + right) >> 1;
                if (notes[mid].startTime >= startTick)
                {
                    result = mid;
                    right = mid - 1;
                }
                else
                {
                    left = mid + 1;
                }
            }

            return Math.Max(0, result - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Raylib_cs.Color GetNoteColor(uint raw, bool glow)
        {
            uint key = glow ? (raw | 0x80000000u) : raw;

            if (colorCache.TryGetValue(key, out Raylib_cs.Color cached))
                return cached;

            byte r = (byte)((raw >> 16) & 0xFF);
            byte g = (byte)((raw >> 8) & 0xFF);
            byte b = (byte)(raw & 0xFF);

            if (glow)
            {
                r = (byte)Math.Min(255, r * 1.8f);
                g = (byte)Math.Min(255, g * 1.8f);
                b = (byte)Math.Min(255, b * 1.8f);
            }

            Raylib_cs.Color color = new(r, g, b, (byte)255);
            if (colorCache.Count < 256)
                colorCache[key] = color;
            return color;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Render(int screenWidth, int screenHeight, int pad)
        {
            if (!initialized) return;

            Raylib_cs.Rectangle src = new(0, 0, textureWidth, -textureHeight);
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
            Raylib.UnloadTexture(whitePixel);
            if (bufferHandle.IsAllocated) bufferHandle.Free();
            colorCache.Clear();
        }

        public static void Shutdown()
        {
            Cleanup();
            initialized = false;
        }
    }
}
