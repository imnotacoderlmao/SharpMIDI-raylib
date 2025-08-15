using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        // --- PACKED NOTE FORMAT (64 bits per note) ---
        // bits 0..31   -> startTick (32 bits)
        // bits 32..47  -> duration  (16 bits) 
        // bits 48..54  -> noteNumber (7 bits)
        // bits 55..58  -> colorIndex (4 bits)
        // bits 59..63  -> trackIndex (5 bits) - supports up to 32 tracks

        public static byte[] AllPackedNotes = Array.Empty<byte>();
        private static bool AllPackedNotesRented = false;
        public static int[] BucketOffsets = Array.Empty<int>();
        public static int BucketSize = 2048;
        public static bool IsReady { get; private set; } = false;

        // Active note tracking for direct conversion
        private static readonly Dictionary<int, ActiveNote> activeNotes = new Dictionary<int, ActiveNote>(2048);
        private static readonly List<PackedNote> tempNotes = new List<PackedNote>(65536);

        public static int TotalPackedNotes => (BucketOffsets != null && BucketOffsets.Length > 0) ? BucketOffsets[^1] : 0;

        // 16 channel colors - pre-calculated
        public static readonly uint[] trackColors = new uint[16];

        // Compact active note structure
        private struct ActiveNote
        {
            public float StartTick;
            public byte Velocity;
            public byte Channel;
            public byte Note;
            public int TrackIndex;
        }

        // Packed note for sorting and storage
        private struct PackedNote
        {
            public float StartTick;
            public ushort Duration;
            public byte Note;
            public byte ColorIndex;
            public int TrackIndex;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public PackedNote(float start, int duration, byte note, byte colorIndex, int trackIndex)
            {
                StartTick = start;
                Duration = (ushort)Math.Min(duration, 65535);
                Note = note;
                ColorIndex = colorIndex;
                TrackIndex = trackIndex;
            }
        }

        static NoteProcessor()
        {
            InitializeTrackColors();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void InitializeTrackColors()
        {
            // Generate distinct colors using golden ratio spacing
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
                ClearAllData();

                if (MIDIPlayer.tracks == null || MIDIPlayer.tracks.Length == 0)
                {
                    IsReady = true;
                    return;
                }

                var tracks = MIDIPlayer.tracks;
                int trackCount = tracks.Length;

                // Process all tracks directly from SynthEvent data
                for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
                {
                    var track = tracks[trackIndex];
                    if (track?.synthEvents == null || track.synthEvents.Count == 0) 
                        continue;

                    ProcessTrackSynthEvents(track, trackIndex);
                }

                // Sort notes by start time for efficient rendering
                if (tempNotes.Count > 1)
                {
                    tempNotes.Sort((a, b) => 
                    {
                        int diff = a.StartTick.CompareTo(b.StartTick);
                        return diff != 0 ? diff : a.TrackIndex.CompareTo(b.TrackIndex);
                    });
                }

                // Convert to packed format
                ConvertToPackedFormat();
                IsReady = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ProcessTrackSynthEvents(MIDITrack track, int trackIndex)
        {
            activeNotes.Clear();
            
            var events = track.synthEvents;
            int eventCount = events.Count;
            float currentTick = 0;

            for (int i = 0; i < eventCount; i++)
            {
                var synthEvent = events[i];
                currentTick += synthEvent.pos;

                int eventValue = synthEvent.val;
                int status = eventValue & 0xF0;
                int channel = eventValue & 0x0F;
                int noteNumber = (eventValue >> 8) & 0x7F;
                int velocity = (eventValue >> 16) & 0x7F;

                if ((uint)noteNumber > 127u) continue;

                if (status == 0x90 && velocity > 0) // Note On
                {
                    int key = (channel << 7) | noteNumber;
                    activeNotes[key] = new ActiveNote
                    {
                        StartTick = currentTick,
                        Velocity = (byte)velocity,
                        Channel = (byte)channel,
                        Note = (byte)noteNumber,
                        TrackIndex = trackIndex
                    };
                }
                else if (status == 0x80 || (status == 0x90 && velocity == 0)) // Note Off
                {
                    int key = (channel << 7) | noteNumber;
                    if (activeNotes.TryGetValue(key, out var activeNote))
                    {
                        float duration = Math.Max(1f, currentTick - activeNote.StartTick);
                        byte colorIndex = CalculateColorIndex(activeNote.Channel, trackIndex);
                        
                        tempNotes.Add(new PackedNote(
                            activeNote.StartTick,
                            (int)duration,
                            activeNote.Note,
                            colorIndex,
                            trackIndex
                        ));
                        
                        activeNotes.Remove(key);
                    }
                }
            }

            // Handle remaining active notes (notes that never got a note-off)
            float fallbackEndTick = currentTick + 100f;
            foreach (var kvp in activeNotes)
            {
                var activeNote = kvp.Value;
                float duration = Math.Max(1f, fallbackEndTick - activeNote.StartTick);
                byte colorIndex = CalculateColorIndex(activeNote.Channel, trackIndex);
                
                tempNotes.Add(new PackedNote(
                    activeNote.StartTick,
                    (int)duration,
                    activeNote.Note,
                    colorIndex,
                    trackIndex
                ));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte CalculateColorIndex(int channel, int trackIndex)
        {
            // Simple color calculation - can be enhanced based on your needs
            return (byte)((channel + (trackIndex << 2)) & 0x0F);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ConvertToPackedFormat()
        {
            if (tempNotes.Count == 0)
            {
                ClearAllData();
                return;
            }

            int packedBytes = tempNotes.Count * 8; // 8 bytes per note
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            
            if (AllPackedNotes != null && AllPackedNotes.Length > 0 && AllPackedNotesRented)
            {
                pool.Return(AllPackedNotes);
                AllPackedNotes = Array.Empty<byte>();
                AllPackedNotesRented = false;
            }
            
            AllPackedNotes = pool.Rent(packedBytes);
            AllPackedNotesRented = true;

            // Find max start tick for bucket calculation
            float maxStart = tempNotes[tempNotes.Count - 1].StartTick;
            int bucketCount = (int)(maxStart / BucketSize) + 2;
            var offsets = new int[bucketCount + 1];

            // Pack all notes
            for (int i = 0; i < tempNotes.Count; i++)
            {
                var note = tempNotes[i];
                int bucketIndex = (int)(note.StartTick / BucketSize);
                int bucketStartTick = bucketIndex * BucketSize;
                int relativeStart = Math.Max(0, Math.Min((int)note.StartTick - bucketStartTick, BucketSize - 1));

                PackNoteToBuffer(i, (uint)relativeStart, note.Duration, note.Note, note.ColorIndex, (ushort)note.TrackIndex);
            }

            // Build bucket offsets
            int noteIndex = 0;
            for (int bucketIndex = 0; bucketIndex <= bucketCount; bucketIndex++)
            {
                int bucketStartTick = bucketIndex * BucketSize;
                while (noteIndex < tempNotes.Count && tempNotes[noteIndex].StartTick < bucketStartTick)
                    noteIndex++;
                offsets[bucketIndex] = noteIndex;
            }
            
            
            BucketOffsets = offsets;
            tempNotes.Clear(); // Clear temporary storage
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PackNoteToBuffer(int index, uint relStart11, ushort duration16, byte note7, byte color4, ushort track5)
        {
            ulong packedValue = 0;
            packedValue |= (ulong)(relStart11 & 0x7FFu);           // 11 bits: relative start
            packedValue |= ((ulong)duration16 << 11);              // 16 bits: duration  
            packedValue |= ((ulong)(note7 & 0x7Fu) << 27);         // 7 bits: note number
            packedValue |= ((ulong)(color4 & 0x0Fu) << 34);        // 4 bits: color index
            packedValue |= ((ulong)(track5 & 0x1Fu) << 38);        // 5 bits: track index

            int offset = index * 8;
            fixed (byte* ptr = &AllPackedNotes[offset])
            {
                *(ulong*)ptr = packedValue;
            }
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
            trackIndex = (int)((packedValue >> 38) & 0x1Fu);
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
            activeNotes.Clear();
            tempNotes.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Cleanup()
        {
            IsReady = false;
            ClearAllData();
        }
    }
}