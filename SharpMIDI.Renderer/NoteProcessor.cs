using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {        
        // pseudo-struct for sortedbuckets since its bitpacked:
        // bits 0-10 = RelativeStart
        // bits 11-18 = NoteNumber (midi key essentially)
        // bits 18-27 = Duration
        // bits 28-31 = ColorIndex
        public static uint[][] SortedBuckets = Array.Empty<uint[]>();
        public static readonly uint[] trackColors = new uint[16];
        private static List<uint>[]? sharedBuckets;
        private static readonly object bucketsLock = new object();
        private static int currentBucketCount = 0;
        
        public const int BucketSize = 2047;
        public const int MaxChunkDuration = 1023;
        public static bool IsReady { get; private set; } = false;
        public static long TotalNoteCount { get; private set; } = 0;

        private const uint RELSTART_MASK = 0x7FFu;
        private const int NOTENUMBER_SHIFT = 11;
        private const uint NOTENUMBER_MASK = 0x7Fu;
        private const int DURATION_SHIFT = 18;
        private const uint DURATION_MASK = 0x3FFu;
        private const int COLORINDEX_SHIFT = 28;

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

        public static void ProcessTrackForRendering(SynthEvent* trackEvents, long trackEventCount, int trackIndex, int trackMaxTick)
        {
            if (trackEvents == null || trackEventCount == 0) return;
    
            int bucketsNeeded = (trackMaxTick / BucketSize) + 1;
            int bucketCount = ((int)trackEventCount / BucketSize) + 1;
            int thisTrackNotes = (int)(trackEventCount / 2);
            int estimatedNotesPerBucket = (thisTrackNotes / bucketCount) + 16;
            EnsureBucketCapacity(bucketsNeeded);
    
            NoteStack[] stacks = new NoteStack[2048];
            bool[] hasNotes = new bool[2048];
    
            int bucketSize = BucketSize;
            int maxDuration = MaxChunkDuration;
            int bucketsLen = currentBucketCount;  // Use current size
            int trackColorBase = (trackIndex * 17) & 0xFF;
    
            for (long i = 0; i < trackEventCount; i++)
            {
                SynthEvent evt = trackEvents[i];
                int tick = (int)evt.tick;
                int val = (int)evt.message;
                
                int status = val & 0xF0;
                int channel = val & 0x0F;
                int note = (val >> 8) & 0x7F;
                int velocity = (val >> 16) & 0x7F;
    
                if (note > 127) continue;
    
                int key = (channel << 7) | note;
    
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
                        
                        int colorIndex = (trackColorBase + channel) & 0xFF;
                        
                        SliceNote(startTick, duration, note, colorIndex, sharedBuckets, 
                                 estimatedNotesPerBucket, bucketSize, maxDuration, bucketsLen);
                        hasNotes[key] = stacks[key].HasNotes;
                    }
                }
            }
    
            // Handle remaining notes
            int fallbackEnd = trackMaxTick + 100;
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

            int bucketCount = currentBucketCount;
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
            currentBucketCount = 0;
            IsReady = true;
        }

        public static void Cleanup()
        {
            IsReady = false;
            SortedBuckets = Array.Empty<uint[]>();
            sharedBuckets = null;
            currentBucketCount = 0;
            TotalNoteCount = 0;
        }
    }
}