using Raylib_cs;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteRenderer
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct MergeNote
        {
            public float x1, x2;
            public uint color;
            public byte height;
            public byte noteLayer;
            public bool glowing;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PackedRect
        {
            public Raylib_cs.Rectangle rect;
            public Raylib_cs.Color color;
        }

        private const int RECT_CAPACITY = 16777216;
        private static PackedRect[] packedRects = new PackedRect[RECT_CAPACITY];

        private static readonly MergeNote[] mergeNotePool = new MergeNote[128];
        private static readonly bool[] validPool = new bool[128];

        private static float cachedYScale = 0f;
        private static float cachedWindow = 0f;
        private static float cachedScreenWidth = 0f;
        private static float cachedScreenHeight = 0f;
        private static int cachedPadding = 0;

        private static readonly Dictionary<uint, uint> glowColorCache = new(256);

        public static int LastFrameRectCount { get; private set; } = 0;
        public static bool EnableGlow { get; set; } = true;
        public static float Window { get; set; } = 2000f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Initialize(int screenHeight, int padding)
        {
            cachedScreenHeight = screenHeight;
            cachedPadding = padding;
            cachedYScale = (screenHeight - padding * 2) / 128f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static int FindStartIndex(float visibleStart, float windowBuffer)
        {
            if (NoteProcessor.allNotes.Length == 0) return 0;

            int lo = 0, hi = NoteProcessor.allNotes.Length - 1, result = 0;

            while (lo <= hi)
            {
                int m = lo + ((hi - lo) >> 1);
                ref var note = ref NoteProcessor.allNotes[m];

                // Use a more conservative buffer for the binary search
                if (note.endTime >= visibleStart - windowBuffer * 2f)
                {
                    result = m;
                    hi = m - 1;
                }
                else
                {
                    lo = m + 1;
                }
            }

            // Conservative backtrack to ensure we don't miss any visible notes
            while (result > 0)
            {
                ref var prevNote = ref NoteProcessor.allNotes[result - 1];
                if (prevNote.endTime < visibleStart - windowBuffer * 2f) break;
                result--;
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int BuildVisibleRectangles(float tick, int screenWidth, int screenHeight, int padding)
        {
            // Cache dimension updates - avoid repeated calculations
            if ((screenWidth != (int)cachedScreenWidth) | (screenHeight != (int)cachedScreenHeight) | (padding != cachedPadding))
            {
                cachedScreenWidth = screenWidth;
                cachedScreenHeight = screenHeight;
                cachedPadding = padding;
                cachedYScale = (screenHeight - (padding << 1)) * 0.0078125f; // 1/128 as constant
            }

            // Early exit without lock if possible
            var allNotes = NoteProcessor.AllNotes;
            if (allNotes.Length == 0) return 0;

            // Pre-calculate all constants once
            float windowHalf = Window * 0.5f;
            float start = tick - windowHalf;
            float end = tick + windowHalf;
            float scale = screenWidth / Window;
            float offset = -start * scale;
            float startMinusBuffer = start - Window * 0.05f;
            float screenWidthF = screenWidth;
            float yBase = screenHeight - padding;
            bool enableGlow = EnableGlow;
            int noteCount = allNotes.Length;
            int capacityLimit = RECT_CAPACITY - 128;

            // Simplified clearing - only what we actually use
            unsafe
            {
                fixed (bool* validPtr = validPool)
                {
                    // Clear in 64-byte chunks for better cache efficiency
                    ulong* validUlong = (ulong*)validPtr;
                    for (int i = 0; i < 16; i++) validUlong[i] = 0;      // 128 bools = 16 ulongs
                }
            }

            int startIdx = FindStartIndex(start, Window * 0.05f);
            int count = 0;

            // Main loop with aggressive inlining and minimal branching
            for (int i = startIdx; i < noteCount && count < capacityLimit; i++)
            {
                ref var n = ref allNotes[i];
                if (n.startTime > end) break;
                if (n.endTime < startMinusBuffer) continue;
            
                float x1 = n.startTime * scale + offset;
                float x2 = n.endTime * scale + offset;
            
                if (x2 <= 0f || x1 >= screenWidthF) continue;
            
                x1 = MathF.Max(0f, x1);
                x2 = MathF.Min(screenWidthF, x2);
                float width = x2 - x1;
            
                int ny = n.noteNumber;
                byte layer = n.noteLayer;
            
                uint baseColor = n.color;
                bool glowing = enableGlow && (tick >= n.startTime && tick <= n.endTime);
                if (glowing) baseColor = GetGlowColorCached(baseColor);
            
                ref var slot = ref mergeNotePool[ny];
            
                if (!validPool[ny])
                {
                    slot = new MergeNote { x1 = x1, x2 = x2, color = baseColor, height = n.height, glowing = glowing, noteLayer = layer };
                    validPool[ny] = true;
                    continue;
                }
            
                // Compare once
                bool sameLayer = (layer == slot.noteLayer);
                bool sameGlow = (glowing == slot.glowing);
                bool closeEnough = (x1 - slot.x2 <= 2f);
            
                if (sameLayer & sameGlow & closeEnough)
                {
                    if (x2 > slot.x2) slot.x2 = x2;
                    continue;
                }
            
                // Compute overlap
                float ovStart = MathF.Max(x1, slot.x1);
                float ovEnd = MathF.Min(x2, slot.x2);
                bool hasOverlap = ovStart < ovEnd;
            
                if (hasOverlap)
                {
                    bool currentCovers = x1 <= slot.x1 && x2 >= slot.x2;
                    bool existingCovers = slot.x1 <= x1 && slot.x2 >= x2;
            
                    if (slot.noteLayer > layer && existingCovers)
                        continue;
            
                    if (layer > slot.noteLayer && currentCovers)
                    {
                        slot = new MergeNote { x1 = x1, x2 = x2, color = baseColor, height = n.height, glowing = glowing, noteLayer = layer };
                        continue;
                    }
                }
            
                EmitRect(slot, ny, yBase, count++);
                slot = new MergeNote { x1 = x1, x2 = x2, color = baseColor, height = n.height, glowing = glowing, noteLayer = layer };
            }

            // Final emission with unrolled loop for better cache usage
            for (int ny = 0; (ny < 128) & (count < RECT_CAPACITY); ny++)
            {
                if (validPool[ny])
                {
                    ref var r = ref mergeNotePool[ny];
                    EmitRect(r, ny, yBase, count);
                    count++;
                }
            }

            LastFrameRectCount = count;
            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitRect(in MergeNote r, int ny, float yBase, int index)
        {
            float noteY = yBase - (ny + 0.5f) * cachedYScale;
            float rectY = noteY - r.height * 0.5f;
            float width = r.x2 - r.x1;

            packedRects[index].rect = new Raylib_cs.Rectangle(r.x1, rectY, width, r.height);
            uint c = r.color;
            packedRects[index].color = new Raylib_cs.Color(
                (byte)(c >> 16),
                (byte)(c >> 8),
                (byte)c,
                (byte)255
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GetGlowColorCached(uint baseColor)
        {
            if (glowColorCache.TryGetValue(baseColor, out uint cached))
                return cached;

            uint glowColor = CalculateGlowColorFast(baseColor);

            if (glowColorCache.Count < 512)
                glowColorCache[baseColor] = glowColor;

            return glowColor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint CalculateGlowColorFast(uint baseColor)
        {
            uint r = Math.Min(255u, (baseColor >> 16) & 0xFF) << 2;
            uint g = Math.Min(255u, (baseColor >> 8) & 0xFF) << 2;
            uint b = Math.Min(255u, baseColor & 0xFF) << 2;

            r = (r > 255u) ? 255u : r;
            g = (g > 255u) ? 255u : g;
            b = (b > 255u) ? 255u : b;

            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void DrawRectangles(int count)
        {
            if (count == 0) return;

            const int BATCH_SIZE = 1024;
            int fullBatches = count / BATCH_SIZE;
            int remainder = count % BATCH_SIZE;

            fixed (PackedRect* rectPtr = packedRects)
            {
                for (int b = 0; b < fullBatches; b++)
                {
                    EmitRectBatch(rectPtr + b * BATCH_SIZE, BATCH_SIZE);
                }

                if (remainder > 0)
                {
                    EmitRectBatch(rectPtr + fullBatches * BATCH_SIZE, remainder);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitRectBatch(PackedRect* batch, int length)
        {
            for (int i = 0; i < length; i++)
            {
                Raylib.DrawRectangleRec(batch[i].rect, batch[i].color);
            }
        }

        public static void ClearGlowCache()
        {
            glowColorCache.Clear();
        }

        public static void Shutdown()
        {
            packedRects = null;
            glowColorCache.Clear();
        }
    }
}