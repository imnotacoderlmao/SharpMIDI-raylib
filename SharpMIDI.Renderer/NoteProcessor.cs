using System;
using System.Runtime.CompilerServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        // Compact packed note format: 32 bits stored as uint
        // bits 0..10   -> relativeStart (11 bits, 0-2047)
        // bits 11..19  -> duration (9 bits, 0-511) - REDUCED by 1 bit
        // bits 20..26  -> noteNumber (7 bits, 0-127)
        // bits 27..31  -> color (5 bits, 0-31) - INCREASED by 1 bit

        public static List<uint>[] SortedBuckets = Array.Empty<List<uint>>();
        public static int BucketSize = Math.Clamp((int)MIDIPlayer.ppq, 96, 2048);
        public static bool IsReady { get; private set; } = false;
        public static long TotalNoteCount { get; private set; } = 0;

        private const int MAX_DURATION = 511; // 9-bit limit (reduced from 1023)

        // Simplified active note tracking
        private struct ActiveNote { public float StartTick; public ushort TrackIndex; public byte Channel; }

        // Random color assignment
        private static readonly Random colorRng = new Random();
        private static readonly Dictionary<int, int> trackChannelColors = new Dictionary<int, int>();

        // Color table - RGB only, no alpha (32 colors for 5-bit color index)
        public static readonly uint[] trackColors = new uint[32];

        static NoteProcessor()
        {
            // Initialize colors with golden ratio distribution (RGB only) - expanded to 32 colors
            const float goldenRatio = 1.618034f;
            for (int i = 0; i < 32; i++)
            {
                float h = (i * 137.50776f) % 360f;
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
            int globalMaxTick = 0;
            for (int t = 0; t < tracks.Length; t++)
                if (tracks[t]?.maxTick > globalMaxTick)
                    globalMaxTick = tracks[t].maxTick;

            int bucketCount = Math.Max(2, (globalMaxTick / BucketSize) + 2);

            // Initialize bucket arrays (Lists created lazily)
            SortedBuckets = new List<uint>[bucketCount];

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
                if (SortedBuckets[bucketIdx] != null)
                    totalNotes += SortedBuckets[bucketIdx].Count;
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

            // Generate random color for track+channel combination
            int trackChannelKey = (trackIndex << 8) | channel; // Combine track and channel
            if (!trackChannelColors.TryGetValue(trackChannelKey, out int colorIndex))
            {
                lock (trackChannelColors)
                {
                    if (!trackChannelColors.TryGetValue(trackChannelKey, out colorIndex))
                    {
                        colorIndex = colorRng.Next(32); // Random color 0-31 (increased from 16)
                        trackChannelColors[trackChannelKey] = colorIndex;
                    }
                }
            }

            SliceNoteWithMaxDuration(noteStart, fullDuration, noteNumber, colorIndex, bucketCount);
        }

        private static void SliceNoteWithMaxDuration(int noteStart, int duration, int noteNumber, int colorIndex, int bucketCount)
        {
            int remaining = duration;
            int currentStart = noteStart;

            while (remaining > 0)
            {
                int chunkDuration = Math.Min(remaining, MAX_DURATION);
                SliceNoteChunk(currentStart, chunkDuration, noteNumber, colorIndex, bucketCount);
                currentStart += chunkDuration;
                remaining -= chunkDuration;
            }
        }

        private static void SliceNoteChunk(int noteStart, int duration, int noteNumber, int colorIndex, int bucketCount)
        {
            int targetBucket = noteStart / BucketSize;
            if (targetBucket >= bucketCount) targetBucket = bucketCount - 1;

            if (noteStart + duration <= (targetBucket + 1) * BucketSize)
            {
                int relStart = noteStart - (targetBucket * BucketSize);
                uint packed = Pack32(relStart, duration, noteNumber, colorIndex);
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

                    uint packed = Pack32(relStartInBucket, slice, noteNumber, colorIndex);
                    AddToBucket(currentBucket, packed);

                    writeStart += slice;
                    remaining -= slice;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddToBucket(int bucket, uint packed)
        {
            lock (SortedBuckets)
            {
                if (SortedBuckets[bucket] == null)
                {
                    // Estimate initial capacity based on bucket size and typical note density
                    int estimatedCapacity = Math.Max(64, BucketSize / 10);
                    SortedBuckets[bucket] = new List<uint>(estimatedCapacity);
                }

                SortedBuckets[bucket].Add(packed);
            }
        }

        private static void SortBucket(int b)
        {
            var bucket = SortedBuckets[b];
            if (bucket == null || bucket.Count <= 1) return;

            bucket.Sort((u1, u2) =>
            {
                // Sort by relative start position
                int r1 = (int)(u1 & 0x7FFu);
                int r2 = (int)(u2 & 0x7FFu);
                if (r1 != r2) return r1 - r2;
                
                // Then by duration (shorter notes last = on top)
                /*int d1 = (int)((u1 >> 11) & 0x1FFu); // 9-bit mask (updated)
                int d2 = (int)((u2 >> 11) & 0x1FFu); // 9-bit mask (updated)
                if (d1 != d2) return d2 - d1;*/
                
                // Then by color as tiebreaker
                int c1 = (int)((u1 >> 28) & 0xFu);
                int c2 = (int)((u2 >> 28) & 0xFu);
                return c1.CompareTo(c2);
            });
            
            bucket.TrimExcess();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Pack32(int relStart, int duration, int noteNumber, int colorIndex)
        {
            return ((uint)relStart & 0x7FFu) |              // 11 bits: 0-10
                   (((uint)duration & 0x1FFu) << 11) |      // 9 bits: 11-19 (reduced from 10)
                   (((uint)noteNumber & 0x7Fu) << 20) |     // 7 bits: 20-26 (shifted from 21)
                   (((uint)colorIndex & 0x1Fu) << 27);      // 5 bits: 27-31 (increased from 4)
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackNote(uint packed, out int relStart, out int duration, out int noteNumber, out int colorIndex)
        {
            relStart = (int)(packed & 0x7FFu);
            duration = (int)((packed >> 11) & 0x1FFu);      // 9-bit mask (updated)
            noteNumber = (int)((packed >> 20) & 0x7Fu);     // Shifted from 21 to 20
            colorIndex = (int)((packed >> 27) & 0x1Fu);     // 5-bit color (updated)
        }

        private static void ClearAllData()
        {
            // Lists will be garbage collected automatically
            SortedBuckets = Array.Empty<List<uint>>();
            TotalNoteCount = 0;
            trackChannelColors.Clear(); // Clear color assignments for new file
        }

        public static void Cleanup()
        {
            IsReady = false;
            ClearAllData();
        }
    }
}