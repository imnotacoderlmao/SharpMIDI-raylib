using Raylib_cs;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    /// <summary>
    /// True GPU-accelerated renderer using streaming texture approach
    /// Renders notes to a scrolling texture buffer for maximum performance
    /// </summary>
    public static unsafe class NoteRenderer
    {
        // Piano key layout constants
        private const int WHITE_KEY_HEIGHT = 8;
        private const int BLACK_KEY_HEIGHT = 5;
        private const int TOTAL_TEXTURE_HEIGHT = 128 * WHITE_KEY_HEIGHT; // 1024 pixels for full piano range
        
        // Streaming texture dimensions
        private static int textureWidth = 4096;  // Will be set to window width
        private static int textureHeight = TOTAL_TEXTURE_HEIGHT;
        
        // GPU resources
        private static RenderTexture2D streamingTexture;
        private static RenderTexture2D tempTexture;
        private static Texture2D whitePixel;
        private static bool initialized = false;
        
        // Scrolling state
        private static float currentPosition = 0f;
        private static float pixelsPerTick = 1f;
        private static int lastRenderedColumn = 0;
        
        // Pre-allocated pixel data for direct texture updates
        private static byte[] pixelBuffer;
        private static GCHandle bufferHandle;
        private static IntPtr bufferPtr;
        
        // Note lookup arrays for fast rendering
        private static readonly int[] noteToPixelY = new int[128];
        private static readonly int[] noteHeights = new int[128];
        private static readonly bool[] isBlackKey = new bool[128];
        
        // Raylib_cs.Color cache
        private static readonly Dictionary<uint, Raylib_cs.Color> colorCache = new(256);
        
        public static float Window { get; set; } = 2000f;
        public static bool EnableGlow { get; set; } = true;
        public static int RenderedColumns { get; private set; } = 0;

        static NoteRenderer()
        {
            InitializePianoLayout();
        }

        private static void InitializePianoLayout()
        {
            const uint BK = 0x54A; // Black key pattern
            int totalWhiteKeys = 0;

            // First count number of white keys (so we can scale them to textureHeight)
            for (int i = 0; i < 128; i++)
            {
                int noteInOctave = i % 12;
                bool isBlack = ((BK >> noteInOctave) & 1) != 0;
                if (!isBlack) totalWhiteKeys++;
            }
            for (int note = 0; note < 128; note++)
            {
                float yStart = textureHeight * ((127 - note) / 128f); // Flip so note 0 is at bottom
                float yEnd = textureHeight * ((128 - note) / 128f);
                noteToPixelY[note] = (int)yStart;
                noteHeights[note] = Math.Max(1, (int)(yEnd - yStart)); // Always at least 1 pixel tall
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Initialize(int windowWidth, int windowHeight)
        {
            if (initialized && textureWidth == windowWidth) return;
            
            if (initialized) Cleanup();
            
            textureWidth = windowWidth;
            pixelsPerTick = textureWidth / Window;
            
            InitializeGPUResources();
            initialized = true;
        }

        private static void InitializeGPUResources()
        {
            // Create streaming render texture
            streamingTexture = Raylib.LoadRenderTexture(textureWidth, textureHeight);
            tempTexture = Raylib.LoadRenderTexture(textureWidth, textureHeight);
            
            // Create 1x1 white pixel for drawing
            Raylib_cs.Image whiteImage = Raylib.GenImageColor(1, 1, Raylib_cs.Color.White);
            whitePixel = Raylib.LoadTextureFromImage(whiteImage);
            Raylib.UnloadImage(whiteImage);
            
            // Initialize pixel buffer for direct texture manipulation
            int bufferSize = textureWidth * textureHeight * 4; // RGBA
            pixelBuffer = new byte[bufferSize];
            bufferHandle = GCHandle.Alloc(pixelBuffer, GCHandleType.Pinned);
            bufferPtr = bufferHandle.AddrOfPinnedObject();
            
            // Clear the streaming texture
            Raylib.BeginTextureMode(streamingTexture);
            Raylib.ClearBackground(Raylib_cs.Color.Black);
            Raylib.EndTextureMode();
            // Clear temp texture too
            Raylib.BeginTextureMode(tempTexture);
            Raylib.ClearBackground(Raylib_cs.Color.Black);
            Raylib.EndTextureMode();

            currentPosition = 0f;
            lastRenderedColumn = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void UpdateStreaming(float tick)
        {
            if (!initialized || !NoteProcessor.IsReady) return;
            
            float targetPosition = tick * pixelsPerTick;
            int targetColumn = (int)targetPosition % textureWidth;
            
            if (currentPosition == 0f && tick == 0f)
            {
                RenderFullTexture(0f);
                currentPosition = 0.01f;  // fake small advance to prevent skipping next frame
                return;
            }

            // Calculate how many columns we need to render
            int columnsToRender = CalculateColumnsToRender(targetPosition, targetColumn);
            if (columnsToRender <= 0) return;
            
            // Scroll the texture if needed
            if (columnsToRender >= textureWidth)
            {
                // Full refresh needed
                RenderFullTexture(tick);
            }
            else
            {
                // Incremental update
                ScrollAndRenderColumns(tick, columnsToRender, targetColumn);
            }
            
            currentPosition = targetPosition;
            lastRenderedColumn = targetColumn;
            RenderedColumns = columnsToRender;
        }

        private static int CalculateColumnsToRender(float targetPosition, int targetColumn)
        {
            float deltaPosition = targetPosition - currentPosition;
            if (Math.Abs(deltaPosition) < 0.5f) return 0;
            
            // If we've moved too far, do a full refresh
            if (Math.Abs(deltaPosition) >= textureWidth) return textureWidth;
            
            return Math.Max(1, (int)Math.Abs(deltaPosition));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ScrollAndRenderColumns(float tick, int columnsToRender, int targetColumn)
        {
            // Copy current texture to temp
            Raylib.BeginTextureMode(tempTexture);
            Raylib.ClearBackground(Raylib_cs.Color.Black);
            
            // Draw shifted texture
            Raylib_cs.Rectangle sourceRect = new(columnsToRender, 0, textureWidth - columnsToRender, -textureHeight);
            Raylib_cs.Rectangle destRect = new(0, 0, textureWidth - columnsToRender, textureHeight);
            Texture2D tex = streamingTexture.Texture;
            Raylib.DrawTexturePro(tex, sourceRect, destRect, Vector2.Zero, 0f, Raylib_cs.Color.White);
            
            Raylib.EndTextureMode();
            
            // Swap textures
            (streamingTexture, tempTexture) = (tempTexture, streamingTexture);
            
            // Render new columns
            Raylib.BeginTextureMode(streamingTexture);
            
            float startTick = tick + (Window * 0.5f) - (columnsToRender / pixelsPerTick);
            RenderColumns(textureWidth - columnsToRender, columnsToRender, startTick);
            
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
        private static void RenderColumns(int startX, int columnCount, float startTick)
        {
            if (!NoteProcessor.IsReady) return;
            
            var notes = NoteProcessor.AllNotes;
            float ticksPerPixel = Window / textureWidth;
            float endTick = startTick + (columnCount * ticksPerPixel);
            
            // Find relevant notes using binary search
            int startIdx = FindNotesInRange(notes, startTick - 100f, endTick + 100f);
            
            // Render notes as rectangles directly to the texture
            for (int i = startIdx; i < notes.Length; i++)
            {
                var note = notes[i];
                if (note.startTime > endTick) break;
                if (note.endTime < startTick) continue;
                
                // Calculate pixel coordinates
                float noteStartPixel = (note.startTime - startTick) / ticksPerPixel;
                float noteEndPixel = (note.endTime - startTick) / ticksPerPixel;
                
                int x1 = Math.Max(0, (int)noteStartPixel);
                int x2 = Math.Min(columnCount, (int)Math.Ceiling(noteEndPixel));
                
                if (x2 <= x1) continue;
                
                // Get note color
                bool isGlowing = EnableGlow;
                Raylib_cs.Color noteColor = GetNoteColor(note.color, isGlowing);
                
                // Draw the note rectangle
                int noteY = noteToPixelY[note.NoteNumber];
                int noteHeight = noteHeights[note.NoteNumber];
                
                Raylib.DrawRectangle(
                    startX + x1, 
                    noteY, 
                    x2 - x1, 
                    noteHeight, 
                    noteColor
                );
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindNotesInRange(NoteProcessor.OptimizedEnhancedNote[] notes, float startTick, float endTick)
        {
            if (notes.Length == 0) return 0;
            
            int left = 0, right = notes.Length - 1, result = 0;
            
            // Binary search for first note that might be visible
            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                if (notes[mid].endTime >= startTick)
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
        private static Raylib_cs.Color GetNoteColor(uint baseColor, bool isGlowing)
        {
            uint cacheKey = baseColor | (isGlowing ? 0x80000000u : 0u);
            
            if (colorCache.TryGetValue(cacheKey, out Raylib_cs.Color cached))
                return cached;
            
            byte r = (byte)((baseColor >> 16) & 0xFF);
            byte g = (byte)((baseColor >> 8) & 0xFF);
            byte b = (byte)(baseColor & 0xFF);
            
            if (isGlowing)
            {
                r = (byte)Math.Min(255, (int)(r * 1.8f));
                g = (byte)Math.Min(255, (int)(g * 1.8f));
                b = (byte)Math.Min(255, (int)(b * 1.8f));
            }
            
            Raylib_cs.Color result = new(r, g, b, (byte)255);
            
            if (colorCache.Count < 256)
                colorCache[cacheKey] = result;
            
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Render(int screenWidth, int screenHeight, int padding)
        {
            if (!initialized) return;

            Raylib_cs.Rectangle sourceRect = new(0, 0, textureWidth, -textureHeight);
            Raylib_cs.Rectangle destRect = new(0, padding, screenWidth, screenHeight - (padding * 2));

            Raylib.DrawTexturePro(
                streamingTexture.Texture,
                sourceRect,
                destRect,
                Vector2.Zero,
                0f,
                Raylib_cs.Color.White
            );
        }

        public static void SetWindow(float newWindow)
        {
            if (Math.Abs(Window - newWindow) > 0.1f)
            {
                Window = newWindow;
                pixelsPerTick = textureWidth / Window;
                
                // Force full refresh on next update
                currentPosition = float.MinValue;
            }
        }

        private static void Cleanup()
        {
            if (!initialized) return;
            
            Raylib.UnloadRenderTexture(streamingTexture);
            Raylib.UnloadRenderTexture(tempTexture);
            Raylib.UnloadTexture(whitePixel);
            
            if (bufferHandle.IsAllocated)
                bufferHandle.Free();
            
            colorCache.Clear();
        }

        public static void Shutdown()
        {
            Cleanup();
            initialized = false;
        }
    }
}