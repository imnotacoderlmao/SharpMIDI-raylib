using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        // Bit layout (MSB first, total = 32 bits):
        // bits 21-31 = RelativeStart  (11 bits, 0..2047)
        // bits 11-20 = Duration       (10 bits, 0..1023)
        // bits  7-10 = ColorIndex     ( 4 bits, 0..15)
        // bits  0-6  = NoteNumber     ( 7 bits, 0..127)
        
        public static BigArray<uint> FlatNotes = null;
        public static int[] BucketOffsets = Array.Empty<int>();
        public static int[] BucketLengths = Array.Empty<int>();
        public static int BucketCount = 0;

        public static readonly uint[] trackColors = new uint[16];
        private static List<uint>[]? sharedBuckets;   // temp build structure, freed after finalize
        private static readonly object bucketsLock = new object();
        private static int currentBucketCount = 0;

        public const int BucketSize = 2047;
        public const int MaxChunkDuration = 1023;
        public static bool IsReady { get; private set; } = false;
        public static long TotalNoteCount { get; private set; } = 0;

        private const int RELSTART_SHIFT = 21;
        private const uint RELSTART_MASK = 0x7FFu;
        private const int DURATION_SHIFT = 11;
        private const uint DURATION_MASK = 0x3FFu;
        private const int COLORINDEX_SHIFT = 7;
        private const uint COLORINDEX_MASK = 0xFu;
        private const uint NOTENUMBER_MASK = 0x7Fu;

        private struct NoteStack
        {
            private int tick0, tick1, tick2, tick3;
            private int count;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Push(int tick)
            {
                if (count == 0) tick0 = tick;
                else if (count == 1) tick1 = tick;
                else if (count == 2) tick2 = tick;
                else tick3 = tick;
                if (count < 4) count++;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Pop()
            {
                if (count == 0) return -1;
                count--;
                return count == 0 ? tick0 : count == 1 ? tick1 : count == 2 ? tick2 : tick3;
            }

            public bool HasNotes => count > 0;
        }

        static NoteProcessor()
        {
            for (int i = 0; i < 16; i++)
            {
                float h = (i * 137.50776f) % 360f;
                float hue60 = h / 60f;
                float c = 0.75f;
                float x = c * (1f - MathF.Abs(hue60 % 2f - 1f));
                float r, g, b;
                switch ((int)hue60 % 6)
                {
                    case 0: r = c; g = x; b = 0; break;
                    case 1: r = x; g = c; b = 0; break;
                    case 2: r = 0; g = c; b = x; break;
                    case 3: r = 0; g = x; b = c; break;
                    case 4: r = x; g = 0; b = c; break;
                    default: r = c; g = 0; b = x; break;
                }
                trackColors[i] = ((uint)((r + 0.15f) * 255) << 16) |
                                 ((uint)((g + 0.15f) * 255) << 8)  |
                                  (uint)((b + 0.15f) * 255);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint PackNote(int relStart, int duration, int noteNumber, int colorIndex)
        {
            return (((uint)relStart   & RELSTART_MASK) << RELSTART_SHIFT)  |
                   (((uint)duration   & DURATION_MASK) << DURATION_SHIFT)  |
                   (((uint)colorIndex & COLORINDEX_MASK) << COLORINDEX_SHIFT) |
                   ((uint)noteNumber  & NOTENUMBER_MASK);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackNote(uint packed, out int relStart, out int duration, out int noteNumber, out int colorIndex)
        {
            relStart   = (int)((packed >> RELSTART_SHIFT) & RELSTART_MASK);
            duration   = (int)((packed >> DURATION_SHIFT) & DURATION_MASK);
            colorIndex = (int)((packed >> COLORINDEX_SHIFT) & COLORINDEX_MASK);
            noteNumber = (int)( packed & NOTENUMBER_MASK);
        }

        // Any packed uint >= RelStartThreshold(n) has relStart >= n.
        // Used by the renderer for binary search lower-bound within a bucket span.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint RelStartThreshold(int minRelStart) =>
            (uint)minRelStart << RELSTART_SHIFT;

        private static void EnsureBucketCapacity(int requiredBuckets)
        {
            lock (bucketsLock)
            {
                if (sharedBuckets == null)
                {
                    sharedBuckets = new List<uint>[requiredBuckets];
                    currentBucketCount = requiredBuckets;
                }
                else if (requiredBuckets > currentBucketCount)
                {
                    Array.Resize(ref sharedBuckets, requiredBuckets);
                    currentBucketCount = requiredBuckets;
                }
            }
        }

        // FIX 1: accepts TickGroup[] (the full array) instead of ref TickGroup (a single entry).
        //         The old signature meant ticks.offset never changed — every tick iteration
        //         read the same end-of-events value, so nothing was ever processed.
        public static void ProcessEventsForRendering(TickGroup[] tickGroups, BigArray<uint24> eventPtr, BigArray<ushort> trackPtr, long eventCount, int maxTick)
        {
            int bucketsNeeded = (maxTick / BucketSize) + 1;
            int bucketCount   = (int)(eventPtr.Length / BucketSize) + 1;
            int estimatedNotesPerBucket = (int)((eventPtr.Length / 2) / bucketCount) + 16;
            EnsureBucketCapacity(bucketsNeeded);

            NoteStack[] stacks   = new NoteStack[2048];
            bool[]      hasNotes = new bool[2048];

            int bucketSize  = BucketSize;
            int maxDuration = MaxChunkDuration;
            int bucketsLen  = currentBucketCount;

            // FIX 2: work directly through the native pointer — BigArray<T> exposes no indexer,
            //         so eventPtr[mes] / trackPtr[mes] would not compile.
            uint24* evPtr  = eventPtr.Pointer;
            ushort* trPtr  = trackPtr.Pointer;   // null when trackcolors is off
            bool useTrack  = trPtr != null;

            // FIX 3: lastoffset is now updated from tickGroups[tick+1].offset at the bottom
            //         of each tick iteration, not from the stale single-group ref.
            long lastoffset = tickGroups[0].offset;

            for (int tick = 0; tick < maxTick && tick + 1 < tickGroups.Length; tick++)
            {
                long tickEnd = tickGroups[tick + 1].offset;   // exclusive end for this tick

                for (long mes = lastoffset; mes < tickEnd; mes++)
                {
                    int val      = evPtr[mes].Value;
                    int status   = val & 0xF0;
                    int channel  = val & 0x0F;
                    int note     = (val >> 8)  & 0x7F;
                    int velocity = (val >> 16) & 0x7F;

                    if (note > 127) continue;

                    // colorIndex: upper bits = track (hashed), lower bits = channel.
                    // Matches the pattern from the NoteStack loop below.
                    int trackColorBase = useTrack ? (trPtr[mes] * 17) & 0xF0 : 0;
                    int colorIndex     = (trackColorBase | channel) & 0xFF;
                    int key            = (channel << 7) | note;

                    if (status == 0x90 && velocity > 0)
                    {
                        stacks[key].Push(tick);
                        hasNotes[key] = true;
                    }
                    else if (status == 0x80 || (status == 0x90 && velocity == 0))
                    {
                        int startTick = stacks[key].Pop();
                        if (startTick >= 0)
                        {
                            int duration = tick - startTick;
                            if (duration < 1) duration = 1;
                            SliceNote(startTick, duration, note, colorIndex, sharedBuckets,
                                      estimatedNotesPerBucket, bucketSize, maxDuration, bucketsLen);
                            hasNotes[key] = stacks[key].HasNotes;
                        }
                    }
                }

                // FIX 3 (cont): advance the offset cursor to this tick's end.
                lastoffset = tickEnd;
            }

            // Flush notes that never received a NoteOff — treat maxTick as their end.
            // FIX 4: `fallbackEnd` was undefined. Use maxTick.
            // FIX 5: `colorIndex = ( + channel)` was a broken expression. Reconstruct
            //         it the same way as the main loop: hash the channel into [0,15].
            for (int key = 0; key < 2048; key++)
            {
                if (!hasNotes[key]) continue;
                int note    = key & 0x7F;
                int channel = key >> 7;
                int colorIndex = channel & 0xFF;   // no track info available here; channel only

                while (stacks[key].HasNotes)
                {
                    int startTick = stacks[key].Pop();
                    if (startTick >= 0)
                    {
                        int duration = maxTick - startTick;   // FIX 4
                        if (duration < 1) duration = 1;
                        SliceNote(startTick, duration, note, colorIndex, sharedBuckets,
                                  estimatedNotesPerBucket, bucketSize, maxDuration, bucketsLen);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SliceNote(int noteStart, int duration, int noteNum, int colorIdx,
                                      List<uint>[] buckets, int capacity, int bucketSize, int maxDur, int bucketsLen)
        {
            int remaining = duration;
            int currentStart = noteStart;

            while (remaining > 0)
            {
                int bucketIdx = currentStart / bucketSize;
                if (bucketIdx >= bucketsLen) bucketIdx = bucketsLen - 1;

                int bucketStartTick = bucketIdx * bucketSize;
                int relStart = Math.Min(currentStart - bucketStartTick, 2047);
                int chunkDur = Math.Min(Math.Min(remaining, bucketSize - relStart), maxDur);
                uint packed = PackNote(relStart, chunkDur, noteNum, colorIdx);

                List<uint>? bucket = buckets[bucketIdx];
                if (bucket == null)
                {
                    lock (bucketsLock)
                    {
                        bucket = buckets[bucketIdx];
                        if (bucket == null)
                        {
                            bucket = new List<uint>(capacity);
                            buckets[bucketIdx] = bucket;
                        }
                    }
                }
                lock (bucket) bucket.Add(packed);

                currentStart += chunkDur;
                remaining -= chunkDur;
            }
        }

        public static void FinalizeBuckets()
        {
            if (sharedBuckets == null) return;

            int bucketCount = currentBucketCount;

            // Sort each bucket in parallel.
            // Because relStart is in the high bits, sorting raw uint = sorting by relStart —
            // no comparer lambda. CollectionsMarshal.AsSpan avoids a ToArray() copy before sort.
            Parallel.For(0, bucketCount, i =>
            {
                var b = sharedBuckets[i];
                if (b != null && b.Count > 1)
                    MemoryExtensions.Sort(CollectionsMarshal.AsSpan(b));
            });

            // One pass to compute per-bucket offsets and total note count.
            int[] offsets = new int[bucketCount];
            int[] lengths = new int[bucketCount];
            long  total   = 0;
            for (int i = 0; i < bucketCount; i++)
            {
                int len = sharedBuckets[i]?.Count ?? 0;
                offsets[i] = (int)total;
                lengths[i] = len;
                total += len;
            }

            // Single native allocation via BigArray — no GC pressure, no per-bucket object headers.
            var flatArray = new BigArray<uint>(total);
            uint* flat = flatArray.Pointer;

            // Copy each sorted bucket into the flat block.
            for (int i = 0; i < bucketCount; i++)
            {
                var b = sharedBuckets[i];
                if (b == null || b.Count == 0) continue;
                CollectionsMarshal.AsSpan(b).CopyTo(new Span<uint>(flat + offsets[i], lengths[i]));
            }

            FlatNotes = flatArray;
            BucketOffsets = offsets;
            BucketLengths = lengths;
            BucketCount = bucketCount;
            TotalNoteCount = total;

            sharedBuckets = null;
            currentBucketCount = 0;
            IsReady = true;
        }

        public static void Cleanup()
        {
            IsReady = false;
            FlatNotes.Dispose();
            FlatNotes = null;
            BucketOffsets = Array.Empty<int>();
            BucketLengths = Array.Empty<int>();
            BucketCount = 0;
            sharedBuckets = null;
            currentBucketCount = 0;
            TotalNoteCount = 0;
        }
    }
}