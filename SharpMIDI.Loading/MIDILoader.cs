#pragma warning disable 8602
using MIDIModificationFramework;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
        static Stream midistream;
        public static long totalNotes = 0;
        public static long eventCount = 0;
        public static int maxTick = 0;
        public static int trackAmount = 0;
        public static int loadedtracks = 0;
        static uint headersize = 0; 
        static uint fmt = 0;
        static uint ppq = 0;
        static bool success;
        public static bool midiLoaded = false;
        public static string? filename;
        public static string loadstatus = "No MIDI Loaded";

        static void Crash(string test)
        {
            loadstatus = test;
            Console.WriteLine(loadstatus);
            throw new Exception();
        }

        public static void LoadMIDI(string path)
        {
            UnloadMIDI();
            filename = Path.GetFileName(path);
            loadstatus = $"Loading MIDI file: {filename}";
            if (!path.EndsWith(".mid"))
            { 
                loadstatus = "file dosent end with 'mid'. are you even loading a midi file?";
                return;
            }
            midistream = File.Open(path, FileMode.Open);
            loadstatus = $"verifying header";
            VerifyHeader();
            MIDIClock.ppq = ppq;
            filename = Path.GetFileName(path);

            trackAmount = 0; 
            loadedtracks = 0;
            loadstatus = $"Indexing MIDI tracks...";
            while (midistream.Position < midistream.Length)
            {
                if (!IndexTrack()) break;
                loadstatus = $"Indexing MIDI tracks... {trackAmount} found..";
            }
            DiskReadProvider threadStream = new DiskReadProvider(midistream);
            unsafe
            {
                List<TickGroup> histogram = new();
                loadstatus = $"scanning events for grouping";
                // this will very severely overallocate. for now ill just let it since it wont be as big of an allocation as the events itself
                // plus itll get freed after building the actual tickgroup, it does become a problem at huge track counts though
                for (int i = 0; i < trackAmount; i++)
                {
                    FastTrack t = new FastTrack(new BufferByteReader(threadStream, 64*1024, trackProperties[i].start, trackProperties[i].len));
                    t.ScanEvents(histogram);
                    eventCount += t.eventCount;
                    Console.WriteLine($"scanned track {i}/{trackAmount} event count = {t.eventCount}, total = {eventCount}");
                    t.Dispose();
                }
                histogram.TrimExcess();
                Console.WriteLine($"finished scanning {trackAmount} tracks. building tick groups for events");
                long[] writeCursors = new long[maxTick + 2];
                TickGroup[] tickgroup = new TickGroup[maxTick + 2];
                histogram.Sort((a, b) => a.tick.CompareTo(b.tick));
                long running = 0;
                uint count = 0;
                int histIdx = 0;
                for (int t = 0; t <= maxTick; t++)
                {
                    writeCursors[t] = running;
                    while (histIdx < histogram.Count && histogram[histIdx].tick == t)
                    {
                        count += histogram[histIdx].count;
                        histIdx++;
                    }
                    tickgroup[t] = new TickGroup { tick = (uint)t, count = count, offset = running };
                    running += count;
                    count = 0;
                }
                histogram = null;
                Console.WriteLine($"eventcount = {eventCount} now that sums of events per tick are prefixed");
                BigArray<MIDIEvent> messages = new BigArray<MIDIEvent>((ulong)eventCount);
                MIDIEvent* msgPtr = messages.Pointer;
                loadstatus = $"actually parsing events now";
                Parallel.For(0, trackAmount, i =>
                {
                    fixed (long* wc = writeCursors)
                    {
                        FastTrack t = new FastTrack(new BufferByteReader(threadStream, 256*1024, trackProperties[i].start, trackProperties[i].len));
                        t.ParseTrackEvents(msgPtr, wc, (ushort)i);
                        totalNotes += t.totalNotes;
                        loadedtracks++;
                        loadstatus = $"Loading {filename} ({loadedtracks}/{trackAmount} tracks, {totalNotes} notes loaded)";
                        Console.Write($"\r{loadstatus}");
                        t.Dispose();
                    }
                });
                threadStream.Dispose();
                midistream.Close();
                tickgroup[maxTick + 1] = new TickGroup { tick = uint.MaxValue, count = 0, offset = running };
                MIDI.MIDIEventArray = messages;
                MIDI.TickGroupArray = tickgroup;
                MIDIRenderer.InitializeForMIDI();
                Console.WriteLine($"\nLoaded {filename} with {totalNotes} notes loaded from {trackAmount} tracks");
                string memusage = Starter.toMemoryText(Process.GetCurrentProcess().WorkingSet64);
                string eventmemusage = Starter.toMemoryText((long)MIDI.MIDIEventArray.Length * sizeof(uint));
                string timingmemusage = Starter.toMemoryText((long)MIDI.TickGroupArray.Length * sizeof(uint));
                Console.WriteLine($"current memory usage: {memusage} | events: {eventmemusage}\ntiming: {timingmemusage}");
            }

            tempMIDIstorage.temppos.Add(new Tempo { tick = uint.MaxValue });
            tempMIDIstorage.SysEx.Add(new SysEx { tick = uint.MaxValue, message = [] });
            MIDI.TempoEventArray = [.. tempMIDIstorage.temppos];
            MIDI.SysExArray = [.. tempMIDIstorage.SysEx];
            Array.Sort(MIDI.TempoEventArray, (a, b) => a.tick.CompareTo(b.tick));
            Array.Sort(MIDI.SysExArray, (a, b) => a.tick.CompareTo(b.tick));
            tempMIDIstorage.temppos = null;
            tempMIDIstorage.SysEx = null;
            midiLoaded = true;
            loadstatus = filename;
        }

        public static void UnloadMIDI()
        {
            if (!midiLoaded) return;
            loadstatus = $"unloading {filename}";
            Console.WriteLine(loadstatus);
            MIDIPlayer.stopping = true;
            midiLoaded = false;
            totalNotes = 0;
            eventCount = 0;
            maxTick = 0;
            trackAmount = 0;
            trackProperties.Clear();
            MIDIRenderer.ResetForUnload();
            tempMIDIstorage.SysEx = [];
            tempMIDIstorage.temppos = [];
            MIDI.MIDIEventArray?.Dispose();
            MIDI.TempoEventArray = null;
            MIDI.SysExArray = null;
            MIDI.TickGroupArray = null;
            loadstatus = $"No MIDI Loaded";
            GC.Collect();
        }

        static uint VerifyHeader()
        {
            success = FindText("MThd");
            if (success)
            {
                headersize = ReadUInt32();
                fmt = ReadUInt16();
                midistream.Seek(midistream.Position + 2, SeekOrigin.Begin);
                ppq = ReadUInt16();
                if (fmt == 2)
                    Crash("MIDI format 2 unsupported");
                if (ppq < 0)
                    Crash("PPQ is negative");
                if (headersize != 6)
                    Crash($"Incorrect header size of {headersize}");
                return headersize;
            }
            else
            {
                Crash("Header issue");
                return 0;
            }
        }

        static bool IndexTrack()
        {
            bool success = FindText("MTrk");
            if (success)
            {
                uint size = ReadUInt32();
                trackProperties.Add(new TrackProperties
                {
                    start = midistream.Position,
                    len = size
                });
                midistream.Position += size;
                trackAmount++;
                return true;
            }
            else
            {
                return false;
            }
        }

        static uint ReadUInt32()
        {
            uint length = 0;
            for (int i = 0; i != 4; i++)
                length = (length << 8) | (byte)midistream.ReadByte();
            return length;
        }

        static ushort ReadUInt16()
        {
            ushort length = 0;
            for (int i = 0; i != 2; i++)
                length = (ushort)((length << 8) | (byte)midistream.ReadByte());
            return length;
        }

        static bool FindText(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                int test = midistream.ReadByte();
                if (test != text[i])
                {
                    if(test == -1){
                        return false;
                    } else {
                        Crash($"Header issue searching for {text}");
                    }
                }
            }
            return true;
        }
    }
}
