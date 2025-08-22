using System;
using System.Runtime.CompilerServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        // Packed note format: 64 bits stored as ulong
        // bits 0..10   -> relativeStart (11 bits, 0-2047)
        // bits 11..26  -> duration (16 bits, 0-65535)
        // bits 27..33  -> noteNumber (7 bits, 0-127)
        // bits 34..37  -> colorIndex (4 bits, 0-15)
        // bits 38..53  -> trackIndex (16 bits, 0-65535)

        public static byte[] AllPackedNotes = Array.Empty<byte>();
        public static int[] BucketOffsets = Array.Empty<int>();
        public static readonly int BucketSize = 2048;
        public static bool IsReady { get; private set; } = false;

        // Simplified active note tracking
        private struct ActiveNote { public float StartTick; public ushort TrackIndex; public byte Channel; }
        private static readonly ActiveNote[] activeNotes = new ActiveNote[2048]; // 16 channels * 128 notes
        private static readonly bool[] activeFlags = new bool[2048];

        // Bucket management
        private static ulong[][] bucketBuffers = Array.Empty<ulong[]>();
        private static int[] bucketCounts = Array.Empty<int>();

        // Color table
        public static readonly uint[] trackColors = new uint[16];

        static NoteProcessor()
        {
            // Initialize colors with golden ratio distribution
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

                trackColors[i] = 0xFF000000u | (finalR << 16) | (finalG << 8) | finalB;
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
            bucketBuffers = new ulong[bucketCount][];
            bucketCounts = new int[bucketCount];

            // Process tracks in parallel
            System.Threading.Tasks.Parallel.For(0, tracks.Length, trackIndex =>
            {
                var track = tracks[trackIndex];
                if (track?.synthEvents?.Count > 0)
                    ProcessTrack(track, trackIndex, bucketCount);
            });

            // Count total notes
            long totalNotes = 0;
            for (int i = 0; i < bucketCount; i++) 
                totalNotes += bucketCounts[i];

            if (totalNotes == 0)
            {
                IsReady = true;
                return;
            }

            // Sort and flatten
            System.Threading.Tasks.Parallel.For(0, bucketCount, SortBucket);
            FlattenBuckets(bucketCount, totalNotes);
            BuildBucketOffsets(bucketCount);

            // Cleanup
            bucketBuffers = Array.Empty<ulong[]>();
            bucketCounts = Array.Empty<int>();
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
                currentTick += synthEvent.pos;

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
            int duration = Math.Max(1, Math.Min(65535, (int)(endTick - activeNote.StartTick)));
            byte colorIndex = CalculateColorIndex(channel, trackIndex);

            int targetBucket = noteStart / BucketSize;
            if (targetBucket >= bucketCount) targetBucket = bucketCount - 1;

            // Check if note fits in single bucket
            if (noteStart + duration <= (targetBucket + 1) * BucketSize)
            {
                int relStart = noteStart - (targetBucket * BucketSize);
                ulong packed = Pack(relStart, (ushort)duration, (byte)noteNumber, colorIndex, (ushort)trackIndex);
                AddToBucket(targetBucket, packed);
            }
            else
            {
                // Split across buckets
                SliceNote(noteStart, duration, noteNumber, colorIndex, (ushort)trackIndex, bucketCount);
            }
        }

        private static void SliceNote(int noteStart, int duration, int noteNumber, byte colorIndex, ushort trackIndex, int bucketCount)
        {
            int remaining = duration;
            int writeStart = noteStart;

            while (remaining > 0)
            {
                int targetBucket = Math.Min(writeStart / BucketSize, bucketCount - 1);
                int bucketStartTick = targetBucket * BucketSize;
                int relStartInBucket = Math.Min(writeStart - bucketStartTick, BucketSize - 1);
                int available = BucketSize - relStartInBucket;
                int slice = Math.Min(remaining, available);

                ulong packed = Pack(relStartInBucket, (ushort)slice, (byte)noteNumber, colorIndex, trackIndex);
                AddToBucket(targetBucket, packed);

                writeStart += slice;
                remaining -= slice;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddToBucket(int bucket, ulong packed)
        {
            lock (bucketBuffers) // Single lock for simplicity
            {
                if (bucketBuffers[bucket] == null)
                    bucketBuffers[bucket] = new ulong[512];
                
                if (bucketCounts[bucket] >= bucketBuffers[bucket].Length)
                {
                    var newBuf = new ulong[bucketBuffers[bucket].Length * 2];
                    Array.Copy(bucketBuffers[bucket], newBuf, bucketCounts[bucket]);
                    bucketBuffers[bucket] = newBuf;
                }
                
                bucketBuffers[bucket][bucketCounts[bucket]++] = packed;
            }
        }

        private static void SortBucket(int b)
        {
            int cnt = bucketCounts[b];
            if (cnt <= 1 || bucketBuffers[b] == null) return;

            Array.Sort(bucketBuffers[b], 0, cnt, Comparer<ulong>.Create((u1, u2) =>
            {
                int r1 = (int)(u1 & 0x7FFu);
                int r2 = (int)(u2 & 0x7FFu);
                return r1 != r2 ? r1 - r2 : 
                       (int)((u1 >> 11) & 0xFFFFu) - (int)((u2 >> 11) & 0xFFFFu);
            }));
        }

        private static void FlattenBuckets(int bucketCount, long totalNotes)
        {
            AllPackedNotes = new byte[totalNotes * 8];
            
            int writeOffset = 0;
            fixed (byte* destPtr = AllPackedNotes)
            {
                for (int b = 0; b < bucketCount; b++)
                {
                    var buf = bucketBuffers[b];
                    int cnt = bucketCounts[b];
                    if (cnt == 0 || buf == null) continue;

                    fixed (ulong* srcPtr = buf)
                    {
                        Buffer.MemoryCopy(srcPtr, destPtr + writeOffset, cnt * 8, cnt * 8);
                    }
                    writeOffset += cnt * 8;
                }
            }
        }

        private static void BuildBucketOffsets(int bucketCount)
        {
            BucketOffsets = new int[bucketCount + 1];
            int accum = 0;
            for (int i = 0; i < bucketCount; i++)
            {
                BucketOffsets[i] = accum;
                accum += bucketCounts[i];
            }
            BucketOffsets[bucketCount] = accum;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Pack(int relStart, ushort duration, byte note, byte color, ushort track)
        {
            return (ulong)(relStart & 0x7FF) |
                   ((ulong)duration << 11) |
                   ((ulong)(note & 0x7F) << 27) |
                   ((ulong)(color & 0x0F) << 34) |
                   ((ulong)track << 38);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackNoteAt(int index, out int relStart, out int duration, out int noteNumber, out int colorIndex, out int trackIndex)
        {
            fixed (byte* ptr = &AllPackedNotes[index * 8])
            {
                ulong packed = *(ulong*)ptr;
                relStart = (int)(packed & 0x7FF);
                duration = (int)((packed >> 11) & 0xFFFF);
                noteNumber = (int)((packed >> 27) & 0x7F);
                colorIndex = (int)((packed >> 34) & 0x0F);
                trackIndex = (int)((packed >> 38) & 0xFFFF);
            }
        }

        private static void ClearAllData()
        {
            AllPackedNotes = Array.Empty<byte>();
            BucketOffsets = Array.Empty<int>();
            bucketBuffers = Array.Empty<ulong[]>();
            bucketCounts = Array.Empty<int>();
            Array.Clear(activeFlags, 0, activeFlags.Length);
        }

        public static void Cleanup()
        {
            IsReady = false;
            ClearAllData();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte CalculateColorIndex(int channel, int trackIndex)
        {
            // Primary color is based on track index
            // If track uses multiple channels, use channel for variation
            return (byte)((channel + (trackIndex << 1)) & 0x0F);
        }
    }
}