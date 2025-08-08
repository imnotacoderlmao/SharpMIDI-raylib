using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct OptimizedEnhancedNote
        {
            private readonly ulong packed;

            public int StartTick => (int)(packed & 0x3FFFFFFF);
            public int Duration => (int)((packed >> 30) & 0xFFFF);
            public int EndTick => StartTick + Duration;
            public byte NoteNumber => (byte)((packed >> 46) & 0x7F);
            public byte NoteLayer => (byte)((packed >> 53) & 0x0F);
            public uint Color => (uint)((packed >> 60) & 0x0F);
            
            // Layout:
            // Bits 0–29:   startTick (30 bits)
            // Bits 30–45:  duration  (16 bits)
            // Bits 46–52:  noteNumber (7 bits)
            // Bits 53–59:  noteLayer (7 bits) - 128 layers
            // Bits 60–63:  colorIndex (4 bits) - 16 colors

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public OptimizedEnhancedNote(int startTick, int duration, byte noteNumber, byte colorIndex, byte noteLayer)
            {
                packed = ((ulong)(startTick & 0x3FFFFFFF)) |
                         ((ulong)(duration & 0xFFFF) << 30) |
                         ((ulong)(noteNumber & 0x7F) << 46) |
                         ((ulong)(noteLayer & 0x7F) << 53) |
                         ((ulong)(colorIndex & 0x0F) << 60);
            }
        }
        
        public static uint GetTrackColor(int index)
        {
            return trackColors[index & 0x0F]; // safe mask - loops after 16 colors
        }

        private const uint BK = 0x54A;
        public static OptimizedEnhancedNote[] allNotes = Array.Empty<OptimizedEnhancedNote>();
        private static readonly object readyLock = new();

        public static readonly uint[] trackColors = new uint[16]; // 16 colors matching MIDI channels

        private static List<OptimizedEnhancedNote> noteList = new(65536);
        private static Dictionary<int, (int, byte)> activeNotes = new(128);
        public static uint[] AllNoteColors = Array.Empty<uint>();   // full ARGB per note
        public static int[] BucketOffsets = Array.Empty<int>();     // index into allNotes
        public static int BucketSize = 2048;     
        public static bool IsReady { get; private set; } = false;
        public static OptimizedEnhancedNote[] AllNotes => allNotes;
        public static object ReadyLock => readyLock;

        static NoteProcessor()
        {
            InitializeTrackColors();
        }

        private static void InitializeTrackColors()
        {
            const float G = 1.618034f;
            const float saturation = 0.72f;
            const float brightness = 0.18f;

            for (int i = 0; i < 16; i++) // Generate 16 colors (matching MIDI channels)
            {
                float h = (i * G * 360f) % 360f;
                float c = saturation;
                float x = c * (1f - MathF.Abs((h / 60f) % 2f - 1f));
                int sector = (int)(h / 60f) % 6;

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

                uint finalR = (uint)((r + brightness) * 255);
                uint finalG = (uint)((g + brightness) * 255);
                uint finalB = (uint)((b + brightness) * 255);

                trackColors[i] = 0xFF000000 | (finalR << 16) | (finalG << 8) | finalB;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void EnhanceTracksForRendering()
        {
            lock (readyLock)
            {
                IsReady = false;
                if (MIDIPlayer.tracks == null) return;

                // Clear previous data first
                noteList.Clear();
                activeNotes.Clear();
                
                // Pre-size collections based on estimated needs
                var tracks = MIDIPlayer.tracks;
                int estimatedNotes = 0;
                for (int i = 0; i < tracks.Length; i++)
                {
                    if (tracks[i]?.synthEvents != null)
                        estimatedNotes += tracks[i].synthEvents.Count / 2; // Rough estimate
                }
                
                // Ensure adequate capacity but don't go overboard
                int targetCapacity = Math.Min(estimatedNotes * 2, 10_000_000); // Cap at 10M to prevent excessive allocation
                if (noteList.Capacity < targetCapacity)
                    noteList.Capacity = targetCapacity;

                // First pass: analyze channel usage per track to determine coloring strategy
                var channelTrackCount = new Dictionary<int, List<int>>(); // channel -> list of track indices
                
                for (int ti = 0; ti < tracks.Length; ti++)
                {
                    var track = tracks[ti];
                    if (track?.synthEvents == null || track.synthEvents.Count == 0) continue;

                    var channelsInTrack = new HashSet<int>();
                    var events = track.synthEvents;

                    for (int ei = 0; ei < events.Count; ei++)
                    {
                        var e = events[ei];
                        int val = e.val;
                        int stat = val & 0xF0;
                        if (stat == 0x90 || stat == 0x80) // Note events only
                        {
                            int ch = val & 0x0F;
                            channelsInTrack.Add(ch);
                        }
                    }

                    // Record which tracks use which channels
                    foreach (int ch in channelsInTrack)
                    {
                        if (!channelTrackCount.ContainsKey(ch))
                            channelTrackCount[ch] = new List<int>();
                        channelTrackCount[ch].Add(ti);
                    }
                }

                // Second pass: process tracks with hybrid coloring
                for (int ti = 0; ti < tracks.Length; ti++)
                {
                    var track = tracks[ti];
                    if (track?.synthEvents == null || track.synthEvents.Count == 0) continue;

                    activeNotes.Clear();
                    int tick = 0;
                    byte noteLayer = (byte)(ti & 0x7F); // 7-bit layer (128 layers)

                    var events = track.synthEvents;

                    for (int ei = 0; ei < events.Count; ei++)
                    {
                        var e = events[ei];
                        tick += (int)e.pos;

                        int val = e.val;
                        int stat = val & 0xF0;
                        int ch = val & 0x0F;  // MIDI channel (0-15)
                        int note = (val >> 8) & 0x7F;
                        int vel = (val >> 16) & 0x7F;

                        if ((uint)note > 127u) continue;

                        // Hybrid coloring strategy
                        byte colorIndex;
                        if (channelTrackCount.ContainsKey(ch) && channelTrackCount[ch].Count > 1)
                        {
                            // Multiple tracks on same channel - use track index for differentiation
                            int trackIndexInChannel = channelTrackCount[ch].IndexOf(ti);
                            colorIndex = (byte)((ch + trackIndexInChannel * 4) & 0x0F); // Spread colors
                        }
                        else
                        {
                            // Single track per channel - use channel-based coloring
                            colorIndex = (byte)(ch & 0x0F);
                        }
                        
                        int key = (ch << 7) | note;

                        if (stat == 0x90 && vel > 0)
                        {
                            activeNotes[key] = (tick, (byte)vel);
                        }
                        else if (stat == 0x80 || (stat == 0x90 && vel == 0))
                        {
                            if (activeNotes.TryGetValue(key, out var info))
                            {
                                int duration = Math.Clamp(tick - info.Item1, 1, 65535);
                                noteList.Add(new OptimizedEnhancedNote(info.Item1, duration, (byte)note, colorIndex, noteLayer));
                                activeNotes.Remove(key);
                            }
                        }
                    }

                    // Handle hanging notes with same hybrid coloring logic
                    if (activeNotes.Count > 0)
                    {
                        int fallbackEnd = tick + 100;
                        foreach (var kv in activeNotes)
                        {
                            int note = kv.Key & 0x7F;
                            int ch = (kv.Key >> 7) & 0x0F;
                            
                            // Apply same hybrid coloring for hanging notes
                            byte colorIndex;
                            if (channelTrackCount.ContainsKey(ch) && channelTrackCount[ch].Count > 1)
                            {
                                int trackIndexInChannel = channelTrackCount[ch].IndexOf(ti);
                                colorIndex = (byte)((ch + trackIndexInChannel * 4) & 0x0F);
                            }
                            else
                            {
                                colorIndex = (byte)(ch & 0x0F);
                            }
                            
                            var info = kv.Value;
                            int duration = Math.Clamp(fallbackEnd - info.Item1, 1, 65535);
                            noteList.Add(new OptimizedEnhancedNote(info.Item1, duration, (byte)note, colorIndex, noteLayer));
                        }
                        activeNotes.Clear();
                    }
                }

                // Sort by startTick, then noteLayer for optimal rendering
                noteList.Sort((a, b) =>
                {
                    int d = a.StartTick - b.StartTick;
                    return d != 0 ? d : a.NoteLayer - b.NoteLayer;
                });

                // Replace old array
                var oldNotes = allNotes;
                allNotes = noteList.ToArray();
                
                int n = allNotes.Length;
                var colors = new uint[n];
                for (int i = 0; i < n; i++)
                {
                    // note.Color returns the 4-bit index; GetTrackColor returns ARGB full uint
                    colors[i] = GetTrackColor((int)(allNotes[i].Color & 0x0F));
                }
                AllNoteColors = colors;

                if (n == 0)
                {
                    BucketOffsets = Array.Empty<int>();
                }
                else
                {
                    int maxStart = allNotes[n - 1].StartTick;
                    int bucketCount = (maxStart / BucketSize) + 2; // +1 for final sentinel
                    var offsets = new int[bucketCount + 1]; // make room for sentinel (offsets[bucketCount] = n)

                    // Fill offsets with first index >= bucketStartTick
                    int noteIdx = 0;
                    for (int b = 0; b <= bucketCount; b++)
                    {
                        int bucketTick = b * BucketSize;
                        while (noteIdx < n && allNotes[noteIdx].StartTick < bucketTick) noteIdx++;
                        offsets[b] = noteIdx;
                    }
                    offsets[bucketCount] = n; // sentinel (safe end)
                    BucketOffsets = offsets;
                }

                // Clear old reference to help GC (in case it was large)
                if (oldNotes.Length > 1_000_000)
                {
                    // Force cleanup of large arrays
                    Array.Clear(oldNotes, 0, oldNotes.Length);
                }
                
                IsReady = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Cleanup()
        {
            lock (readyLock)
            {
                IsReady = false;
                
                // Store reference to old array for cleanup
                var oldNotes = allNotes;
                allNotes = Array.Empty<OptimizedEnhancedNote>();
                
                noteList?.Clear();
                activeNotes?.Clear();
                
                // Force cleanup of large arrays
                if (oldNotes.Length > 1_000_000)
                {
                    Array.Clear(oldNotes, 0, oldNotes.Length);
                }
                
                // Trim excess capacity if collections got too large
                if (noteList != null && noteList.Capacity > 1_000_000)
                {
                    noteList = new List<OptimizedEnhancedNote>(65536);
                }
                if (activeNotes != null && activeNotes.Count > 1000)
                {
                    activeNotes = new Dictionary<int, (int, byte)>(128);
                }
                
                // Force garbage collection for large data sets
                if (oldNotes.Length > 5_000_000)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Shutdown()
        {
            lock (readyLock)
            {
                IsReady = false;
                
                var oldNotes = allNotes;
                allNotes = Array.Empty<OptimizedEnhancedNote>();
                
                noteList?.Clear();
                activeNotes?.Clear();
                
                // More aggressive cleanup on shutdown
                if (oldNotes.Length > 100_000)
                {
                    Array.Clear(oldNotes, 0, oldNotes.Length);
                }
                
                // Nullify references
                noteList = null;
                activeNotes = null;
                
                // Force full GC
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
    }
}