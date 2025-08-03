using Raylib_cs;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteRenderer
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct MergeNote
        {
            public float noteStart, noteEnd;
            public uint color;
            public byte height;
            public byte noteLayer;
            public bool glowing;
            public float occludedStart, occludedEnd; // Track which portion is occluded
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

                if (note.endTime >= visibleStart - windowBuffer)
                {
                    result = m;
                    hi = m - 1;
                }
                else
                {
                    lo = m + 1;
                }
            }

            while (result > 0)
            {
                ref var prevNote = ref NoteProcessor.allNotes[result - 1];
                if (prevNote.endTime < visibleStart) break;
                result--;
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int BuildVisibleRectangles(float tick, int screenWidth, int screenHeight, int padding)
        {
            bool dimensionsChanged = cachedScreenWidth != screenWidth ||
                                     cachedScreenHeight != screenHeight ||
                                     cachedPadding != padding;

            if (dimensionsChanged)
            {
                cachedScreenWidth = screenWidth;
                cachedScreenHeight = screenHeight;
                cachedPadding = padding;
                cachedYScale = (screenHeight - (padding << 1)) * (1f / 128f);
            }

            lock (NoteProcessor.ReadyLock)
            {
                if (!NoteProcessor.IsReady || NoteProcessor.AllNotes.Length == 0) return 0;
            }

            float windowHalf = Window * 0.5f;
            float start = tick - windowHalf;
            float end = tick + windowHalf;
            float scale = screenWidth / Window;

            if (MathF.Abs(cachedWindow - Window) > 1f)
            {
                cachedWindow = Window;
            }

            float windowBuffer = Window * 0.05f;
            int startIdx = FindStartIndex(start, windowBuffer);

            unsafe
            {
                fixed (MergeNote* poolPtr = mergeNotePool)
                {
                    Unsafe.InitBlockUnaligned(poolPtr, 0, (uint)(sizeof(MergeNote) << 7));
                }
                fixed (bool* validPtr = validPool)
                {
                    Unsafe.InitBlockUnaligned(validPtr, 0, 128);
                }
            }

            int count = 0;
            float yBase = screenHeight - padding;
            float screenWidthF = screenWidth;

            var allNotes = NoteProcessor.AllNotes;
            int noteCount = allNotes.Length;

            bool enableGlow = EnableGlow;
            float startMinusBuffer = start - windowBuffer;
            int capacityMinus128 = RECT_CAPACITY - 128;
            float offset = -start * scale;

            // Improved culling: handle partial occlusion properly
            for (int i = startIdx; i < noteCount && count < capacityMinus128; i++)
            {
                ref var n = ref allNotes[i];

                // Early termination: if this note starts beyond our visible end, 
                // all subsequent notes will also be beyond (assuming sorted by start time)
                if (n.startTime > end) break;
                
                // Skip notes that end before our visible start (with buffer)
                if (n.endTime < startMinusBuffer) continue;

                float noteStart = n.startTime * scale + offset;
                float noteEnd = n.endTime * scale + offset;

                // Improved screen bounds culling - check if note has any visible portion
                bool hasVisiblePortion = !(noteEnd <= 0f || noteStart >= screenWidthF);
                if (!hasVisiblePortion) continue;

                // Clamp to screen bounds
                noteStart = MathF.Max(0f, noteStart);
                noteEnd = MathF.Min(screenWidthF, noteEnd);

                uint baseColor = n.color;
                bool isGlowing = false;
                uint finalColor = baseColor;

                if (enableGlow)
                {
                    isGlowing = (tick >= n.startTime) & (tick <= n.endTime);
                    finalColor = isGlowing ? GetGlowColorCached(baseColor) : baseColor;
                }
                
                int ny = n.noteNumber;

                ref var valid = ref validPool[ny];
                if (valid)
                {
                    ref var existing = ref mergeNotePool[ny];
                    
                    // Check for layer-based occlusion
                    bool currentNoteHigherLayer = n.noteLayer > existing.noteLayer;
                    bool existingNoteHigherLayer = existing.noteLayer > n.noteLayer;
                    bool sameLayer = n.noteLayer == existing.noteLayer;
                    bool notesOverlap = !(noteEnd <= existing.noteStart || noteStart >= existing.noteEnd);
                    
                    if (notesOverlap)
                    {
                        if (currentNoteHigherLayer)
                        {
                            // Current note has higher layer, update occlusion of existing note
                            float overlapStart = MathF.Max(existing.noteStart, noteStart);
                            float overlapEnd = MathF.Min(existing.noteEnd, noteEnd);
                            
                            // Check if the existing note is completely occluded
                            if (overlapStart <= existing.noteStart && overlapEnd >= existing.noteEnd)
                            {
                                // Completely occluded - mark for skipping
                                existing.occludedStart = existing.noteStart;
                                existing.occludedEnd = existing.noteEnd;
                            }
                            else
                            {
                                // Partially occluded - track the occluded region
                                existing.occludedStart = overlapStart;
                                existing.occludedEnd = overlapEnd;
                            }
                        }
                        else if (existingNoteHigherLayer)
                        {
                            // Existing note has higher layer, check if current note is completely covered
                            float overlapStart = MathF.Max(noteStart, existing.noteStart);
                            float overlapEnd = MathF.Min(noteEnd, existing.noteEnd);
                            
                            if (overlapStart <= noteStart && overlapEnd >= noteEnd)
                            {
                                // Current note is completely covered, skip it
                                continue;
                            }
                            // If only partially covered, we'll still process it (partial visibility)
                        }
                        else if (sameLayer)
                        {
                            // Same layer - try to merge if conditions are met
                            bool sameGlow = isGlowing == existing.glowing;
                            bool canMerge = noteStart - existing.noteEnd <= 2f;
                            
                            if (canMerge & sameGlow)
                            {
                                // Extend existing note if this one goes further
                                if (noteEnd > existing.noteEnd) existing.noteEnd = noteEnd;
                                continue;
                            }
                        }
                    }
                    
                    // Emit existing note with proper occlusion handling
                    EmitNoteWithOcclusion(in existing, ny, yBase, ref count, capacityMinus128);
                    if (count >= capacityMinus128) break;
                }

                // Create new merge note
                ref var newMerge = ref mergeNotePool[ny];
                newMerge.noteStart = noteStart;
                newMerge.noteEnd = noteEnd;
                newMerge.color = finalColor;
                newMerge.height = n.height;
                newMerge.glowing = isGlowing;
                newMerge.noteLayer = n.noteLayer;
                newMerge.occludedStart = float.MaxValue; // No occlusion initially
                newMerge.occludedEnd = float.MinValue;
                valid = true;
            }

            // Emit remaining notes in the pool with occlusion handling
            for (int ny = 0; ny < 128 && count < RECT_CAPACITY; ny++)
            {
                if (validPool[ny])
                {
                    EmitNoteWithOcclusion(in mergeNotePool[ny], ny, yBase, ref count, RECT_CAPACITY);
                }
            }

            LastFrameRectCount = count;
            return count;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitNoteWithOcclusion(in MergeNote note, int ny, float yBase, ref int count, int maxCount)
        {
            // Check if note has any visible portions
            bool hasOcclusion = note.occludedStart < note.occludedEnd;
            
            if (!hasOcclusion)
            {
                // No occlusion, emit the full note
                EmitRectOptimized(in note, ny, yBase, count++);
                return;
            }
            
            // Check if completely occluded
            if (note.occludedStart <= note.noteStart && note.occludedEnd >= note.noteEnd)
            {
                // Completely occluded, don't emit anything
                return;
            }
            
            // Partially occluded - emit visible portions
            if (note.noteStart < note.occludedStart && count < maxCount)
            {
                // Left portion is visible
                var leftPortion = note;
                leftPortion.noteEnd = note.occludedStart;
                EmitRectOptimized(in leftPortion, ny, yBase, count++);
            }
            
            if (note.occludedEnd < note.noteEnd && count < maxCount)
            {
                // Right portion is visible
                var rightPortion = note;
                rightPortion.noteStart = note.occludedEnd;
                EmitRectOptimized(in rightPortion, ny, yBase, count++);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitRectOptimized(in MergeNote r, int ny, float yBase, int index)
        {
            float noteY = yBase - (ny + 0.5f) * cachedYScale;
            float rectY = noteY - r.height * 0.5f;
            float width = r.noteEnd - r.noteStart;

            packedRects[index].rect = new Raylib_cs.Rectangle(r.noteStart, rectY, width, r.height);
            uint c = r.color;
            packedRects[index].color = new Raylib_cs.Color(
                (byte)(c >> 16),
                (byte)(c >> 8),
                (byte)c,
                (byte)255
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitRectBatch(PackedRect* batch, int length)
        {
            // Optimized batch rendering using pointer arithmetic
            PackedRect* end = batch + length;
            for (PackedRect* current = batch; current < end; current++)
            {
                Raylib.DrawRectangleRec(current->rect, current->color);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void DrawRectangles(int count)
        {
            if (count == 0) return;
            
            // Larger batch size for better performance
            const int BATCH_SIZE = 2048;
            
            fixed (PackedRect* rectPtr = packedRects)
            {
                PackedRect* currentPtr = rectPtr;
                int remaining = count;
                
                // Process full batches
                while (remaining >= BATCH_SIZE)
                {
                    EmitRectBatch(currentPtr, BATCH_SIZE);
                    currentPtr += BATCH_SIZE;
                    remaining -= BATCH_SIZE;
                }
                
                // Process remainder
                if (remaining > 0)
                {
                    EmitRectBatch(currentPtr, remaining);
                }
            }
        }

        public static void Shutdown()
        {
            packedRects = null;
            glowColorCache.Clear();
        }
    }
}