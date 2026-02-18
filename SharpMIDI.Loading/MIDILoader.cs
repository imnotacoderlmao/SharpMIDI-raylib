#pragma warning disable 8602
using System.Runtime.InteropServices;

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

        private struct HeapNode
        {
            public int trackIndex;
            public long position;
        }
        
        private static List<long> trackLocations = new List<long>();
        private static List<uint> trackSizes = new List<uint>();
        static Stream? midistream;
        public static long totalNotes = 0;
        public static long loadedNotes = 0;
        public static long eventCount = 0;
        public static int maxTick = 0;
        public static int trackAmount = 0;
        public static int loadedtracks = 0;
        static uint headersize = 0; 
        static uint fmt = 0;
        static uint ppq = 0;
        static bool success;

        static void Crash(string test)
        {
            MessageBox.Show(test);
            throw new Exception();
        }

        public static void LoadPath(string path, byte thres, int tracklimit)
        {   
            midistream = File.Open(path, FileMode.Open);
            VerifyHeader();

            MIDIClock.ppq = ppq;
            Starter.form.label6.Text = $"PPQ: {ppq}";

            Console.WriteLine("Indexing MIDI tracks...");
            trackAmount = 0;
            loadedtracks = 0;
            while (midistream.Position < midistream.Length)
            {
                if (!IndexTrack()) break;
            }

            unsafe
            {
                int actualTrackCount = Math.Min(trackAmount, tracklimit + 1);
                
                // track properties for merging
                TrackHeapData[] trackDataArray = new TrackHeapData[actualTrackCount];
                
                midistream.Position++;
                if (tracklimit < ushort.MaxValue) 
                    Console.WriteLine($"track limit set to {tracklimit}. will exit early if reached.");
                Parallel.For(0, actualTrackCount, (i) =>
                {
                    Console.Write($"\rLoading track #{i + 1} | Size {trackSizes[i]} bytes ({loadedtracks + 1} / {actualTrackCount} tracks loaded)");

                    FastTrack temp = new FastTrack(
                        new BufferByteReader(midistream, 256 * 1024, trackLocations[i], trackSizes[i]) 
                    );

                    long estimate = Math.Max(trackSizes[i] / 4, 64);
                    BigArray<SynthEvent> trackEvents = new BigArray<SynthEvent>((ulong)estimate);
                    temp.ParseTrackEvents(thres, trackEvents.Pointer);
                    if ((ulong)temp.eventAmount < trackEvents.Length)
                    {
                        trackEvents.Resize((ulong)temp.eventAmount);
                    }
                    
                    trackDataArray[i] = new TrackHeapData
                    {
                        events = trackEvents,
                        eventCount = temp.eventAmount,
                        trackIndex = i
                    };
                    
                    Renderer.NoteProcessor.ProcessTrackForRendering(
                        trackEvents.Pointer,
                        temp.eventAmount,
                        i,
                        temp.trackMaxTick
                    );
                    
                    loadedNotes += temp.loadedNotes;
                    totalNotes += temp.totalNotes;
                    eventCount += temp.eventAmount;
                    loadedtracks++;
                    temp.Dispose();
                });
                
                midistream.Close();

                Console.WriteLine($"\nflattening event array");
                MIDI.synthEvents = MergeAndSort(trackDataArray, actualTrackCount);
                for (int i = 0; i < actualTrackCount; i++)
                {
                    trackDataArray[i].events?.Dispose();
                }
                Renderer.NoteProcessor.FinalizeBuckets();
                
                // dummy events for no bounds checking
                MIDI.temppos.Add(new Tempo { tick = uint.MaxValue });
                unsafe
                {
                    SynthEvent* eventsPtr = MIDI.synthEvents.Pointer;
                    eventsPtr[eventCount] = new SynthEvent { tick = uint.MaxValue };
                }
            }

            MIDI.tempoEvents = [.. MIDI.temppos];
            Array.Sort(MIDI.tempoEvents, (a,b) => a.tick.CompareTo(b.tick)); // it was this that fixed the issue. a literal one liner :sob:
            MIDI.temppos = null;
            Starter.form.label2.Text = "Status: Loaded";
            Starter.form.button4.Enabled = true;
            Console.WriteLine("MIDI Loaded");
        }

        static unsafe BigArray<SynthEvent> MergeAndSort(TrackHeapData[] tracks, int trackCount)
        {
            long totalEvents = 0;
            for (int i = 0; i < trackCount; i++)
            {
                if (tracks[i].eventCount > 0)
                {
                    totalEvents += tracks[i].eventCount;
                }
            }
            
            BigArray<SynthEvent> result = new BigArray<SynthEvent>((ulong)totalEvents + 1);
            SynthEvent* resultPtr = result.Pointer;
            HeapMerge(tracks, trackCount, resultPtr, totalEvents);
            
            eventCount = totalEvents;
            return result;
        }
        
        static unsafe void HeapMerge(TrackHeapData[] tracks, int trackCount, SynthEvent* dest, long totalEvents)
        {
            // min-heap of (tick, trackIndex, position)
            var heap = new PriorityQueue<HeapNode, uint>();
            
            // initialize heap with first event from each non-empty track
            for (int i = 0; i < trackCount; i++)
            {
                if (tracks[i].eventCount > 0)
                {
                    SynthEvent firstEvent = tracks[i].events.Pointer[0];
                    heap.Enqueue(new HeapNode { trackIndex = i, position = 0 }, firstEvent.tick);
                }
            }
            
            long writePos = 0;
            
            while (heap.Count > 0)
            {
                var node = heap.Dequeue();
                int trackIdx = node.trackIndex;
                long pos = node.position;
                dest[writePos++] = tracks[trackIdx].events.Pointer[pos];
                
                // add next event from same track if available
                long nextPos = pos + 1;
                if (nextPos < tracks[trackIdx].eventCount)
                {
                    SynthEvent nextEvent = tracks[trackIdx].events.Pointer[nextPos];
                    heap.Enqueue(new HeapNode { trackIndex = trackIdx, position = nextPos }, nextEvent.tick);
                }
            }
        }

        public static void Unload()
        {
            MIDIPlayer.stopping = true;
            totalNotes = 0;
            loadedNotes = 0;
            eventCount = 0;
            maxTick = 0;
            trackAmount = 0;
            trackLocations.Clear();
            trackSizes.Clear();
            MIDI.synthEvents.Dispose();
            MIDI.tempoEvents = null;
            MIDI.temppos = [];
            GC.Collect();
        }

        static uint VerifyHeader()
        {
            success = FindText("MThd");
            if (success)
            {
                headersize = ReadUInt32();
                fmt = ReadInt16();
                midistream.Seek(midistream.Position + 2, SeekOrigin.Begin);
                ppq = ReadInt16();
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

        static ushort ReadInt16()
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