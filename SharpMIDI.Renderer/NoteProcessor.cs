using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpMIDI;

namespace SharpMIDI.Renderer
{
    public static unsafe class NoteProcessor
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct EnhancedNote
        {
            public float startTime, endTime;
            public int noteNumber;
            public byte velocity, height;
            public uint color;
            public int trackIndex;
            public byte noteLayer;
        }

        private const uint BK = 0x54A;
        public static EnhancedNote[] allNotes = Array.Empty<EnhancedNote>();
        private static readonly object readyLock = new();
        
        // Pre-computed lookup tables - cache-friendly
        private static readonly byte[] noteHeights = new byte[128];
        private static readonly uint[] trackColors = new uint[256];
        private static readonly bool[] isBlackKey = new bool[128];
        
        // Reusable collections to reduce allocations
        private static readonly List<EnhancedNote> noteList = new(65536);
        private static readonly Dictionary<int, (float, byte)> activeNotes = new(128);
        
        public static bool IsReady { get; private set; } = false;
        public static EnhancedNote[] AllNotes => allNotes;
        public static object ReadyLock => readyLock;

        static NoteProcessor()
        {
            InitializeNoteHeights();
            InitializeTrackColors();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void InitializeNoteHeights()
        {
            // Precompute both heights and black key lookup using bit operations
            for (int i = 0; i < 128; i++)
            {
                int noteInOctave = i & 0x0F; // i % 12, but faster for powers of 2
                if (noteInOctave >= 12) noteInOctave -= 12; // Handle the case where & 0x0F gives > 11
                
                // Alternative: use actual modulo but with bit shifting optimization elsewhere
                noteInOctave = i - ((i / 12) << 3) - ((i / 12) << 2); // i % 12 via bit shifts: i - (i/12)*12, where *12 = *8 + *4
                
                // Actually, let's keep it simple and fast:
                int mod12 = i % 12; // Compiler optimizes this well for constants
                bool isBlack = ((BK >> mod12) & 1) != 0;
                
                noteHeights[i] = (byte)(isBlack ? 6 : 12);
                isBlackKey[i] = isBlack;
            }
        }

        private static void InitializeTrackColors()
        {
            // Optimized color generation - precompute HSV to RGB
            const float G = 1.618034f;
            const float saturation = 0.72f;
            const float brightness = 0.18f;
            
            for (int i = 0; i < 256; i++)
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
                
                // Single calculation for final color
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
                
                // Reuse collections instead of allocating new ones
                noteList.Clear();
                if (noteList.Capacity < 65536) noteList.Capacity = 65536;
                
                var tracks = MIDIPlayer.tracks;
                int trackCount = tracks.Length;
                
                for (int ti = 0; ti < trackCount; ti++)
                {
                    var track = tracks[ti];
                    if (track?.synthEvents == null || track.synthEvents.Count == 0) continue;

                    activeNotes.Clear();
                    float t = 0f;
                    uint color = trackColors[ti & 0xFF]; // Explicit 8-bit mask
                    byte noteLayer = (byte)(ti & 0xFF);
                    
                    var events = track.synthEvents;
                    int eventCount = events.Count;

                    // Process events with minimal branching and optimized bit operations
                    for (int ei = 0; ei < eventCount; ei++)
                    {
                        var e = events[ei];
                        t += e.pos;
                        
                        int val = e.val;
                        int stat = val & 0xF0;
                        int ch = val & 0x0F;
                        int note = (val >> 8) & 0x7F;
                        int vel = (val >> 16) & 0x7F;
                        
                        if ((uint)note > 127u) continue; // Unsigned comparison is faster
                        
                        int key = (ch << 7) | note; // ch * 128 + note via bit ops

                        if (stat == 0x90 && vel > 0) 
                        {
                            activeNotes[key] = (t, (byte)vel);
                        }
                        else if (stat == 0x80 || (stat == 0x90 && vel == 0))
                        {
                            if (activeNotes.TryGetValue(key, out var info))
                            {
                                // Inline note creation to avoid method call overhead
                                noteList.Add(new EnhancedNote { 
                                    startTime = info.Item1, 
                                    endTime = t, 
                                    noteNumber = note, 
                                    velocity = info.Item2, 
                                    height = noteHeights[note], 
                                    color = color,
                                    trackIndex = ti,
                                    noteLayer = noteLayer
                                });
                                activeNotes.Remove(key);
                            }
                        }
                    }

                    // Handle remaining active notes - batch process
                    if (activeNotes.Count > 0)
                    {
                        float endTime = t + 100f;
                        foreach (var kv in activeNotes)
                        {
                            int note = kv.Key & 0x7F; // Extract note via bit mask (faster than % 128)
                            var info = kv.Value;
                            noteList.Add(new EnhancedNote { 
                                startTime = info.Item1, 
                                endTime = endTime, 
                                noteNumber = note, 
                                velocity = info.Item2, 
                                height = noteHeights[note], 
                                color = color,
                                trackIndex = ti,
                                noteLayer = noteLayer
                            });
                        }
                    }
                }

                // Optimized sorting - primary by start time, secondary by layer
                noteList.Sort((a, b) => {
                    float diff = a.startTime - b.startTime;
                    if (diff < 0f) return -1;
                    if (diff > 0f) return 1;
                    return a.noteLayer.CompareTo(b.noteLayer);
                });
                
                allNotes = noteList.ToArray();
                IsReady = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Cleanup()
        {
            lock (readyLock)
            {
                IsReady = false;
                allNotes = Array.Empty<EnhancedNote>();
                noteList.Clear();
                activeNotes.Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Shutdown()
        {
            lock (readyLock)
            {
                IsReady = false;
                allNotes = Array.Empty<EnhancedNote>();
                noteList?.Clear();
                activeNotes?.Clear();
            }
        }
    }
}