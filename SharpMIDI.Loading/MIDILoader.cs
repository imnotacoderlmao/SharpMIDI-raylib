#pragma warning disable 8602
using SharpMIDI.Renderer;

namespace SharpMIDI
{
    static class MIDILoader
    {
        struct TrackQueueHeap
        {
            public long value;
            public int track;
        }
        private static List<long> trackLocations = new List<long>();
        private static List<uint> trackSizes = new List<uint>();
        static Stream? midistream;
        public static long totalNotes = 0;
        public static long loadedNotes = 0;
        public static long eventCount = 0;
        public static int maxTick = 0;
        public static int trackAmount = 0;
        public static bool loaded = false;
        static uint headersize = 0; 
        static uint fmt = 0;
        static uint ppq = 0;
        static bool success;

        static void Crash(string test)
        {
            MessageBox.Show(test);
            throw new Exception();
        }

        public static async Task LoadPath(string path, byte thres, int tracklimit)
        {   
            midistream = File.OpenRead(path);
            VerifyHeader();

            MIDIClock.ppq = ppq;
            Starter.form.label6.Text = $"PPQ: {ppq}";
            Starter.form.label10.Text = $"Loaded tracks: 0 / ???";

            Console.WriteLine("Indexing MIDI tracks...");
            trackAmount = 0;
            while (midistream.Position < midistream.Length)
            {
                bool success = IndexTrack();
                Starter.form.label10.Text = $"Loaded tracks: 0 / {trackAmount}";
                if (!success)
                    break;
            }
            
            List<long>[] tempLists = new List<long>[trackAmount];
            for (int i = 0; i < trackAmount; i++)
            {
                tempLists[i] = new List<long>();
            }
            
            midistream.Position++;
            int totaltracks = 0;
            Parallel.For(0, trackAmount, (i) =>
            {
                if (i > tracklimit) return;
                Console.WriteLine($"Loading track #{i + 1} | Size {trackSizes[i]}");
                
                // this shouldntve worked at all, but it deadass makes >2gb track loading possible
                int bufSize = (int)Math.Min(int.MaxValue, trackSizes[i]/4);
                
                FastTrack temp = new FastTrack(
                    new BufferByteReader(midistream, bufSize, trackLocations[i], trackSizes[i])
                );
                temp.ParseTrackEvents(thres, tempLists[i]);
                
                // update counters
                Interlocked.Add(ref loadedNotes, temp.loadedNotes);
                Interlocked.Add(ref totalNotes, temp.totalNotes);
                Interlocked.Add(ref eventCount, temp.eventAmount);
                totaltracks++;
                
                Starter.form.label10.Text = $"Loaded tracks: {totaltracks} / {trackAmount}";
                Starter.form.label5.Text = $"Notes: {loadedNotes} / {totalNotes}";
                temp.Dispose();
            });
            midistream.Close();

            Console.WriteLine("merging events to one array");
            MIDI.synthEvents = new BigArray<long>((ulong)eventCount);
            unsafe
            {
                MergeAllTracks(tempLists, MIDI.synthEvents);
            }
            
            MIDI.tempoEvents = [.. MIDI.temppos];
            MIDI.temppos.Clear();
            
            Starter.form.label2.Text = "Status: Loaded";
            
            Starter.form.button4.Enabled = true;
            Console.WriteLine("MIDI Loaded");
            MIDIRenderer.InitializeForMIDI();
            loaded = true;
        }
        
        public static void Unload()
        {
            loaded = false;
            totalNotes = 0;
            loadedNotes = 0;
            eventCount = 0;
            maxTick = 0;
            trackAmount = 0;
            trackLocations.Clear();
            trackSizes.Clear();
            MIDIRenderer.ResetForUnload();
            MIDI.synthEvents.Dispose();
            MIDI.tempoEvents = null;
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

        public static unsafe void MergeAllTracks(List<long>[] trackEvents, BigArray<long> output)
        {
            int trackCount = trackEvents.Length;
            ulong outPos = 0;

            // Per-track index
            int[] idx = new int[trackCount];

            // Heap (max tracks is small)
            TrackQueueHeap[] heap = new TrackQueueHeap[trackCount];
            int heapSize = 0;

            // Build initial heap
            for (int t = 0; t < trackCount; t++)
            {
                var list = trackEvents[t];
                if (list != null && list.Count != 0)
                {
                    heap[heapSize++] = new TrackQueueHeap
                    {
                        value = list[0],
                        track = t
                    };
                }
            }

            // Heapify
            for (int i = (heapSize >> 1) - 1; i >= 0; i--)
                SiftDown(heap, heapSize, i);

            long* outPtr = output.Pointer;

            while (heapSize > 0)
            {
                // Pop smallest
                var top = heap[0];
                outPtr[outPos++] = top.value;

                int t = top.track;
                int next = ++idx[t];

                var list = trackEvents[t];
                if (next < list.Count)
                {
                    heap[0].value = list[next];
                    heap[0].track = t;
                }
                else
                {
                    heap[0] = heap[--heapSize];
                    trackEvents[t] = null;
                }

                if (heapSize > 0)
                    SiftDown(heap, heapSize, 0);
            }
        }
        
        static void SiftDown(TrackQueueHeap[] heap, int size, int i)
        {
            while (true)
            {
                int left = (i << 1) + 1;
                if (left >= size) return;

                int right = left + 1;
                int smallest = left;

                if (right < size && heap[right].value < heap[left].value)
                    smallest = right;

                if (heap[i].value <= heap[smallest].value)
                    return;

                (heap[i], heap[smallest]) = (heap[smallest], heap[i]);
                i = smallest;
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
            foreach (char l in text)
            {
                int test = midistream.ReadByte();
                if (test != l)
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