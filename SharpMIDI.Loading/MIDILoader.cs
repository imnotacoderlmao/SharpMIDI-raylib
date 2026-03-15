#pragma warning disable 8602
using MIDIModificationFramework;
namespace SharpMIDI
{
    static class MIDILoader
    {
        private struct TrackHeapData
        {
            public BigArray<SynthEvent> events;
            public long eventCount;
            public int trackIndex;
        }

        private unsafe struct HeapNode
        {
            public SynthEvent* current;  // direct pointer into track's event array
            public SynthEvent* end;      // pointer to one past last event
        }
        
        private static readonly List<long> trackLocations = new List<long>();
        private static readonly List<uint> trackSizes = new List<uint>();
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
            Console.WriteLine(test);
            throw new Exception();
        }

        public static void LoadMIDI(string path, int tracklimit)
        {   
            if (midiLoaded) UnloadMIDI();
            loadstatus = $"Loading MIDI file: {path}";
            Console.WriteLine(loadstatus);
            midistream = File.Open(path, FileMode.Open);
            VerifyHeader();

            MIDIClock.ppq = ppq;
            filename = Path.GetFileName(path);
            
            loadstatus = $"Indexing MIDI tracks...";
            Console.WriteLine(loadstatus);
            trackAmount = 0;
            loadedtracks = 0;
            while (midistream.Position < midistream.Length)
            {
                if (!IndexTrack()) break;
                loadstatus = $"Indexing MIDI tracks... {trackAmount} found..";
            }
            int actualTrackCount = Math.Min(trackAmount, tracklimit + 1);
            DiskReadProvider threadStream = new DiskReadProvider(midistream);
            unsafe
            {                
                // track properties for merging
                TrackHeapData[] trackDataArray = new TrackHeapData[actualTrackCount];
                if (tracklimit < ushort.MaxValue) 
                    Console.WriteLine($"track limit set to {tracklimit}. will exit early if reached.");
                Parallel.For(0, actualTrackCount, (i) =>
                {
                    Console.Write($"\r{loadstatus}");
                    long estimate = trackSizes[i] / 3;
                    BigArray<SynthEvent> trackEvents = new BigArray<SynthEvent>((ulong)estimate);
                    FastTrack temp = new FastTrack(
                        new BufferByteReader(threadStream, 256*1024, trackLocations[i], trackSizes[i]) 
                    );
                    temp.ParseTrackEvents(trackEvents.Pointer);
                    trackDataArray[i] = new TrackHeapData
                    {
                        events = trackEvents,
                        eventCount = temp.eventCount,
                        trackIndex = i
                    };
                    
                    Renderer.NoteProcessor.ProcessTrackForRendering(
                        trackEvents.Pointer,
                        temp.eventCount,
                        i,
                        temp.trackMaxTick
                    );
                    
                    totalNotes += temp.totalNotes;
                    eventCount += temp.eventCount;
                    loadedtracks++;
                    loadstatus = $"Loading {filename} ({loadedtracks} / {actualTrackCount} tracks, {eventCount} events loaded)";
                    temp.Dispose();
                });
                threadStream.Dispose();
                midistream.Close();
                loadstatus = $"flattening event array";
                Console.WriteLine($"\n{loadstatus}");
                MIDI.synthEvents = MergeAndSort(trackDataArray, actualTrackCount);
                for (int i = 0; i < actualTrackCount; i++)
                {
                    trackDataArray[i].events?.Dispose();
                }
                Renderer.NoteProcessor.FinalizeBuckets();
                
                // dummy events for no bounds checking
                MIDI.temppos.Add(new Tempo { tick = uint.MaxValue });
                MIDI.tickGroups.Add(new TickGroup { tick = uint.MaxValue, count = 0 });
                MIDI.SysEx.Add(new SysEx { tick = uint.MaxValue, message = [] });
            }

            MIDI.tempoEvents = [.. MIDI.temppos];
            MIDI.tickGroupArr = [.. MIDI.tickGroups];
            MIDI.SysExarr = [.. MIDI.SysEx];
            Array.Sort(MIDI.tempoEvents, (a,b) => a.tick.CompareTo(b.tick)); // it was this that fixed the issue. a literal one liner :sob:
            Array.Sort(MIDI.SysExarr, (a,b) => a.tick.CompareTo(b.tick)); 
            MIDI.temppos = null;
            MIDI.SysEx = null; 
            midiLoaded = true;
            loadstatus = filename;
            Console.WriteLine($"Loaded {filename} with {totalNotes} notes loaded from {actualTrackCount} tracks");
        }

        static unsafe BigArray<uint24> MergeAndSort(TrackHeapData[] tracks, int trackCount)
        {   
            BigArray<uint24> messages = new((ulong)eventCount);
            HeapMerge(tracks, trackCount, out MIDI.tickGroups, messages);
            return messages;
        }
         
        static unsafe void HeapMerge(TrackHeapData[] tracks, int trackCount, out List<TickGroup> tickGroups, BigArray<uint24> messages)
        {
            tickGroups = new List<TickGroup>(1024);
            uint24* msgPtr = messages.Pointer;
            long writePos = 0;

            var heap = new PriorityQueue<HeapNode, uint>(trackCount);
            for (int i = 0; i < trackCount; i++)
            {
                if (tracks[i].eventCount > 0)
                {
                    SynthEvent* ptr = tracks[i].events.Pointer;
                    heap.Enqueue(new HeapNode { 
                        current = ptr, 
                        end = ptr + tracks[i].eventCount 
                    }, ptr->tick);
                }
            }

            uint currentTick = uint.MaxValue;
            uint currentCount = 0;

            while (heap.Count > 0)
            {
                var node = heap.Dequeue();
                SynthEvent* ev = node.current;

                if (ev->tick != currentTick)
                {
                    // flush previous group
                    if (currentCount > 0)
                        tickGroups.Add(new TickGroup { tick = currentTick, count = currentCount });
                    currentTick = ev->tick;
                    currentCount = 0;
                }

                msgPtr[writePos++] = ev->message;
                currentCount++;

                node.current++;
                if (node.current < node.end)
                    heap.Enqueue(node, node.current->tick);
            }

            // flush last group
            if (currentCount > 0)
                tickGroups.Add(new TickGroup { tick = currentTick, count = currentCount });
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
            trackLocations.Clear();
            trackSizes.Clear();
            Renderer.NoteProcessor.Cleanup();
            MIDI.synthEvents.Dispose();
            MIDI.tempoEvents = null;
            MIDI.tickGroups = null;
            MIDI.SysEx = [];
            MIDI.SysExarr = null;
            MIDI.temppos = [];
            MIDI.tickGroupArr = null;
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
                trackLocations.Add(midistream.Position);
                trackSizes.Add(size);
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