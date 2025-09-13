using System;
using System.Runtime.CompilerServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        // Compact packed note format: 32 bits stored as uint
        // bits 0..10   -> relativeStart (11 bits, 0-2047)
        // bits 11..23  -> duration (13 bits, 0-8191)  
        // bits 24..30  -> noteNumber (7 bits, 0-127)
        // bit 31       -> unused (1 bit)

        public static List<uint>[] SortedBuckets = Array.Empty<List<uint>>();
        public static List<byte>[] NoteColors = Array.Empty<List<byte>>();
        public static int BucketSize = Math.Clamp((int)MIDIPlayer.ppq, 96, 2048);
        public static bool IsReady { get; private set; } = false;
        public static long TotalNoteCount { get; private set; } = 0;

        private const int MAX_DURATION = 8191; // 13-bit limit

        // Simplified active note tracking
        private struct ActiveNote { public float StartTick; public ushort TrackIndex; public byte Channel; }
        private static readonly bool[] activeFlags = new bool[2048];

        // Color table - RGB only, no alpha (back to 256 colors)
        public static readonly uint[] trackColors = new uint[256];

        static NoteProcessor()
        {
            // Initialize colors with golden ratio distribution (RGB only) - same as original
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
            int globalMaxTick = 0;
            for (int t = 0; t < tracks.Length; t++)
                if (tracks[t]?.maxTick > globalMaxTick)
                    globalMaxTick = tracks[t].maxTick;

            int bucketCount = Math.Max(2, (globalMaxTick / BucketSize) + 2);

            // Initialize bucket arrays (Lists created lazily)
            SortedBuckets = new List<uint>[bucketCount];
            NoteColors = new List<byte>[bucketCount];

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

            // Generate color based on channel + track combination (full 8-bit range)
            byte colorIndex = (byte)((channel + (trackIndex << 1)) & 0xFF);

            SliceNoteWithMaxDuration(noteStart, fullDuration, noteNumber, colorIndex, bucketCount);
        }

        private static void SliceNoteWithMaxDuration(int noteStart, int duration, int noteNumber, byte colorIndex, int bucketCount)
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

        private static void SliceNoteChunk(int noteStart, int duration, int noteNumber, byte colorIndex, int bucketCount)
        {
            int targetBucket = noteStart / BucketSize;
            if (targetBucket >= bucketCount) targetBucket = bucketCount - 1;

            if (noteStart + duration <= (targetBucket + 1) * BucketSize)
            {
                int relStart = noteStart - (targetBucket * BucketSize);
                uint packed = Pack32(relStart, duration, noteNumber);
                AddToBucket(targetBucket, packed, colorIndex);
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

                    uint packed = Pack32(relStartInBucket, slice, noteNumber);
                    AddToBucket(currentBucket, packed, colorIndex);

                    writeStart += slice;
                    remaining -= slice;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddToBucket(int bucket, uint packed, byte colorIndex)
        {
            lock (SortedBuckets)
            {
                if (SortedBuckets[bucket] == null)
                {
                    // Estimate initial capacity based on bucket size and typical note density
                    int estimatedCapacity = Math.Max(64, BucketSize / 10);
                    SortedBuckets[bucket] = new List<uint>(estimatedCapacity);
                    NoteColors[bucket] = new List<byte>(estimatedCapacity);
                }

                SortedBuckets[bucket].Add(packed);
                NoteColors[bucket].Add(colorIndex);
            }
        }

        private static void SortBucket(int b)
        {
            var bucket = SortedBuckets[b];
            var colors = NoteColors[b];
            if (bucket == null || bucket.Count <= 1) return;

            // Create paired array for sorting
            var paired = new (uint note, byte color)[bucket.Count];
            for (int i = 0; i < bucket.Count; i++)
            {
                paired[i] = (bucket[i], colors[i]);
            }

            Array.Sort(paired, (p1, p2) =>
            {
                uint u1 = p1.note, u2 = p2.note;

                // Sort by relative start position
                int r1 = (int)(u1 & 0x7FFu);
                int r2 = (int)(u2 & 0x7FFu);
                if (r1 != r2) return r1 - r2;

                // Then by duration (shorter notes last = on top)
                int d1 = (int)((u1 >> 11) & 0x1FFFu);
                int d2 = (int)((u2 >> 11) & 0x1FFFu);
                if (d1 != d2) return d2 - d1;

                // Since we don't have track info, use color as tiebreaker
                return p1.color.CompareTo(p2.color);
            });

            // Copy back sorted data
            for (int i = 0; i < paired.Length; i++)
            {
                bucket[i] = paired[i].note;
                colors[i] = paired[i].color;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Pack32(int relStart, int duration, int noteNumber)
        {
            return ((uint)relStart & 0x7FFu) |
                   (((uint)duration & 0x1FFFu) << 11) |
                   (((uint)noteNumber & 0x7Fu) << 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackNote(uint packed, byte colorIndex, out int relStart, out int duration, out int noteNumber, out int color)
        {
            relStart = (int)(packed & 0x7FFu);
            duration = (int)((packed >> 11) & 0x1FFFu);
            noteNumber = (int)((packed >> 24) & 0x7Fu);
            color = colorIndex;
        }

        private static void ClearAllData()
        {
            // Lists will be garbage collected automatically
            SortedBuckets = Array.Empty<List<uint>>();
            NoteColors = Array.Empty<List<byte>>();
            TotalNoteCount = 0;
            Array.Clear(activeFlags, 0, activeFlags.Length);
        }

        public static void Cleanup()
        {
            IsReady = false;
            ClearAllData();
        }
    }
}