using System;
using System.Runtime.CompilerServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        // Optimized packed note format: 64 bits stored as ulong
        // bits 0..10   -> relativeStart (11 bits, 0-2047)
        // bits 11..23  -> duration (13 bits, 0-8191)  
        // bits 24..30  -> noteNumber (7 bits, 0-127)
        // bits 31..38  -> colorIndex (8 bits, 0-255)
        // bits 39..54  -> trackIndex (16 bits, 0-65535)
        // bits 55..63  -> unused

        public static ulong[][] SortedBuckets = Array.Empty<ulong[]>();
        public static int[] BucketCounts = Array.Empty<int>();
        public static int BucketSize => Math.Clamp((int)MIDIPlayer.ppq, 96, 2048);
        public static bool IsReady { get; private set; } = false;
        public static long TotalNoteCount { get; private set; } = 0;

        private const int MAX_DURATION = 8191; // 13-bit limit

        // Simplified active note tracking
        private struct ActiveNote { public float StartTick; public ushort TrackIndex; public byte Channel; }
        private static readonly bool[] activeFlags = new bool[2048];

        // Color table - RGB only, no alpha
        public static readonly uint[] trackColors = new uint[256];

        // Single array pool for memory efficiency
        private static readonly Queue<ulong[]> noteArrayPool = new Queue<ulong[]>(32);

        static NoteProcessor()
        {
            // Initialize colors with golden ratio distribution (RGB only)
            const float goldenRatio = 1.618034f;
            for (int i = 0; i < 256; i++)
            {
                float h = (i * goldenRatio * 360f / 16f) % 360f;
                float hue60 = h / 60f;
                float c = 0.72f;
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

                uint finalR = Math.Min((uint)((r + 0.18f) * 255f), 255u);
                uint finalG = Math.Min((uint)((g + 0.18f) * 255f), 255u);
                uint finalB = Math.Min((uint)((b + 0.18f) * 255f), 255u);

                trackColors[i] = (finalR << 16) | (finalG << 8) | finalB;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void EnhanceTracksForRendering()
        {
            IsReady = false;
            ClearAllData();

            var tracks = MIDIPlayer.tracks;
            if (tracks?.Length == 0)
            {
                IsReady = true;
                return;
            }

            // Calculate bucket count
            float globalMaxTick = 0;
            for (int t = 0; t < tracks.Length; t++)
                if (tracks[t]?.maxTick > globalMaxTick) 
                    globalMaxTick = tracks[t].maxTick;

            int bucketCount = Math.Max(2, (int)(globalMaxTick / BucketSize) + 2);
            
            // Initialize buckets
            SortedBuckets = new ulong[bucketCount][];
            BucketCounts = new int[bucketCount];

            // Process tracks in parallel
            System.Threading.Tasks.Parallel.For(0, tracks.Length, trackIndex =>
            {
                var track = tracks[trackIndex];
                if (track?.synthEvents?.Count > 0)
                    ProcessTrack(track, trackIndex, bucketCount);
            });

            // Count total notes and sort buckets
            long totalNotes = 0;
            System.Threading.Tasks.Parallel.For(0, bucketCount, bucketIdx =>
            {
                SortBucket(bucketIdx);
                totalNotes += BucketCounts[bucketIdx];
            });

            TotalNoteCount = totalNotes;
            IsReady = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ProcessTrack(MIDITrack track, int trackIndex, int bucketCount)
        {
            var localActiveNotes = new ActiveNote[2048];
            var localActiveFlags = new bool[2048];
            var events = track.synthEvents;
            float currentTick = 0f;

            for (int i = 0; i < events.Count; i++)
            {
                var synthEvent = events[i];
                currentTick = synthEvent.pos;

                int eventValue = synthEvent.val;
                int status = eventValue & 0xF0;
                int channel = eventValue & 0x0F;
                int noteNumber = (eventValue >> 8) & 0x7F;
                int velocity = (eventValue >> 16) & 0x7F;

                if (noteNumber > 127) continue;

                int key = (channel << 7) | noteNumber;

                if (status == 0x90 && velocity > 0) // Note ON
                {
                    localActiveFlags[key] = true;
                    localActiveNotes[key] = new ActiveNote 
                    { 
                        StartTick = currentTick, 
                        TrackIndex = (ushort)trackIndex,
                        Channel = (byte)channel
                    };
                }
                else if (status == 0x80 || (status == 0x90 && velocity == 0)) // Note OFF
                {
                    if (localActiveFlags[key])
                    {
                        ProcessNoteOff(localActiveNotes[key], currentTick, noteNumber, trackIndex, bucketCount, channel);
                        localActiveFlags[key] = false;
                    }
                }
            }

            // Handle remaining notes
            float fallbackEnd = currentTick + 100f;
            for (int slot = 0; slot < 2048; slot++)
            {
                if (localActiveFlags[slot])
                {
                    int noteNumber = slot & 0x7F;
                    int channel = slot >> 7;
                    ProcessNoteOff(localActiveNotes[slot], fallbackEnd, noteNumber, trackIndex, bucketCount, channel);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ProcessNoteOff(ActiveNote activeNote, float endTick, int noteNumber, int trackIndex, int bucketCount, int channel)
        {
            int noteStart = (int)activeNote.StartTick;
            int fullDuration = Math.Max(1, (int)(endTick - activeNote.StartTick));
            byte colorIndex = (byte)((channel + (trackIndex << 1)) & 0xFF);

            SliceNoteWithMaxDuration(noteStart, fullDuration, noteNumber, colorIndex, (ushort)trackIndex, bucketCount);
        }

        private static void SliceNoteWithMaxDuration(int noteStart, int duration, int noteNumber, byte colorIndex, ushort trackIndex, int bucketCount)
        {
            int remaining = duration;
            int currentStart = noteStart;

            while (remaining > 0)
            {
                int chunkDuration = Math.Min(remaining, MAX_DURATION);
                SliceNoteChunk(currentStart, chunkDuration, noteNumber, colorIndex, trackIndex, bucketCount);
                currentStart += chunkDuration;
                remaining -= chunkDuration;
            }
        }

        private static void SliceNoteChunk(int noteStart, int duration, int noteNumber, byte colorIndex, ushort trackIndex, int bucketCount)
        {
            int targetBucket = noteStart / BucketSize;
            if (targetBucket >= bucketCount) targetBucket = bucketCount - 1;

            if (noteStart + duration <= (targetBucket + 1) * BucketSize)
            {
                int relStart = noteStart - (targetBucket * BucketSize);
                ulong packed = Pack64(relStart, duration, noteNumber, colorIndex, trackIndex);
                AddToBucket(targetBucket, packed);
            }
            else
            {
                int remaining = duration;
                int writeStart = noteStart;

                while (remaining > 0)
                {
                    int currentBucket = Math.Min(writeStart / BucketSize, bucketCount - 1);
                    int bucketStartTick = currentBucket * BucketSize;
                    int relStartInBucket = Math.Min(writeStart - bucketStartTick, BucketSize - 1);
                    int available = BucketSize - relStartInBucket;
                    int slice = Math.Min(remaining, available);

                    ulong packed = Pack64(relStartInBucket, slice, noteNumber, colorIndex, trackIndex);
                    AddToBucket(currentBucket, packed);

                    writeStart += slice;
                    remaining -= slice;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddToBucket(int bucket, ulong packed)
        {
            lock (SortedBuckets)
            {
                if (SortedBuckets[bucket] == null)
                {
                    SortedBuckets[bucket] = RentArray(512);
                    BucketCounts[bucket] = 0;
                }
                
                if (BucketCounts[bucket] >= SortedBuckets[bucket].Length)
                {
                    int newSize = SortedBuckets[bucket].Length * 2;
                    var newArray = RentArray(newSize);
                    Array.Copy(SortedBuckets[bucket], newArray, BucketCounts[bucket]);
                    ReturnArray(SortedBuckets[bucket]);
                    SortedBuckets[bucket] = newArray;
                }
                
                SortedBuckets[bucket][BucketCounts[bucket]++] = packed;
            }
        }

        private static void SortBucket(int b)
        {
            int cnt = BucketCounts[b];
            if (cnt <= 1 || SortedBuckets[b] == null) return;

            Array.Sort(SortedBuckets[b], 0, cnt, Comparer<ulong>.Create((u1, u2) =>
            {
                // Sort by relative start position
                int r1 = (int)(u1 & 0x7FFul);
                int r2 = (int)(u2 & 0x7FFul);
                if (r1 != r2) return r1 - r2;

                // Then by duration (shorter notes last = on top)
                int d1 = (int)((u1 >> 11) & 0x1FFFul);
                int d2 = (int)((u2 >> 11) & 0x1FFFul);
                if (d1 != d2) return d2 - d1;

                // Then by trackIndex (higher track numbers render last = on top)
                int t1 = (int)((u1 >> 39) & 0xFFFFul);
                int t2 = (int)((u2 >> 39) & 0xFFFFul);
                return t1 - t2;
            }));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Pack64(int relStart, int duration, int noteNumber, byte colorIndex, ushort trackIndex)
        {
            return ((ulong)relStart & 0x7FFul) |
                   (((ulong)duration & 0x1FFFul) << 11) |
                   (((ulong)noteNumber & 0x7Ful) << 24) |
                   (((ulong)colorIndex & 0xFFul) << 31) |
                   (((ulong)trackIndex & 0xFFFFul) << 39);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackNote(ulong packed, out int relStart, out int duration, out int noteNumber, out int colorIndex, out int trackIndex)
        {
            relStart = (int)(packed & 0x7FFul);
            duration = (int)((packed >> 11) & 0x1FFFul);
            noteNumber = (int)((packed >> 24) & 0x7Ful);
            colorIndex = (int)((packed >> 31) & 0xFFul);
            trackIndex = (int)((packed >> 39) & 0xFFFFul);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] RentArray(int minSize)
        {
            if (noteArrayPool.Count > 0)
            {
                var array = noteArrayPool.Dequeue();
                if (array.Length >= minSize) return array;
            }
            return new ulong[Math.Max(minSize, 512)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnArray(ulong[] array)
        {
            if (noteArrayPool.Count < 32) noteArrayPool.Enqueue(array);
        }

        private static void ClearAllData()
        {
            for (int i = 0; i < SortedBuckets.Length; i++)
            {
                if (SortedBuckets[i] != null)
                    ReturnArray(SortedBuckets[i]);
            }

            SortedBuckets = Array.Empty<ulong[]>();
            BucketCounts = Array.Empty<int>();
            TotalNoteCount = 0;
            Array.Clear(activeFlags, 0, activeFlags.Length);
        }

        public static void Cleanup()
        {
            IsReady = false;
            ClearAllData();
            noteArrayPool.Clear();
        }
    }
}