using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SharpMIDI
{
    static class MIDILoader
    {   
        private struct TrackProperties
        {
            public long start;
            public uint len;
        }

        private static readonly List<TrackProperties> trackProperties = [];
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

        public static int Crash(string error, bool choices = true)
        {        
            if (choices)
            {
                Console.WriteLine($"{error}\nplease input: yes/no to proceed");
                loadstatus = error;
                string choice = Console.ReadLine().Trim();
                if (Regex.IsMatch(choice, @"^(yes|y)$", RegexOptions.IgnoreCase))
                {
                    Console.WriteLine("will continue");
                    return 1;
                }
                else
                {
                    loadstatus = "Aborted.";
                    Console.WriteLine("Aborted.");
                    return 0;
                }
            }
            else
            {
                Console.WriteLine(error);
                string prevstatus = loadstatus;
                loadstatus = error;
                Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    if (loadstatus == error) 
                        loadstatus = prevstatus;
                });
                return 1;
            }
        }

        public static unsafe void LoadMIDI(string path)
        {
            UnloadMIDI();
            filename = Path.GetFileName(path);
            loadstatus = $"Loading MIDI file: {filename}";
            Console.WriteLine(loadstatus);
            if (!path.EndsWith(".mid"))
            { 
                int ret = Crash("file doesn't end with 'mid'. are you even loading a midi file?");
                if (ret == 0) return;
            }
            filePos = 0;
            fileLength = 0;
            string memusage = string.Empty;
            double counttime;
            double parsetime;
            using (var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
            using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                byte* basePtr = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    filePtr = basePtr;
                    fileLength = accessor.Capacity;

                    if (accessor.Capacity > 34_359_738_368) // or 32 GiB somethig something
                        Crash("this midi is a little big. your ram might get starved and loading might take a while. continue?");

                    loadstatus = $"verifying header";
                    VerifyHeader();
                    MIDIClock.ppq = ppq;
                    trackAmount = 0; 
                    loadedtracks = 0;
                    loadstatus = $"Indexing MIDI tracks...";

                    while (filePos < fileLength)
                    {
                        int ret = IndexTrack();
                        if (ret == 0) 
                            break;
                        else if (ret == 2)
                        {
                            Console.WriteLine("aborting....");
                            UnloadMIDI();
                            return;
                        }
                        loadstatus = $"Indexing MIDI tracks... {trackAmount} found..";
                    }

                    BigArray<TickGroup>[]? trackHistogram = new BigArray<TickGroup>[trackAmount];
                    loadstatus = $"scanning events for grouping";
                    long countednotes = 0;
                    double parsestart = Timer.Seconds();

                    Parallel.For(0, trackAmount, i =>
                    {
                        byte* trackStartPtr = filePtr + trackProperties[i].start;
                        FastTrack t = new FastTrack(trackStartPtr, trackProperties[i].len);
                        trackHistogram[i] = t.ScanEvents();
                        Interlocked.Add(ref eventCount, t.eventCount);
                        Interlocked.Add(ref countednotes, t.totalNotes);
                        Interlocked.Increment(ref loadedtracks);
                        Console.Write($"\rcounted {loadedtracks}/{trackAmount} tracks | total nc: {countednotes:N0}");
                        t.Dispose();
                    });
                    
                    double parseend = Timer.Seconds();
                    counttime = parseend - parsestart;
                    
                    BigArray<long> writeCursors = new BigArray<long>(maxTick + 2);
                    BigArray<TickGroup> tickgroup = new BigArray<TickGroup>(maxTick + 2);
                    
                    for (int i = 0; i < trackAmount; i++)
                    {
                        BigArray<TickGroup> list = trackHistogram[i];
                        if (list == null) continue;
                        for (int j = 0; j < list.Count; j++)
                        {
                            TickGroup g = list.Pointer[j];
                            tickgroup.Pointer[g.tick].offset += g.offset;
                            tickgroup.Pointer[g.tick].notecount += g.notecount;
                        }
                        list.Dispose();
                    }
                    trackHistogram = null;
                    
                    long event_offset = 0;
                    for (int t = 0; t <= maxTick; t++)
                    {
                        writeCursors.Pointer[t] = event_offset;
                        long tickEventCount = tickgroup.Pointer[t].offset;
                        tickgroup.Pointer[t] = new TickGroup 
                        { 
                            tick = t, 
                            notecount = tickgroup.Pointer[t].notecount, 
                            offset = event_offset
                        };
                        event_offset += tickEventCount;
                    }

                    SynthEvent.Alloc(eventCount, WindowManager.trackcolors);
                    uint24* msgPtr = SynthEvent.messages.Pointer;
                    byte* trackPtr = WindowManager.trackcolors ? SynthEvent.track.Pointer : null;
                    long* writeCursorsptr = writeCursors.Pointer;

                    loadstatus = $"actually parsing events now";
                    Console.WriteLine($"\n{loadstatus}");
                    loadedtracks = 0;
                    totalNotes = 0;
                    parsestart = Timer.Seconds();

                    Parallel.For(0, trackAmount, i =>
                    {
                        byte* trackStartPtr = filePtr + trackProperties[i].start;
                        FastTrack t = new FastTrack(trackStartPtr, trackProperties[i].len);
                        t.ParseTrackEvents(msgPtr, trackPtr, writeCursorsptr, (byte)i);
                        Interlocked.Add(ref totalNotes, t.totalNotes);
                        Interlocked.Increment(ref loadedtracks);
                        Console.Write($"\rparsed {loadedtracks} tracks | ({totalNotes:N0} notes parsed)");
                        t.Dispose();
                    });

                    parseend = Timer.Seconds();
                    tickgroup.Pointer[maxTick + 1] = new TickGroup { tick = int.MaxValue, notecount = 0, offset = event_offset };
                    MIDIEvent.TickGroupArray = tickgroup;
                    writeCursors.Dispose();
                    parsetime = parseend - parsestart;
                }
                finally
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    filePtr = null;
                }
            }
            Console.WriteLine("\ndoing stuff to tempo and sysex array first..");
            tempMIDIstorage.temppos.Add(new Tempo { tick = int.MaxValue, tempo = 500000 });
            tempMIDIstorage.SysEx.Add(new SysEx { tick = int.MaxValue, message = [] });
            MIDIEvent.TempoEventArray = [.. tempMIDIstorage.temppos];
            MIDIEvent.SysExArray = [.. tempMIDIstorage.SysEx];
            Array.Sort(MIDIEvent.TempoEventArray, (a, b) => a.tick.CompareTo(b.tick));
            Array.Sort(MIDIEvent.SysExArray, (a, b) => a.tick.CompareTo(b.tick));
            tempMIDIstorage.temppos.Clear();
            tempMIDIstorage.SysEx.Clear();
            int tempolen = MIDIEvent.TempoEventArray.Length - 1;
            int sysexlen = MIDIEvent.SysExArray.Length - 1;
            Console.WriteLine(
                ParseStatistics(fileLength, tempolen, sysexlen, counttime, parsetime, filename)
            );
            Console.WriteLine("parsing finished!! awaiting renderer.");            
            midiLoaded = true;
            loadstatus = filename;
            GLNoteRenderer.InitializeForMIDI();
            Console.WriteLine("renderer initialization finished!! awaiting playback.");
        }

        public static void UnloadMIDI()
        {
            if (!midiLoaded) return;
            trackAmount = 0;
            trackProperties.Clear();
            loadstatus = $"No MIDI Loaded";
            Console.WriteLine($"unloading {filename}");
            midiLoaded = false;
            MIDIPlayer.stopping = true;
            GLNoteRenderer.ResetForUnload();
            totalNotes = 0;
            eventCount = 0;
            maxTick = 0;
            SynthEvent.Dispose();
            MIDIEvent.TickGroupArray.Dispose();
            MIDIEvent.TempoEventArray = [];
            MIDIEvent.SysExArray = [];
            Console.WriteLine($"succesfully unloaded {filename}");
            GC.Collect();
        }

        static unsafe string ParseStatistics(long filesize, int tempolen, int sysexlen, double counttime, double parsetime, string filename)
        {
            double sizemult = WindowManager.trackcolors? 1 : 0.75;
            long timingbytes = (long)(maxTick + 2) * sizeof(TickGroup);
            string parsestatistics =
            $"""
            ============== PARSE STATICICS THATS ACTUALLY FANCY ==============
              Filename:  {filename}
              Filesize:  {Starter.toMemoryText(filesize)}
              Took:
                  Counting: {counttime:N12}s. which is {(double)(totalNotes / counttime):N0} notes/s.
                  Parsing:  {parsetime:N12}s. which is {(double)(totalNotes / parsetime):N0} notes/s.
              Counted:
                  MIDI Ticks:              {maxTick:N0}
                  Total Channel Events:    {eventCount:N0}
                  Notes:                   {totalNotes:N0}
                  Tempo Events:            {tempolen:N0}
                  SysEx Events:            {sysexlen:N0}
              Memory Usage:
                  Current:        {Starter.toMemoryText(Process.GetCurrentProcess().WorkingSet64)}
                  Expected:       {Starter.toMemoryText((long)(filesize * sizemult))}
                  Channel Events: {Starter.toMemoryText(eventCount * sizeof(uint24))}
                  Track Indexing: {Starter.toMemoryText((WindowManager.trackcolors? (eventCount * sizeof(byte)) : 0))}
                  Tempo Events:   {Starter.toMemoryText(tempolen * sizeof(Tempo))}
                  Timing:         {Starter.toMemoryText(timingbytes)}
              MIDI to RAM ratio:  {Process.GetCurrentProcess().WorkingSet64 / (double)filesize}x
            ==================================================================
            """;
            return parsestatistics;
        }

        static uint headersize = 0; 
        static uint fmt = 0;
        static uint ppq = 0;

        static void VerifyHeader()
        {
            if (FindText("MThd") == 1)
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

        static int IndexTrack()
        {
            int ret = FindText("MTrk");
            if (ret == 1)
            {
                uint size = ReadUInt32();
                trackProperties.Add(new TrackProperties
                {
                    start = filePos,
                    len = size
                });
                filePos += size;
                trackAmount++;
                return ret;
            }
            else if (ret == 2)
            {
                int ret2 = Crash("Your MIDI file might be corrupted. are you sure you want to continue parsing?");
                if (ret2 == 1)
                    return 0;
            }
            return ret;
        }

        static unsafe uint ReadUInt32()
        {
            uint length = 0;
            Unsafe.CopyBlock(&length, filePtr + filePos, 4);
            filePos += 4;
            return BinaryPrimitives.ReverseEndianness(length);
        }

        static unsafe ushort ReadUInt16()
        {
            ushort length = 0;
            Unsafe.CopyBlock(&length, filePtr + filePos, 2);
            filePos += 2;
            return BinaryPrimitives.ReverseEndianness(length);
        }

        static unsafe int FindText(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (filePos >= fileLength)
                    return 0;
                if (filePtr[filePos++] != text[i])
                {
                    Console.WriteLine($"Header issue searching for {text}");
                    return 2;
                }
            }
            return 1;
        }
    }
}