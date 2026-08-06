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
        public static long loadedNotes = 0;
        public static long eventCount = 0;
        public static int maxTick = 0;
        public static int trackAmount = 0;
        public static int loadedtracks = 0;
        public static volatile bool midiLoaded = false;
        public static string? filename;
        public static string loadstr = "No MIDI Loaded";

        public static int Crash(string error, bool choices = true)
        {        
            if (choices)
            {
                Console.WriteLine($"{error}\nplease input: yes/no to proceed");
                loadstr = error;
                string choice = Console.ReadLine().Trim();
                if (Regex.IsMatch(choice, @"^(yes|y)$", RegexOptions.IgnoreCase))
                {
                    Console.WriteLine("will continue");
                    return 1;
                }
                else
                {
                    loadstr = "Aborted.";
                    Console.WriteLine("Aborted.");
                    return 0;
                }
            }
            else
            {
                Console.WriteLine(error);
                string prevstatus = loadstr;
                loadstr = error;
                Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    if (loadstr == error) 
                        loadstr = prevstatus;
                });
                return 1;
            }
        }

        public static unsafe void LoadMIDI(string path)
        {
            UnloadMIDI();
            filename = Path.GetFileName(path);
            loadstr = $"Loading MIDI file: {filename}";
            Console.WriteLine(loadstr);
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
            double sizemult = WindowManager.trackcolors? 1.01 : 0.76; // +0.01 cause of timing overhead
            loadstr = "intializing memory mapped file";
            using (var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
            using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                byte* basePtr = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    filePtr = basePtr;
                    fileLength = accessor.Capacity;
                    ulong ram_capacity = RamReader.GetTotalMemoryInBytes();
                    if ((accessor.Capacity * sizemult) > (ram_capacity * 0.75))
                    {
                        string crashstr = 
                        $"""
                        this midi is a little big. its expected usage is {Starter.toMemoryText((long)(accessor.Capacity * sizemult))}
                        you have {Starter.toMemoryText((long)ram_capacity)} of ram.
                        your ram as a result will get starved and loading might take a while. continue?
                        """;
                        int ret = Crash(crashstr); 
                        if (ret == 0) return;
                    }
                    loadstr = $"verifying header";
                    VerifyHeader();
                    MIDIClock.ppq = ppq;
                    trackAmount = 0; 
                    loadedtracks = 0;
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
                        loadstr = $"indexing midi tracks.... {trackAmount} found";
                    }

                    BigArray<TickGroup>[]? trackHistogram = new BigArray<TickGroup>[trackAmount];
                    loadstr = $"scanning events for grouping";
                    double parsestart = Timer.Seconds();

                    Parallel.For(0, trackAmount, i =>
                    {
                        byte* trackStartPtr = filePtr + trackProperties[i].start;
                        FastTrack t = new FastTrack(trackStartPtr, trackProperties[i].len);
                        trackHistogram[i] = t.ScanEvents();
                        Interlocked.Add(ref eventCount, t.eventCount);
                        Interlocked.Add(ref totalNotes, t.totalNotes);
                        Interlocked.Increment(ref loadedtracks);
                        loadstr = $"counted {loadedtracks:N0} / {trackAmount:N0} tracks | {totalNotes:N0} notes counted";
                        t.Dispose();
                    });
                    
                    double parseend = Timer.Seconds();
                    counttime = parseend - parsestart;
                    
                    loadstr = $"flattening timing array";
                    BigArray<TickIndex> tickgroup = new BigArray<TickIndex>(maxTick + 2);
                    for (int i = 0; i < trackAmount; i++)
                    {
                        BigArray<TickGroup> list = trackHistogram[i];
                        if (list == null) continue;
                        for (int j = 0; j < list.Count; j++)
                        {
                            ref TickGroup g = ref list.Pointer[j];
                            ref TickIndex global = ref tickgroup.Pointer[g.tick];
                            g.destBase = global.offset;
                            global.offset += g.offset;
                            global.notecount += g.notecount;
                        }
                    }

                    long event_offset = 0;
                    for (int t = 0; t <= maxTick; t++)
                    {
                        long tickEventCount = tickgroup.Pointer[t].offset;
                        tickgroup.Pointer[t] = new TickIndex 
                        { 
                            notecount = tickgroup.Pointer[t].notecount, 
                            offset = event_offset 
                        };
                        event_offset += tickEventCount;
                    }

                    BigArray<long>[] trackBases = new BigArray<long>[trackAmount];
                    Parallel.For(0, trackAmount, i =>
                    {
                        BigArray<TickGroup> list = trackHistogram[i];
                        if (list == null) return;
                        var bases = new BigArray<long>(list.Count);
                        for (int j = 0; j < list.Count; j++)
                            bases.Pointer[j] = list.Pointer[j].destBase + tickgroup.Pointer[list.Pointer[j].tick].offset;
                        bases.Count = list.Count;
                        trackBases[i] = bases;
                        list.Dispose();
                    });

                    SynthEvent.Alloc(eventCount, WindowManager.trackcolors);
                    uint24* msgPtr = SynthEvent.messages.Pointer;
                    byte* trackPtr = WindowManager.trackcolors ? SynthEvent.track.Pointer : null;

                    loadedtracks = 0;
                    loadedNotes = 0;
                    parsestart = Timer.Seconds();

                    Parallel.For(0, trackAmount, i =>
                    {
                        byte* trackStartPtr = filePtr + trackProperties[i].start;
                        FastTrack t = new FastTrack(trackStartPtr, trackProperties[i].len);
                        // shift track left by 4 so keyheader access can be track | channel directly (please speed i need this for 1 less clock cycle needed)
                        t.ParseTrackEvents(trackBases[i], msgPtr, trackPtr, (byte)(i << 4));
                        Interlocked.Add(ref loadedNotes, t.totalNotes);
                        Interlocked.Increment(ref loadedtracks);
                        trackHistogram[i]?.Dispose();
                        loadstr = $"parsed {loadedtracks:N0} / {trackAmount:N0} tracks | {loadedNotes:N0} / {totalNotes:N0} notes parsed";
                        t.Dispose();
                    });

                    parseend = Timer.Seconds();
                    loadstr = "doing stuff to tempo and sysex arrays";
                    tickgroup.Pointer[maxTick + 1] = new TickIndex { notecount = 0, offset = event_offset };
                    MIDIEvent.TickIndexArray = tickgroup;
                    parsetime = parseend - parsestart;
                }
                finally
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    filePtr = null;
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
            int tempolen = MIDIEvent.TempoEventArray.Length - 1;
            int sysexlen = MIDIEvent.SysExArray.Length - 1;
            Console.WriteLine(
                ParseStatistics(fileLength, tempolen, sysexlen, counttime, parsetime, sizemult, filename)
            );
            Console.WriteLine($"\nparsing finished!! awaiting renderer.");            
            midiLoaded = true;
            loadstr = filename;
            GLNoteRenderer.InitializeForMIDI();
            Console.WriteLine("renderer initialization finished!! awaiting playback.");
        }

        public static void UnloadMIDI()
        {
            if (!midiLoaded) return;
            trackAmount = 0;
            trackProperties.Clear();
            loadstr = $"No MIDI Loaded";
            Console.WriteLine($"unloading {filename}");
            midiLoaded = false;
            MIDIPlayer.stopping = true;
            GLNoteRenderer.ResetForUnload();
            totalNotes = 0;
            eventCount = 0;
            maxTick = 0;
            SynthEvent.Dispose();
            MIDIEvent.TickIndexArray.Dispose();
            MIDIEvent.TempoEventArray = [];
            MIDIEvent.SysExArray = [];
            Console.WriteLine($"succesfully unloaded {filename}");
            GC.Collect();
        }

        static unsafe string ParseStatistics(long filesize, int tempolen, int sysexlen, double counttime, double parsetime, double sizemult, string filename)
        {
            string parsestatistics =
            $"""
            ============== PARSE STATICICS THATS ACTUALLY FANCY ==============
              Filename:  {filename}
              Filesize:  {Starter.toMemoryText(filesize)}
              Took:
                  Counting: {counttime:N12}s. which is {(double)(loadedNotes / counttime):N0} notes/s.
                  Parsing:  {parsetime:N12}s. which is {(double)(loadedNotes / parsetime):N0} notes/s.
              Counted:
                  MIDI Ticks:              {maxTick:N0}
                  Total Channel Events:    {eventCount:N0}
                  Notes:                   {loadedNotes:N0}
                  Tempo Events:            {tempolen:N0}
                  SysEx Events:            {sysexlen:N0}
              Memory Usage:
                  Current:        {Starter.toMemoryText(Process.GetCurrentProcess().WorkingSet64)}
                  Expected:       {Starter.toMemoryText((long)(filesize * sizemult))}
                  Channel Events: {Starter.toMemoryText(eventCount * sizeof(uint24))}
                  Track Indexing: {Starter.toMemoryText((WindowManager.trackcolors? (eventCount * sizeof(byte)) : 0))}
                  Tempo Events:   {Starter.toMemoryText(tempolen * sizeof(Tempo))}
                  Timing:         {Starter.toMemoryText((long)(maxTick + 2) * sizeof(TickIndex))}
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