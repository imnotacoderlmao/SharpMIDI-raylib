#pragma warning disable 8602
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SharpMIDI
{
    static class MIDILoader
    {   
        private struct TrackProperties
        {
            public long start;
            public uint len;
        }

        private static readonly List<TrackProperties> trackProperties = new List<TrackProperties>();
        private static unsafe byte* filePtr = null;
        private static long fileLength = 0;
        private static long filePos = 0; 

        public static long totalNotes = 0;
        public static long eventCount = 0;
        public static int maxTick = 0;
        public static int trackAmount = 0;
        public static int loadedtracks = 0;
        public static volatile bool midiLoaded = false;
        public static string? filename;
        public static string loadstatus = "No MIDI Loaded";

        public static void Crash(string error)
        {        
            Task.Run(async () =>
            {
                string prevstatus = loadstatus;
                loadstatus = error;
                Console.WriteLine(error);
                await Task.Delay(1000);
                if (loadstatus == error) 
                    loadstatus = prevstatus;
                throw new Exception();
            });
        }

        public static unsafe void LoadMIDI(string path)
        {
            UnloadMIDI();
            filename = Path.GetFileName(path);
            loadstatus = $"Loading MIDI file: {filename}";
            Console.WriteLine(loadstatus);
            if (!path.EndsWith(".mid"))
            { 
                Crash("file doesn't end with 'mid'. are you even loading a midi file?");
                return;
            }
            filePos = 0;

            using (var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
            using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                byte* basePtr = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    filePtr = basePtr;
                    fileLength = accessor.Capacity;

                    loadstatus = $"verifying header";
                    VerifyHeader();
                    MIDIClock.ppq = ppq;
                    trackAmount = 0; 
                    loadedtracks = 0;
                    loadstatus = $"Indexing MIDI tracks...";

                    while (filePos < fileLength)
                    {
                        if (!IndexTrack()) break;
                        loadstatus = $"Indexing MIDI tracks... {trackAmount} found..";
                    }

                    List<TickGroup> histogram = new();
                    loadstatus = $"scanning events for grouping";
                    long countednotes = 0;
                    double parsestart = Timer.Seconds();

                    Parallel.For(0, trackAmount, i =>
                    {
                        byte* trackStartPtr = filePtr + trackProperties[i].start;
                        FastTrack t = new FastTrack(trackStartPtr, trackProperties[i].len);
                        
                        List<TickGroup> localTrackHistogram = new List<TickGroup>();
                        t.ScanEvents(localTrackHistogram);
                        lock (histogram)
                        {
                            histogram.AddRange(localTrackHistogram);
                        }
                        Interlocked.Add(ref eventCount, t.eventCount);
                        Interlocked.Add(ref countednotes, t.totalNotes);
                        Interlocked.Increment(ref loadedtracks);
                        Console.Write($"\rcounted {loadedtracks}/{trackAmount} tracks | total nc: {countednotes:N0}");
                        t.Dispose();
                    });
                    
                    double parseend = Timer.Seconds();
                    double parsetime = parseend - parsestart;
                    Console.WriteLine($"\ncounted {countednotes:N0} notes in {parsetime}s ({countednotes/parsetime:N0} notes/sec)");
                    histogram.TrimExcess();
                    
                    long[] writeCursors = new long[maxTick + 2];
                    TickGroup[] tickgroup = new TickGroup[maxTick + 2];
                    foreach (TickGroup g in histogram)
                    {
                        tickgroup[g.tick].offset += g.offset;
                        tickgroup[g.tick].notecount += g.notecount;
                    }
                    histogram = null;
                    long event_offset = 0;
                    for (int t = 0; t <= maxTick; t++)
                    {
                        writeCursors[t] = event_offset;
                        long tickEventCount = tickgroup[t].offset;
                        tickgroup[t] = new TickGroup 
                        { 
                            tick = t, 
                            notecount = tickgroup[t].notecount, 
                            offset = event_offset
                        };
                        event_offset += tickEventCount;
                    }

                    SynthEvent.Alloc(eventCount, WindowManager.trackcolors);
                    uint24* msgPtr = SynthEvent.messages.Pointer;
                    ushort* trackPtr = WindowManager.trackcolors ? SynthEvent.track.Pointer : null;

                    loadstatus = $"actually parsing events now";
                    Console.WriteLine(loadstatus);
                    loadedtracks = 0;
                    totalNotes = 0;
                    parsestart = Timer.Seconds();

                    Parallel.For(0, trackAmount, i =>
                    {
                        fixed (long* wc = writeCursors)
                        {
                            byte* trackStartPtr = filePtr + trackProperties[i].start;
                            FastTrack t = new FastTrack(trackStartPtr, trackProperties[i].len);
                            t.ParseTrackEvents(msgPtr, trackPtr, wc, (ushort)i);
                            Interlocked.Add(ref totalNotes, t.totalNotes);
                            Interlocked.Increment(ref loadedtracks);
                            Console.Write($"\rparsed {loadedtracks} tracks | ({totalNotes:N0} notes parsed)");
                            t.Dispose();
                        }
                    });

                    parseend = Timer.Seconds();
                    tickgroup[maxTick + 1] = new TickGroup { tick = int.MaxValue, notecount = 0, offset = event_offset };
                    MIDIEvent.TickGroupArray = tickgroup;
                    GLNoteRenderer.InitializeForMIDI();
                    
                    parsetime = parseend - parsestart;
                    string memusage = 
@$"memory usage statistics below
current usage: {Starter.toMemoryText(Process.GetCurrentProcess().WorkingSet64)}
event array: {Starter.toMemoryText(eventCount * sizeof(uint24))} | timing: {Starter.toMemoryText(maxTick + 2 * sizeof(TickGroup))}
track array: {Starter.toMemoryText(WindowManager.trackcolors? (eventCount * sizeof(ushort)) : 0)}
expected: {Starter.toMemoryText((eventCount * sizeof(uint24) + (WindowManager.trackcolors? (eventCount * sizeof(ushort)) : 0)) + ((maxTick + 2) * sizeof(TickGroup)))}";
                    
                    Console.WriteLine($"\nParsed {totalNotes:N0} notes in {parsetime}s ({totalNotes/parsetime:N0} notes/sec)");
                    Console.WriteLine(memusage);
                }
                finally
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    filePtr = null;
                    fileLength = 0;
                }
            }

            tempMIDIstorage.temppos.Add(new Tempo { tick = int.MaxValue, tempo = 500000 });
            tempMIDIstorage.SysEx.Add(new SysEx { tick = int.MaxValue, message = [] });
            MIDIEvent.TempoEventArray = [.. tempMIDIstorage.temppos];
            MIDIEvent.SysExArray = [.. tempMIDIstorage.SysEx];
            Array.Sort(MIDIEvent.TempoEventArray, (a, b) => a.tick.CompareTo(b.tick));
            Array.Sort(MIDIEvent.SysExArray, (a, b) => a.tick.CompareTo(b.tick));
            tempMIDIstorage.temppos.Clear();
            tempMIDIstorage.SysEx.Clear();
            midiLoaded = true;
            loadstatus = filename;
        }

        public static void UnloadMIDI()
        {
            if (!midiLoaded) return;
            midiLoaded = false;
            MIDIPlayer.stopping = true;
            GLNoteRenderer.ResetForUnload();
            totalNotes = 0;
            eventCount = 0;
            maxTick = 0;
            trackAmount = 0;
            trackProperties.Clear();
            SynthEvent.Dispose();
            MIDIEvent.TempoEventArray = null;
            MIDIEvent.SysExArray = null;
            MIDIEvent.TickGroupArray = null;
            loadstatus = $"No MIDI Loaded";
            GC.Collect();
        }

        static uint headersize = 0; 
        static uint fmt = 0;
        static uint ppq = 0;

        static void VerifyHeader()
        {
            if (FindText("MThd"))
            {
                headersize = ReadUInt32();
                fmt = ReadUInt16();
                filePos += 2;
                ppq = ReadUInt16();
                if (fmt == 2) 
                    Crash("MIDI format 2 unsupported");
                if (ppq < 0)  
                    Crash("PPQ is negative");
                if (headersize != 6) 
                    Crash($"Incorrect header size of {headersize}");
            }
            else
            {
                Crash("Header issue");
            }
        }

        static bool IndexTrack()
        {
            if (FindText("MTrk"))
            {
                uint size = ReadUInt32();
                trackProperties.Add(new TrackProperties
                {
                    start = filePos,
                    len = size
                });
                filePos += size;
                trackAmount++;
                return true;
            }
            return false;
        }

        static unsafe uint ReadUInt32()
        {
            uint length = 0;
            for (int i = 0; i != 4; i++)
                length = (length << 8) | filePtr[filePos++];
            return length;
        }

        static unsafe ushort ReadUInt16()
        {
            ushort length = 0;
            for (int i = 0; i != 2; i++)
                length = (ushort)((length << 8) | filePtr[filePos++]);
            return length;
        }

        static unsafe bool FindText(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (filePos >= fileLength) 
                    return false;
                if (filePtr[filePos++] != text[i])
                {
                    Crash($"Header issue searching for {text}");
                    return false;
                }
            }
            return true;
        }
    }
}