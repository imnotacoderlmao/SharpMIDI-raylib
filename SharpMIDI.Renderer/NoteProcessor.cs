using System;
using System.Runtime.CompilerServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        // Optimized packed note format: 32 bits stored as uint
        // bits 0..10   -> relativeStart (11 bits, 0-2047)
        // bits 11..23  -> duration (13 bits, 0-8191)
        // bits 24..30  -> noteNumber (7 bits, 0-127)
        // bit 31       -> unused
        // colorIndex and trackIndex stored in separate lookup arrays

        // Stream directly from buckets - no flattening
        public static uint[][] SortedBuckets = Array.Empty<uint[]>();
        public static byte[][] ColorIndices = Array.Empty<byte[]>(); // Separate color storage
        public static ushort[][] TrackIndices = Array.Empty<ushort[]>(); // Separate track storage
        public static int[] BucketCounts = Array.Empty<int>();
        public static int BucketSize => Math.Clamp((int)MIDIPlayer.ppq, 96, 2048);
        public static bool IsReady { get; private set; } = false;
        public static long TotalNoteCount { get; private set; } = 0;

        private const int MAX_DURATION = 8191; // 13-bit limit

        // Simplified active note tracking
        private struct ActiveNote { public float StartTick; public ushort TrackIndex; public byte Channel; }
        private static readonly bool[] activeFlags = new bool[2048];

        // Color table - RGB only, no alpha
        public static readonly uint[] trackColors = new uint[16];

        // Array pool for memory efficiency
        private static readonly Queue<uint[]> noteArrayPool = new Queue<uint[]>();
        private static readonly Queue<byte[]> colorArrayPool = new Queue<byte[]>();
        private static readonly Queue<ushort[]> trackArrayPool = new Queue<ushort[]>();

        static NoteProcessor()
        {
            // Initialize colors with golden ratio distribution (RGB only)
            const float goldenRatio = 1.618034f;
            for (int i = 0; i < 16; i++)
            {
                float h = (i * goldenRatio * 360f) % 360f;
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

                // RGB only - no alpha channel
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
            SortedBuckets = new uint[bucketCount][];
            ColorIndices = new byte[bucketCount][];
            TrackIndices = new ushort[bucketCount][];
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
            });

            for (int i = 0; i < bucketCount; i++)
                totalNotes += BucketCounts[i];

            TotalNoteCount = totalNotes;

            // Trim bucket arrays to actual size to save memory
            System.Threading.Tasks.Parallel.For(0, bucketCount, bucketIdx =>
            {
                if (SortedBuckets[bucketIdx] != null && BucketCounts[bucketIdx] > 0)
                {
                    int count = BucketCounts[bucketIdx];
                    if (SortedBuckets[bucketIdx].Length > count)
                    {
                        var trimmedNotes = new uint[count];
                        var trimmedColors = new byte[count];
                        var trimmedTracks = new ushort[count];
                        
                        Array.Copy(SortedBuckets[bucketIdx], trimmedNotes, count);
                        Array.Copy(ColorIndices[bucketIdx], trimmedColors, count);
                        Array.Copy(TrackIndices[bucketIdx], trimmedTracks, count);
                        
                        // Return old arrays to pool
                        ReturnArrays(SortedBuckets[bucketIdx], ColorIndices[bucketIdx], TrackIndices[bucketIdx]);
                        
                        SortedBuckets[bucketIdx] = trimmedNotes;
                        ColorIndices[bucketIdx] = trimmedColors;
                        TrackIndices[bucketIdx] = trimmedTracks;
                    }
                }
                else if (SortedBuckets[bucketIdx] != null)
                {
                    // Return empty buckets to pool
                    ReturnArrays(SortedBuckets[bucketIdx], ColorIndices[bucketIdx], TrackIndices[bucketIdx]);
                    SortedBuckets[bucketIdx] = null;
                    ColorIndices[bucketIdx] = null;
                    TrackIndices[bucketIdx] = null;
                }
            });

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
            byte colorIndex = CalculateColorIndex(channel, trackIndex);

            // Split note by duration if it exceeds our bit limit
            SliceNoteWithMaxDuration(noteStart, fullDuration, noteNumber, colorIndex, (ushort)trackIndex, bucketCount);
        }

        private static void SliceNoteWithMaxDuration(int noteStart, int duration, int noteNumber, byte colorIndex, ushort trackIndex, int bucketCount)
        {
            int remaining = duration;
            int currentStart = noteStart;

            while (remaining > 0)
            {
                int chunkDuration = Math.Min(remaining, MAX_DURATION);
                
                // Now slice this chunk across buckets if needed
                SliceNoteChunk(currentStart, chunkDuration, noteNumber, colorIndex, trackIndex, bucketCount);
                
                currentStart += chunkDuration;
                remaining -= chunkDuration;
            }
        }

        private static void SliceNoteChunk(int noteStart, int duration, int noteNumber, byte colorIndex, ushort trackIndex, int bucketCount)
        {
            int targetBucket = noteStart / BucketSize;
            if (targetBucket >= bucketCount) targetBucket = bucketCount - 1;

            // Check if note fits in single bucket
            if (noteStart + duration <= (targetBucket + 1) * BucketSize)
            {
                int relStart = noteStart - (targetBucket * BucketSize);
                uint packed = Pack32(relStart, (ushort)duration, (byte)noteNumber);
                AddToBucket(targetBucket, packed, colorIndex, trackIndex);
            }
            else
            {
                // Split across buckets
                int remaining = duration;
                int writeStart = noteStart;

                while (remaining > 0)
                {
                    int currentBucket = Math.Min(writeStart / BucketSize, bucketCount - 1);
                    int bucketStartTick = currentBucket * BucketSize;
                    int relStartInBucket = Math.Min(writeStart - bucketStartTick, BucketSize - 1);
                    int available = BucketSize - relStartInBucket;
                    int slice = Math.Min(remaining, available);

                    uint packed = Pack32(relStartInBucket, (ushort)slice, (byte)noteNumber);
                    AddToBucket(currentBucket, packed, colorIndex, trackIndex);

                    writeStart += slice;
                    remaining -= slice;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddToBucket(int bucket, uint packed, byte colorIndex, ushort trackIndex)
        {
            lock (SortedBuckets) // Single lock for simplicity
            {
                if (SortedBuckets[bucket] == null)
                {
                    SortedBuckets[bucket] = RentNoteArray(512);
                    ColorIndices[bucket] = RentColorArray(512);
                    TrackIndices[bucket] = RentTrackArray(512);
                    BucketCounts[bucket] = 0;
                }
                
                if (BucketCounts[bucket] >= SortedBuckets[bucket].Length)
                {
                    int newSize = SortedBuckets[bucket].Length * 2;
                    var newNotes = RentNoteArray(newSize);
                    var newColors = RentColorArray(newSize);
                    var newTracks = RentTrackArray(newSize);
                    
                    Array.Copy(SortedBuckets[bucket], newNotes, BucketCounts[bucket]);
                    Array.Copy(ColorIndices[bucket], newColors, BucketCounts[bucket]);
                    Array.Copy(TrackIndices[bucket], newTracks, BucketCounts[bucket]);
                    
                    ReturnArrays(SortedBuckets[bucket], ColorIndices[bucket], TrackIndices[bucket]);
                    
                    SortedBuckets[bucket] = newNotes;
                    ColorIndices[bucket] = newColors;
                    TrackIndices[bucket] = newTracks;
                }
                
                int idx = BucketCounts[bucket]++;
                SortedBuckets[bucket][idx] = packed;
                ColorIndices[bucket][idx] = colorIndex;
                TrackIndices[bucket][idx] = trackIndex;
            }
        }

        private static void SortBucket(int b)
        {
            int cnt = BucketCounts[b];
            if (cnt <= 1 || SortedBuckets[b] == null) return;

            // Create indices for sorting
            var indices = new int[cnt];
            for (int i = 0; i < cnt; i++) indices[i] = i;

            Array.Sort(indices, 0, cnt, Comparer<int>.Create((i1, i2) =>
            {
                uint u1 = SortedBuckets[b][i1];
                uint u2 = SortedBuckets[b][i2];
                
                // Sort by relative start position
                int r1 = (int)(u1 & 0x7FFu);
                int r2 = (int)(u2 & 0x7FFu);
                if (r1 != r2) return r1 - r2;

                // .. then by duration (shorter notes last = on top)
                int d1 = (int)((u1 >> 11) & 0x1FFFu);
                int d2 = (int)((u2 >> 11) & 0x1FFFu);
                if (d1 != d2) return d2 - d1;

                // .. then by trackIndex (higher track numbers render last = on top)
                int t1 = TrackIndices[b][i1];
                int t2 = TrackIndices[b][i2];
                return t1 - t2;
            }));

            // Apply sorting to all arrays
            var tempNotes = new uint[cnt];
            var tempColors = new byte[cnt];
            var tempTracks = new ushort[cnt];

            for (int i = 0; i < cnt; i++)
            {
                int srcIdx = indices[i];
                tempNotes[i] = SortedBuckets[b][srcIdx];
                tempColors[i] = ColorIndices[b][srcIdx];
                tempTracks[i] = TrackIndices[b][srcIdx];
            }

            Array.Copy(tempNotes, SortedBuckets[b], cnt);
            Array.Copy(tempColors, ColorIndices[b], cnt);
            Array.Copy(tempTracks, TrackIndices[b], cnt);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Pack32(int relStart, ushort duration, byte note)
        {
            return (uint)(relStart & 0x7FF) |
                   ((uint)(duration & 0x1FFF) << 11) |
                   ((uint)(note & 0x7F) << 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackNote(uint packed, int bucketIdx, int noteIdx, out int relStart, out int duration, out int noteNumber, out int colorIndex, out int trackIndex)
        {
            relStart = (int)(packed & 0x7FF);
            duration = (int)((packed >> 11) & 0x1FFF);
            noteNumber = (int)((packed >> 24) & 0x7F);
            colorIndex = ColorIndices[bucketIdx][noteIdx];
            trackIndex = TrackIndices[bucketIdx][noteIdx];
        }

        // Array pool methods
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint[] RentNoteArray(int minSize)
        {
            if (noteArrayPool.Count > 0)
            {
                var array = noteArrayPool.Dequeue();
                if (array.Length >= minSize) return array;
            }
            return new uint[Math.Max(minSize, 512)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte[] RentColorArray(int minSize)
        {
            if (colorArrayPool.Count > 0)
            {
                var array = colorArrayPool.Dequeue();
                if (array.Length >= minSize) return array;
            }
            return new byte[Math.Max(minSize, 512)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort[] RentTrackArray(int minSize)
        {
            if (trackArrayPool.Count > 0)
            {
                var array = trackArrayPool.Dequeue();
                if (array.Length >= minSize) return array;
            }
            return new ushort[Math.Max(minSize, 512)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnArrays(uint[] notes, byte[] colors, ushort[] tracks)
        {
            if (noteArrayPool.Count < 50) noteArrayPool.Enqueue(notes);
            if (colorArrayPool.Count < 50) colorArrayPool.Enqueue(colors);
            if (trackArrayPool.Count < 50) trackArrayPool.Enqueue(tracks);
        }

        private static void ClearAllData()
        {
            // Return arrays to pools before clearing
            for (int i = 0; i < SortedBuckets.Length; i++)
            {
                if (SortedBuckets[i] != null)
                    ReturnArrays(SortedBuckets[i], ColorIndices[i], TrackIndices[i]);
            }

            SortedBuckets = Array.Empty<uint[]>();
            ColorIndices = Array.Empty<byte[]>();
            TrackIndices = Array.Empty<ushort[]>();
            BucketCounts = Array.Empty<int>();
            TotalNoteCount = 0;
            Array.Clear(activeFlags, 0, activeFlags.Length);
        }

        public static void Cleanup()
        {
            IsReady = false;
            ClearAllData();
            
            // Clear pools
            noteArrayPool.Clear();
            colorArrayPool.Clear();
            trackArrayPool.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte CalculateColorIndex(int channel, int trackIndex)
        {
            return (byte)((channel + (trackIndex << 1)) & 0x0F);
        }
    }
}