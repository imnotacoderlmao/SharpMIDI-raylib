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
        public const int BucketSize = 512;  // Fits in 9 bits
        public const int MaxChunkDuration = 255;  // 8-bit duration limit
        public static bool IsReady { get; private set; } = false;
        public static long TotalNoteCount { get; private set; } = 0;

        // Bit masks and shifts for fast unpacking
        private const uint RELSTART_MASK = 0x1FFu;        // 9 bits
        private const int NOTENUMBER_SHIFT = 9;
        private const uint NOTENUMBER_MASK = 0x7Fu;       // 7 bits
        private const int DURATION_SHIFT = 16;
        private const uint DURATION_MASK = 0xFFu;         // 8 bits
        private const int COLORINDEX_SHIFT = 24;

        // Optimized active note tracking using array-based stack
        private struct ActiveNoteStack
        {
            private int[] startTicks;
            private int count;
            
            public void Init()
            {
                startTicks = new int[8]; // Pre-allocate for typical polyphony
                count = 0;
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Push(int startTick)
            {
                if (count >= startTicks.Length)
                    Array.Resize(ref startTicks, startTicks.Length * 2);
                startTicks[count++] = startTick;
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Pop()
            {
                return count > 0 ? startTicks[--count] : -1;
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

            var allEvents = MIDI.synthEvents;
            int eventCount = allEvents.Length;
            
            if (allEvents == null || eventCount == 0)
            {
                IsReady = true;
                return;
            }

            int globalMaxTick = MIDILoader.maxTick;
            int bucketCount = Math.Max(2, (globalMaxTick / BucketSize) + 2);

            // Pre-allocate bucket lists with better initial capacity
            var tempBuckets = new List<uint>[bucketCount];
            int estimatedNotesPerBucket = Math.Max(100, eventCount / (bucketCount * 4));

            // Process all events
            ProcessEvents(allEvents, eventCount, tempBuckets, estimatedNotesPerBucket);

            // Convert to arrays and sort in parallel
            SortedBuckets = new uint[bucketCount][];
            long totalNotes = 0;

            System.Threading.Tasks.Parallel.For(0, bucketCount, bucketIdx =>
            {
                if (tempBuckets[bucketIdx] != null)
                {
                    var bucket = tempBuckets[bucketIdx];
                    SortedBuckets[bucketIdx] = bucket.ToArray();
                    SortBucket(bucketIdx);
                    System.Threading.Interlocked.Add(ref totalNotes, SortedBuckets[bucketIdx].Length);
                }
            });

            TotalNoteCount = totalNotes;
            IsReady = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ProcessEvents(SynthEvent[] events, int eventCount, List<uint>[] buckets, int estimatedCapacity)
        {
            // Pre-allocate stacks for all possible keys
            var activeNoteStacks = new ActiveNoteStack[2048];
            for (int i = 0; i < 2048; i++)
                activeNoteStacks[i].Init();

            var hasActiveNotes = new bool[2048]; // Track which keys have active notes

            for (int i = 0; i < eventCount; i++)
            {
                var synthEvent = events[i];
                int currentTick = synthEvent.pos;

                int eventValue = synthEvent.val;
                int status = eventValue & 0xF0;
                int channel = eventValue & 0x0F;
                int noteNumber = (eventValue >> 8) & 0x7F;
                int velocity = (eventValue >> 16) & 0x7F;

                if (noteNumber > 127) continue;

                int key = (channel << 7) | noteNumber;

                if (status == 0x90 && velocity > 0) // Note ON
                {
                    activeNoteStacks[key].Push(currentTick);
                    hasActiveNotes[key] = true;
                }
                else if (status == 0x80 || (status == 0x90 && velocity == 0)) // Note OFF
                {
                    int startTick = activeNoteStacks[key].Pop();
                    if (startTick >= 0)
                    {
                        int duration = Math.Max(1, currentTick - startTick);
                        SliceNoteIntoBuckets(startTick, duration, noteNumber, channel, buckets, estimatedCapacity);
                        hasActiveNotes[key] = activeNoteStacks[key].HasNotes;
                    }
                }
            }

            // Handle remaining notes
            int fallbackEnd = eventCount > 0 ? events[eventCount - 1].pos + 100 : 100;
            for (int key = 0; key < 2048; key++)
            {
                if (hasActiveNotes[key])
                {
                    int noteNumber = key & 0x7F;
                    int channel = key >> 7;
                    
                    while (activeNoteStacks[key].HasNotes)
                    {
                        int startTick = activeNoteStacks[key].Pop();
                        if (startTick >= 0)
                        {
                            int duration = Math.Max(1, fallbackEnd - startTick);
                            SliceNoteIntoBuckets(startTick, duration, noteNumber, channel, buckets, estimatedCapacity);
                        }
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SliceNoteIntoBuckets(int noteStart, int duration, int noteNumber, 
            int channel, List<uint>[] buckets, int estimatedCapacity)
        {
            int remaining = duration;
            int currentStart = noteStart;
            int colorIndex = channel;

            while (remaining > 0)
            {
                int bucketIdx = currentStart / BucketSize;
                if (bucketIdx >= buckets.Length) bucketIdx = buckets.Length - 1;

                int bucketStartTick = bucketIdx * BucketSize;
                int relStart = currentStart - bucketStartTick;
                int available = BucketSize - relStart;
                int chunkDuration = Math.Min(remaining, Math.Min(available, MaxChunkDuration));

                if (relStart > 511) relStart = 511;

                uint packed = PackNote(relStart, chunkDuration, noteNumber, colorIndex);
                
                // Inline AddToBucket for better performance
                if (buckets[bucketIdx] == null)
                {
                    lock (buckets)
                    {
                        if (buckets[bucketIdx] == null)
                            buckets[bucketIdx] = new List<uint>(estimatedCapacity);
                    }
                }
                
                buckets[bucketIdx].Add(packed);

                currentStart += chunkDuration;
                remaining -= chunkDuration;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SortBucket(int b)
        {
            var bucket = SortedBuckets[b];
            if (bucket == null || bucket.Length <= 1) return;

            // Sort by relative start (lower 9 bits), then by color for cache coherency
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
            GC.Collect();
        }
    }
}