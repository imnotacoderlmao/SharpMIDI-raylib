using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        // --- PACKED NOTE FORMAT (64 bits stored as ulong, written to AllPackedNotes as 8 bytes) ---
        // bits 0..10   -> relativeStart (11 bits)  (0..2047)
        // bits 11..26  -> duration     (16 bits)  (0..65535)
        // bits 27..33  -> noteNumber   (7 bits)   (0..127)
        // bits 34..37  -> colorIndex   (4 bits)   (0..15)
        // bits 38..53  -> trackIndex   (16 bits)  (0..65535)
        //
        // total used bits: 54, fits easily in ulong

        public static byte[] AllPackedNotes = Array.Empty<byte>();
        private static bool AllPackedNotesRented = false;
        public static int[] BucketOffsets = Array.Empty<int>();
        public static int BucketSize = 2048;
        public static bool IsReady { get; private set; } = false;

        private const int ACTIVE_SLOTS = 16 * 128; // channel<<7 | note
        private const int INITIAL_BUCKET_CAPACITY = 1024; // per-bucket initial capacity (in notes)

        // Per-bucket dynamic buffers (store packed ulongs)
        private static ulong[][] bucketBuffers = Array.Empty<ulong[]>();
        private static int[] bucketCounts = Array.Empty<int>();

        // O(1) active note table (no dictionary)
        private struct ActiveNote
        {
            public float StartTick;
            public byte Velocity;
            public byte Channel;
            public byte Note;
            public ushort TrackIndex;
        }
        private static readonly ActiveNote[] activeNotes = new ActiveNote[ACTIVE_SLOTS];
        private static readonly bool[] activeFlags = new bool[ACTIVE_SLOTS];

        // Color table (16 channel colors)
        public static readonly uint[] trackColors = new uint[16];

        public static int TotalPackedNotes => (BucketOffsets != null && BucketOffsets.Length > 0) ? BucketOffsets[^1] : 0;

        static NoteProcessor()
        {
            InitializeTrackColors();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void InitializeTrackColors()
        {
            const float goldenRatio = 1.618034f;
            const float saturation = 0.72f;
            const float brightness = 0.18f;
            const float inv60 = 1f / 60f;

            for (int i = 0; i < 16; i++)
            {
                float h = (i * goldenRatio * 360f) % 360f;
                float c = saturation;
                float x = c * (1f - MathF.Abs((h * inv60) % 2f - 1f));
                int sector = (int)(h * inv60) % 6;

                float r, g, b;
                switch (sector)
                {
                    case 0: r = c; g = x; b = 0; break;
                    case 1: r = x; g = c; b = 0; break;
                    case 2: r = 0; g = c; b = x; break;
                    case 3: r = 0; g = x; b = c; break;
                    case 4: r = x; g = 0; b = c; break;
                    default: r = c; g = 0; b = x; break;
                }

                uint finalR = Math.Min((uint)((r + brightness) * 255f), 255u);
                uint finalG = Math.Min((uint)((g + brightness) * 255f), 255u);
                uint finalB = Math.Min((uint)((b + brightness) * 255f), 255u);

                trackColors[i] = 0xFF000000u | (finalR << 16) | (finalG << 8) | finalB;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void EnhanceTracksForRendering()
        {
            lock (typeof(NoteProcessor))
            {
                IsReady = false;
                ClearAllData(); // resets buffers

                if (MIDIPlayer.tracks == null || MIDIPlayer.tracks.Length == 0)
                {
                    IsReady = true;
                    return;
                }

                var tracks = MIDIPlayer.tracks;
                int trackCount = tracks.Length;

                // Estimate global max tick to size bucket array
                double globalMaxTick = 0;
                for (int t = 0; t < trackCount; t++)
                {
                    var tr = tracks[t];
                    if (tr == null) continue;
                    if (tr.maxTick > 0) globalMaxTick = Math.Max(globalMaxTick, tr.maxTick);
                }

                int bucketCount = Math.Max(2, (int)(globalMaxTick / BucketSize) + 2);

                bucketBuffers = new ulong[bucketCount][];
                bucketCounts = new int[bucketCount];
                for (int i = 0; i < bucketCount; i++)
                {
                    bucketBuffers[i] = null; // lazy
                    bucketCounts[i] = 0;
                }

                // Process tracks directly into bucketBuffers (no temporary list)
                for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
                {
                    var track = tracks[trackIndex];
                    if (track?.synthEvents == null || track.synthEvents.Count == 0) continue;

                    ProcessTrackSynthEvents(track, trackIndex, bucketCount);
                }

                // Count total notes
                long totalNotes = 0;
                for (int i = 0; i < bucketCount; i++) totalNotes += bucketCounts[i];

                if (totalNotes == 0)
                {
                    ClearAllData();
                    IsReady = true;
                    return;
                }

                // Sort each bucket by relStart for chronological order inside bucket
                for (int b = 0; b < bucketCount; b++)
                {
                    int cnt = bucketCounts[b];
                    var buf = bucketBuffers[b];
                    if (cnt <= 1 || buf == null) continue;

                    Array.Sort(buf, 0, cnt, Comparer<ulong>.Create((u1, u2) =>
                    {
                        int r1 = (int)(u1 & 0x7FFu);
                        int r2 = (int)(u2 & 0x7FFu);
                        if (r1 != r2) return r1 - r2;
                        int d1 = (int)((u1 >> 11) & 0xFFFFu);
                        int d2 = (int)((u2 >> 11) & 0xFFFFu);
                        if (d1 != d2) return d1 - d2;
                        int n1 = (int)((u1 >> 27) & 0x7Fu);
                        int n2 = (int)((u2 >> 27) & 0x7Fu);
                        if (n1 != n2) return n1 - n2;
                        int c1 = (int)((u1 >> 34) & 0x0Fu);
                        int c2 = (int)((u2 >> 34) & 0x0Fu);
                        if (c1 != c2) return c1 - c2;
                        int t1 = (int)((u1 >> 38) & 0xFFFFu);
                        int t2 = (int)((u2 >> 38) & 0xFFFFu);
                        return t1 - t2;
                    }));
                }

                // Flatten per-bucket ulongs into AllPackedNotes (byte array from ArrayPool)
                long packedBytes = totalNotes * 8L;
                if (packedBytes > int.MaxValue) throw new InvalidOperationException("Packed notes exceed int.MaxValue bytes.");
                int pb = (int)packedBytes;

                var pool = System.Buffers.ArrayPool<byte>.Shared;
                if (AllPackedNotes != null && AllPackedNotes.Length > 0 && AllPackedNotesRented)
                {
                    pool.Return(AllPackedNotes);
                    AllPackedNotes = Array.Empty<byte>();
                    AllPackedNotesRented = false;
                }
                AllPackedNotes = pool.Rent(pb);
                AllPackedNotesRented = true;

                int writeNoteIndex = 0;
                fixed (byte* destBase = AllPackedNotes)
                {
                    for (int b = 0; b < bucketCount; b++)
                    {
                        var buf = bucketBuffers[b];
                        int cnt = bucketCounts[b];
                        if (cnt == 0 || buf == null) continue;

                        // copy cnt ulongs into dest
                        // fix src pointer for this buffer
                        fixed (ulong* srcPtr = buf)
                        {
                            byte* outPtr = destBase + (writeNoteIndex * 8);
                            for (int i = 0; i < cnt; i++)
                            {
                                *(ulong*)outPtr = srcPtr[i];
                                outPtr += 8;
                            }
                        }
                        writeNoteIndex += cnt;
                    }
                }

                // Build BucketOffsets (note index start per bucket)
                int[] offsets = new int[bucketCount + 1];
                int accum = 0;
                for (int i = 0; i < bucketCount; i++)
                {
                    offsets[i] = accum;
                    accum += bucketCounts[i];
                }
                offsets[bucketCount] = accum;
                BucketOffsets = offsets;

                // free per-bucket buffers (we copied data out)
                for (int i = 0; i < bucketCount; i++) bucketBuffers[i] = null;
                bucketBuffers = Array.Empty<ulong[]>();
                bucketCounts = Array.Empty<int>();

                IsReady = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ProcessTrackSynthEvents(MIDITrack track, int trackIndex, int bucketCount)
        {
            // reset active flags
            Array.Clear(activeFlags, 0, activeFlags.Length);

            var events = track.synthEvents;
            int eventCount = events.Count;
            float currentTick = 0f;

            for (int i = 0; i < eventCount; i++)
            {
                var synthEvent = events[i];
                currentTick += synthEvent.pos; // synthEvent.pos is delta

                int eventValue = synthEvent.val;
                int status = eventValue & 0xF0;
                int channel = eventValue & 0x0F;
                int noteNumber = (eventValue >> 8) & 0x7F;
                int velocity = (eventValue >> 16) & 0x7F;

                if ((uint)noteNumber > 127u) continue;

                int key = (channel << 7) | noteNumber; // 0..2047

                if (status == 0x90 && velocity > 0) // Note ON
                {
                    activeFlags[key] = true;
                    activeNotes[key].StartTick = currentTick;
                    activeNotes[key].Velocity = (byte)velocity;
                    activeNotes[key].Channel = (byte)channel;
                    activeNotes[key].Note = (byte)noteNumber;
                    activeNotes[key].TrackIndex = (ushort)trackIndex;
                }
                else if (status == 0x80 || (status == 0x90 && velocity == 0)) // Note OFF
                {
                    if (!activeFlags[key]) continue;
                    var a = activeNotes[key];
                    // compute integer start and duration
                    int noteStart = (int)Math.Floor(a.StartTick);
                    int duration = (int)Math.Min(65535, Math.Max(1, Math.Floor(currentTick - a.StartTick)));
                    byte colorIndex = CalculateColorIndex(a.Channel, trackIndex);

                    // slice across buckets
                    int remaining = duration;
                    int writeStart = noteStart;

                    while (remaining > 0)
                    {
                        int targetBucket = Math.Max(0, Math.Min(bucketCount - 1, writeStart / BucketSize));
                        EnsureBucketCapacity(targetBucket);

                        int bucketStartTick = targetBucket * BucketSize;
                        int relStartInBucket = Math.Max(0, Math.Min(writeStart - bucketStartTick, BucketSize - 1));
                        int available = BucketSize - relStartInBucket;
                        int slice = Math.Min(remaining, available);

                        ulong packed = Pack(relStartInBucket, (ushort)slice, a.Note, colorIndex, (ushort)trackIndex);
                        bucketBuffers[targetBucket][bucketCounts[targetBucket]++] = packed;

                        writeStart += slice;
                        remaining -= slice;
                    }

                    activeFlags[key] = false;
                }
            }

            // handle remaining actives (no note-off)
            float fallbackEnd = currentTick + 100f;
            for (int slot = 0; slot < ACTIVE_SLOTS; slot++)
            {
                if (!activeFlags[slot]) continue;
                var a = activeNotes[slot];
                int noteStart = (int)Math.Floor(a.StartTick);
                int duration = (int)Math.Min(65535, Math.Max(1, Math.Floor(fallbackEnd - a.StartTick)));
                byte colorIndex = CalculateColorIndex(a.Channel, trackIndex);

                int remaining = duration;
                int writeStart = noteStart;

                while (remaining > 0)
                {
                    int targetBucket = Math.Max(0, Math.Min(bucketCount - 1, writeStart / BucketSize));
                    EnsureBucketCapacity(targetBucket);

                    int bucketStartTick = targetBucket * BucketSize;
                    int relStartInBucket = Math.Max(0, Math.Min(writeStart - bucketStartTick, BucketSize - 1));
                    int available = BucketSize - relStartInBucket;
                    int slice = Math.Min(remaining, available);

                    ulong packed = Pack(relStartInBucket, (ushort)slice, a.Note, colorIndex, (ushort)trackIndex);
                    bucketBuffers[targetBucket][bucketCounts[targetBucket]++] = packed;

                    writeStart += slice;
                    remaining -= slice;
                }

                activeFlags[slot] = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureBucketCapacity(int bucket)
        {
            var buf = bucketBuffers[bucket];
            if (buf == null)
            {
                bucketBuffers[bucket] = new ulong[INITIAL_BUCKET_CAPACITY];
                bucketCounts[bucket] = 0;
                return;
            }
            int cnt = bucketCounts[bucket];
            if (cnt >= buf.Length)
            {
                int newSize = buf.Length * 2;
                if (newSize < buf.Length + 1024) newSize = buf.Length + 1024;
                var nb = new ulong[newSize];
                Array.Copy(buf, 0, nb, 0, cnt);
                bucketBuffers[bucket] = nb;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Pack(int relStart11, ushort duration16, byte note7, byte color4, ushort track16)
        {
            ulong v = 0;
            v |= (ulong)(relStart11 & 0x7FFu);
            v |= ((ulong)(duration16) << 11);
            v |= ((ulong)(note7 & 0x7Fu) << 27);
            v |= ((ulong)(color4 & 0x0Fu) << 34);
            v |= ((ulong)(track16 & 0xFFFFu) << 38);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackNoteAt(int index, out int relStart, out int duration, out int noteNumber, out int colorIndex, out int trackIndex)
        {
            int offset = index * 8;
            ulong packedValue;
            fixed (byte* ptr = &AllPackedNotes[offset])
            {
                packedValue = *(ulong*)ptr;
            }

            relStart = (int)(packedValue & 0x7FFu);
            duration = (int)((packedValue >> 11) & 0xFFFFu);
            noteNumber = (int)((packedValue >> 27) & 0x7Fu);
            colorIndex = (int)((packedValue >> 34) & 0x0Fu);
            trackIndex = (int)((packedValue >> 38) & 0xFFFFu);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte CalculateColorIndex(int channel, int trackIndex)
        {
            // Keep layering effect but simple deterministic mapping
            return (byte)((channel + (trackIndex << 1)) & 0x0F);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearAllData()
        {
            if (AllPackedNotes != null && AllPackedNotes.Length > 0 && AllPackedNotesRented)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(AllPackedNotes);
            }
            AllPackedNotes = Array.Empty<byte>();
            AllPackedNotesRented = false;
            BucketOffsets = Array.Empty<int>();

            if (bucketBuffers != null)
            {
                for (int i = 0; i < bucketBuffers.Length; i++)
                {
                    bucketBuffers[i] = null;
                }
            }
            bucketBuffers = Array.Empty<ulong[]>();
            bucketCounts = Array.Empty<int>();

            Array.Clear(activeFlags, 0, activeFlags.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Cleanup()
        {
            IsReady = false;
            ClearAllData();
        }
    }
}
