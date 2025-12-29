using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        // Bit layout (optimized for unpacking speed):
        // bits 0-10:  RelativeStart (11 bits, 0-2047)
        // bits 11-18:  NoteNumber (7 bits, 0-127)
        // bits 18-27: Duration (10 bits, 0-1023)
        // bits 28-31: ColorIndex (4 bits, 0-16)
        public static uint[][] SortedBuckets = Array.Empty<uint[]>();
        public const int BucketSize = 2047;
        public const int MaxChunkDuration = 1023; // updated for 10-bit duration
        public static bool IsReady { get; private set; } = false;
        public static long TotalNoteCount { get; private set; } = 0;

        // New bit masks and shifts
        private const uint RELSTART_MASK = 0x7FFu;     // 11 bits
        private const int NOTENUMBER_SHIFT = 11;
        private const uint NOTENUMBER_MASK = 0x7Fu;    // 7 bits
        private const int DURATION_SHIFT = 18;
        private const uint DURATION_MASK = 0x3FFu;     // 10 bits
        private const int COLORINDEX_SHIFT = 28;       // 4 bits

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
                else tick3 = tick; // overwrite if >4
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

        public static readonly uint[] trackColors = new uint[16]; // now only 4-bit index needed

        private static List<uint>[]? sharedBuckets;
        private static readonly object bucketsLock = new object();

        static NoteProcessor()
        {
            // Precompute 16 distinct colors
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
                                 ((uint)((g + 0.15f) * 255) << 8) |
                                 ((uint)((b + 0.15f) * 255));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint PackNote(int relStart, int duration, int noteNumber, int colorIndex)
        {
            return ((uint)relStart & RELSTART_MASK) |
                   (((uint)noteNumber & NOTENUMBER_MASK) << NOTENUMBER_SHIFT) |
                   (((uint)duration & DURATION_MASK) << DURATION_SHIFT) |
                   (((uint)colorIndex & 0xF) << COLORINDEX_SHIFT);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackNote(uint packed, out int relStart, out int duration, out int noteNumber, out int colorIndex)
        {
            relStart = (int)(packed & RELSTART_MASK);
            noteNumber = (int)((packed >> NOTENUMBER_SHIFT) & NOTENUMBER_MASK);
            duration = (int)((packed >> DURATION_SHIFT) & DURATION_MASK);
            colorIndex = (int)((packed >> COLORINDEX_SHIFT) & 0xF);
        }

        public static void InitializeBuckets(int maxTick)
        {
            IsReady = false;
            SortedBuckets = Array.Empty<uint[]>();
            int bucketCount = (maxTick / BucketSize) + 2;
            if (bucketCount < 2) bucketCount = 2;
            sharedBuckets = new List<uint>[bucketCount];
        }

        public static void ProcessTrackForRendering(List<long> trackEvents, int trackIndex, int estimatedNotesPerBucket)
        {
            if (trackEvents == null || trackEvents.Count == 0) return;
            if (sharedBuckets == null) return;

            NoteStack[] stacks = new NoteStack[2048];
            bool[] hasNotes = new bool[2048];

            int bucketSize = BucketSize;
            int maxDuration = MaxChunkDuration;
            int bucketsLen = sharedBuckets.Length;
            int eventCount = trackEvents.Count;

            // Color based on track + channel for more variety
            int trackColorBase = (trackIndex * 17) & 0xFF; // Use track for base color

            for (int i = 0; i < eventCount; i++)
            {
                long evt = trackEvents[i];
                int tick = (int)(evt >> 32);
                int val = (int)(evt & 0xFFFFFFFF);
                
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
                        
                        // Combine track and channel for color variety
                        int colorIndex = (trackColorBase + channel) & 0xFF;
                        
                        SliceNote(startTick, duration, note, colorIndex, sharedBuckets, 
                                 estimatedNotesPerBucket, bucketSize, maxDuration, bucketsLen);
                        hasNotes[key] = stacks[key].HasNotes;
                    }
                }
            }

            // Handle remaining notes
            int fallbackEnd = eventCount > 0 ? (int)(trackEvents[eventCount - 1] >> 32) + 100 : 100;
            for (int key = 0; key < 2048; key++)
            {
                if (!hasNotes[key]) continue;
                
                int note = key & 0x7F;
                int channel = key >> 7;
                int colorIndex = (trackColorBase + channel) & 0xFF;
                
                while (stacks[key].HasNotes)
                {
                    int startTick = stacks[key].Pop();
                    if (startTick >= 0)
                    {
                        int duration = fallbackEnd - startTick;
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

        private static void SortBucket(int idx)
        {
            var bucket = SortedBuckets[idx];
            if (bucket == null || bucket.Length <= 1) return;
            Array.Sort(bucket, (a, b) => ((int)((b >> DURATION_SHIFT) & DURATION_MASK) - (int)((a >> DURATION_SHIFT) & DURATION_MASK)));
        }

        public static void FinalizeBuckets()
        {
            if (sharedBuckets == null) return;

            int bucketCount = sharedBuckets.Length;
            uint[][] finalBuckets = new uint[bucketCount][];
            long totalNotes = 0;

            for (int i = 0; i < bucketCount; i++)
            {
                var bucket = sharedBuckets[i];
                if (bucket != null)
                {
                    finalBuckets[i] = bucket.ToArray();
                    totalNotes += finalBuckets[i].Length;
                }
            }

            SortedBuckets = finalBuckets;
            TotalNoteCount = totalNotes;

            Parallel.For(0, bucketCount, SortBucket);
            sharedBuckets = null;
            IsReady = true;
        }

        public static void Cleanup()
        {
            IsReady = false;
            SortedBuckets = Array.Empty<uint[]>();
            sharedBuckets = null;
            TotalNoteCount = 0;
        }
    }
}
