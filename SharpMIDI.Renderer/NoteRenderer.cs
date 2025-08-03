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
        private static readonly byte[] layerAtNote = new byte[128]; // Track which layer owns each note slot

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
                fixed (byte* layerPtr = layerAtNote)
                {
                    Unsafe.InitBlockUnaligned(layerPtr, 0, 128);
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

            // Process notes - handle note-level occlusion instead of layer replacement
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
                byte currentLayer = n.noteLayer;

                ref var valid = ref validPool[ny];
                if (valid)
                {
                    ref var existing = ref mergeNotePool[ny];
                    byte existingLayer = layerAtNote[ny];
                    
                    // Check if notes overlap in time
                    bool notesOverlap = !(noteEnd <= existing.noteStart || noteStart >= existing.noteEnd);
                    
                    if (notesOverlap)
                    {
                        if (currentLayer > existingLayer)
                        {
                            // Higher layer note overlaps - check if existing note extends beyond overlap
                            float overlapStart = MathF.Max(existing.noteStart, noteStart);
                            float overlapEnd = MathF.Min(existing.noteEnd, noteEnd);
                            
                            // Emit visible portions of existing note (before overlap)
                            if (existing.noteStart < overlapStart)
                            {
                                var beforePortion = existing;
                                beforePortion.noteEnd = overlapStart;
                                EmitRectOptimized(in beforePortion, ny, yBase, count++);
                                if (count >= capacityMinus128) break;
                            }
                            
                            // Emit visible portion after overlap
                            if (existing.noteEnd > overlapEnd)
                            {
                                var afterPortion = existing;
                                afterPortion.noteStart = overlapEnd;
                                EmitRectOptimized(in afterPortion, ny, yBase, count++);
                                if (count >= capacityMinus128) break;
                            }
                            
                            // Replace with current higher layer note
                            existing.noteStart = noteStart;
                            existing.noteEnd = noteEnd;
                            existing.color = finalColor;
                            existing.height = n.height;
                            existing.glowing = isGlowing;
                            existing.noteLayer = currentLayer;
                            layerAtNote[ny] = currentLayer;
                        }
                        else if (currentLayer == existingLayer)
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
                            else
                            {
                                // Can't merge - emit existing and replace
                                EmitRectOptimized(in existing, ny, yBase, count++);
                                if (count >= capacityMinus128) break;
                                
                                existing.noteStart = noteStart;
                                existing.noteEnd = noteEnd;
                                existing.color = finalColor;
                                existing.height = n.height;
                                existing.glowing = isGlowing;
                            }
                        }
                        else
                        {
                            // Lower layer note overlaps with higher layer - check if current note extends beyond
                            float overlapStart = MathF.Max(noteStart, existing.noteStart);
                            float overlapEnd = MathF.Min(noteEnd, existing.noteEnd);
                            
                            // Only emit non-overlapping portions of current note
                            if (noteStart < overlapStart)
                            {
                                // Emit portion before overlap
                                var beforeNote = new MergeNote
                                {
                                    noteStart = noteStart,
                                    noteEnd = overlapStart,
                                    color = finalColor,
                                    height = n.height,
                                    glowing = isGlowing,
                                    noteLayer = currentLayer
                                };
                                EmitRectOptimized(in beforeNote, ny, yBase, count++);
                                if (count >= capacityMinus128) break;
                            }
                            
                            if (noteEnd > overlapEnd)
                            {
                                // Emit portion after overlap
                                var afterNote = new MergeNote
                                {
                                    noteStart = overlapEnd,
                                    noteEnd = noteEnd,
                                    color = finalColor,
                                    height = n.height,
                                    glowing = isGlowing,
                                    noteLayer = currentLayer
                                };
                                EmitRectOptimized(in afterNote, ny, yBase, count++);
                                if (count >= capacityMinus128) break;
                            }
                            // Don't replace existing note since it has higher priority
                        }
                    }
                    else
                    {
                        // No overlap - emit existing and replace with current
                        EmitRectOptimized(in existing, ny, yBase, count++);
                        if (count >= capacityMinus128) break;
                        
                        existing.noteStart = noteStart;
                        existing.noteEnd = noteEnd;
                        existing.color = finalColor;
                        existing.height = n.height;
                        existing.glowing = isGlowing;
                        existing.noteLayer = currentLayer;
                        layerAtNote[ny] = currentLayer;
                    }
                }
                else
                {
                    // Create new merge note
                    ref var newMerge = ref mergeNotePool[ny];
                    newMerge.noteStart = noteStart;
                    newMerge.noteEnd = noteEnd;
                    newMerge.color = finalColor;
                    newMerge.height = n.height;
                    newMerge.glowing = isGlowing;
                    newMerge.noteLayer = currentLayer;
                    layerAtNote[ny] = currentLayer;
                    valid = true;
                }
            }

            // Emit remaining notes in the pool
            for (int ny = 0; ny < 128 && count < RECT_CAPACITY; ny++)
            {
                if (validPool[ny])
                {
                    EmitRectOptimized(in mergeNotePool[ny], ny, yBase, count++);
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