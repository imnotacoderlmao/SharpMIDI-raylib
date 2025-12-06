using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    // Ultra-compact 4-byte note structure packed into a single uint
    // Bit layout (optimized for unpacking speed):
    // bits 0-8:   RelativeStart (9 bits, 0-511)
    // bits 9-15:  NoteNumber (7 bits, 0-127)
    // bits 16-23: Duration (8 bits, 0-255)
    // bits 24-31: ColorIndex (8 bits, 0-255)
    
    public static unsafe class NoteProcessor
    {
        public static uint[][] SortedBuckets = Array.Empty<uint[]>();
        public const int BucketSize = 512;
        public const int MaxChunkDuration = 255;
        public static bool IsReady { get; private set; } = false;
        public static long TotalNoteCount { get; private set; } = 0;

        // Bit masks and shifts
        private const uint RELSTART_MASK = 0x1FFu;
        private const int NOTENUMBER_SHIFT = 9;
        private const uint NOTENUMBER_MASK = 0x7Fu;
        private const int DURATION_SHIFT = 16;
        private const uint DURATION_MASK = 0xFFu;
        private const int COLORINDEX_SHIFT = 24;

        // Minimal stack implementation - no resizing during normal operation
        private struct NoteStack
        {
            private int tick0, tick1, tick2, tick3; // Handle up to 4 overlapping notes (covers 99% of cases)
            private int count;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Push(int tick)
            {
                // Unrolled for speed
                if (count == 0) tick0 = tick;
                else if (count == 1) tick1 = tick;
                else if (count == 2) tick2 = tick;
                else if (count == 3) tick3 = tick;
                // Beyond 4, just overwrite (rare edge case)
                else tick3 = tick;
                
                if (count < 4) count++;
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Pop()
            {
                if (count == 0) return -1;
                count--;
                // Unrolled for speed
                if (count == 0) return tick0;
                if (count == 1) return tick1;
                if (count == 2) return tick2;
                return tick3;
            }
            
            public bool HasNotes => count > 0;
        }

        public static readonly uint[] trackColors = new uint[256];

        static NoteProcessor()
        {
            // Generate 256 distinct colors using golden ratio
            for (int i = 0; i < 256; i++)
            {
                float h = (i * 137.50776f) % 360f;
                float hue60 = h / 60f;
                float c = 0.75f;
                float x = c * (1f - MathF.Abs(hue60 % 2f - 1f));

                float r, g, b;
                int sector = (int)hue60 % 6;
                switch (sector)
                {
                    case 0: r = c; g = x; b = 0; break;
                    case 1: r = x; g = c; b = 0; break;
                    case 2: r = 0; g = c; b = x; break;
                    case 3: r = 0; g = x; b = c; break;
                    case 4: r = x; g = 0; b = c; break;
                    default: r = c; g = 0; b = x; break;
                }

                uint finalR = Math.Min((uint)((r + 0.15f) * 255f), 255u);
                uint finalG = Math.Min((uint)((g + 0.15f) * 255f), 255u);
                uint finalB = Math.Min((uint)((b + 0.15f) * 255f), 255u);

                trackColors[i] = (finalR << 16) | (finalG << 8) | finalB;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint PackNote(int relStart, int duration, int noteNumber, int colorIndex)
        {
            return ((uint)relStart & RELSTART_MASK) |
                   (((uint)noteNumber & NOTENUMBER_MASK) << NOTENUMBER_SHIFT) |
                   (((uint)duration & DURATION_MASK) << DURATION_SHIFT) |
                   (((uint)colorIndex) << COLORINDEX_SHIFT);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackNote(uint packed, out int relStart, out int duration, out int noteNumber, out int colorIndex)
        {
            relStart = (int)(packed & RELSTART_MASK);
            noteNumber = (int)((packed >> NOTENUMBER_SHIFT) & NOTENUMBER_MASK);
            duration = (int)((packed >> DURATION_SHIFT) & DURATION_MASK);
            colorIndex = (int)(packed >> COLORINDEX_SHIFT);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void EnhanceTracksForRendering()
        {
            IsReady = false;
            ClearAllData();

            BigArray<long> allEvents = MIDI.synthEvents;
            ulong eventCount = allEvents?.Length ?? 0;
            
            if (allEvents == null || eventCount == 0)
            {
                IsReady = true;
                return;
            }

            int maxTick = MIDILoader.maxTick;
            int bucketCount = (maxTick / BucketSize) + 2;
            if (bucketCount < 2) bucketCount = 2;

            // Calculate realistic capacity - most events are note on/off pairs
            ulong estimatedNotes = eventCount / 3; // Approximate note pairs + other events
            int notesPerBucket = (int)(estimatedNotes / (ulong)bucketCount) + 16; // Small padding

            List<uint>[] tempBuckets = new List<uint>[bucketCount];

            ProcessEvents(allEvents, eventCount, tempBuckets, notesPerBucket);

            // Convert to arrays
            uint[][] finalBuckets = new uint[bucketCount][];
            long totalNotes = 0;

            for (int i = 0; i < bucketCount; i++)
            {
                List<uint>? bucket = tempBuckets[i];
                if (bucket != null)
                {
                    finalBuckets[i] = bucket.ToArray();
                    totalNotes += finalBuckets[i].Length;
                }
            }

            SortedBuckets = finalBuckets;
            TotalNoteCount = totalNotes;
            
            // Sort in parallel after array conversion
            System.Threading.Tasks.Parallel.For(0, bucketCount, SortBucket);

            IsReady = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ProcessEvents(BigArray<long> events, ulong eventCount, List<uint>[] buckets, int capacity)
        {
            NoteStack[] stacks = new NoteStack[2048];
            bool[] hasNotes = new bool[2048];

            // Cache frequently used values
            int bucketSize = BucketSize;
            int maxDuration = MaxChunkDuration;
            int bucketsLen = buckets.Length;

            // Direct pointer access for maximum speed
            long* eventPtr = events.Pointer;

            for (ulong i = 0; i < eventCount; i++)
            {
                long evt = eventPtr[i];
                int tick = (int)(evt >> 32);
                int val = (int)(evt & 0xFFFFFFFF);
                
                // Cache unpacked values
                int status = val & 0xF0;
                int channel = val & 0x0F;
                int note = (val >> 8) & 0x7F;
                int velocity = (val >> 16) & 0x7F;

                if (note > 127) continue;

                int key = (channel << 7) | note;

                if (status == 0x90 && velocity > 0) // Note ON
                {
                    stacks[key].Push(tick);
                    hasNotes[key] = true;
                }
                else if (status == 0x80 || (status == 0x90 && velocity == 0)) // Note OFF
                {
                    int startTick = stacks[key].Pop();
                    if (startTick >= 0)
                    {
                        int duration = tick - startTick;
                        if (duration < 1) duration = 1;
                        
                        SliceNote(startTick, duration, note, channel, buckets, capacity, bucketSize, maxDuration, bucketsLen);
                        hasNotes[key] = stacks[key].HasNotes;
                    }
                }
            }

            // Handle remaining notes
            int fallbackEnd = eventCount > 0 ? (int)(eventPtr[eventCount - 1] >> 32) + 100 : 100;
            for (int key = 0; key < 2048; key++)
            {
                if (!hasNotes[key]) continue;
                
                int note = key & 0x7F;
                int channel = key >> 7;
                
                while (stacks[key].HasNotes)
                {
                    int startTick = stacks[key].Pop();
                    if (startTick >= 0)
                    {
                        int duration = fallbackEnd - startTick;
                        if (duration < 1) duration = 1;
                        
                        SliceNote(startTick, duration, note, channel, buckets, capacity, bucketSize, maxDuration, bucketsLen);
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
                int relStart = currentStart - bucketStartTick;
                if (relStart > 511) relStart = 511;
                
                int available = bucketSize - relStart;
                int chunkDur = remaining;
                if (chunkDur > available) chunkDur = available;
                if (chunkDur > maxDur) chunkDur = maxDur;

                uint packed = PackNote(relStart, chunkDur, noteNum, colorIdx);
                
                // Double-checked locking for bucket initialization
                List<uint>? bucket = buckets[bucketIdx];
                if (bucket == null)
                {
                    lock (buckets)
                    {
                        bucket = buckets[bucketIdx];
                        if (bucket == null)
                        {
                            bucket = new List<uint>(capacity);
                            buckets[bucketIdx] = bucket;
                        }
                    }
                }
                
                lock (bucket)
                {
                    bucket.Add(packed);
                }

                currentStart += chunkDur;
                remaining -= chunkDur;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SortBucket(int idx)
        {
            uint[]? bucket = SortedBuckets[idx];
            if (bucket == null || bucket.Length <= 1) return;

            // Sort by relative start for temporal coherency
            Array.Sort(bucket, (n1, n2) => (int)(n1 & RELSTART_MASK) - (int)(n2 & RELSTART_MASK));
        }

        private static void ClearAllData()
        {
            SortedBuckets = Array.Empty<uint[]>();
            TotalNoteCount = 0;
        }

        public static void Cleanup()
        {
            IsReady = false;
            ClearAllData();
        }
    }
}