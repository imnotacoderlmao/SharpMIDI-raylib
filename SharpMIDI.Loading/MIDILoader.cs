#pragma warning disable 8602
namespace SharpMIDI
{
    static class MIDILoader
    {
        private static List<long> trackLocations = new List<long>();
        private static List<uint> trackSizes = new List<uint>();
        static Stream? midistream;
        public static long totalNotes = 0;
        public static long loadedNotes = 0;
        public static long eventCount = 0;
        public static int maxTick = 0;
        public static int trackAmount = 0;

        static void Crash(string test)
        {
            MessageBox.Show(test);
            throw new Exception();
        }

        public static async void LoadPath(string path, byte thres, int tracklimit)
        {
            midistream = File.OpenRead(path);
            
            bool find = FindText("MThd");
            if (!find) 
                Crash("Unexpected EoF searching for MThd"); 
            
            uint size = ReadInt32();
            int fmt = ReadInt16();
            int tracks = ReadInt16();
            int ppq = ReadInt16();

            if (size != 6)
                Crash($"Incorrect header size of {size}");
            if (fmt == 2)
                Crash("MIDI format 2 unsupported");
            if (ppq < 0)
                Crash("PPQ is negative");
            
            MIDIClock.ppq = ppq;
            Starter.form.label6.Text = $"PPQ: {ppq}";
            Starter.form.label10.Text = $"Loaded tracks: 0 / ??? ({tracks})";
            
            midistream = new StreamReader(path).BaseStream;
            trackAmount = 0;
            
            Console.WriteLine("Indexing MIDI tracks...");
            VerifyHeader();
            while (midistream.Position < midistream.Length)
            {
                bool success = IndexTrack();
                Starter.form.label10.Text = $"Loaded tracks: 0 / {trackAmount} ( {tracks} )";
                if (!success)
                    break;
            }
            Starter.form.label10.Text = $"Loaded tracks: 0 / {trackAmount}";
            
            List<long>[] tempLists = new List<long>[trackAmount];
            for(int i = 0; i < trackAmount; i++)
            {
                tempLists[i] = new List<long>();
            }
            
            midistream.Position++;
            int loops = 0;
            int totaltracks = 0;
            Parallel.For(0, trackAmount, (i) =>
            {
                if (Interlocked.Increment(ref loops) <= tracklimit)
                {
                    Console.WriteLine($"Loading track #{i + 1} | Size {trackSizes[i]}");
                    
                    // this shouldntve worked at all, but it deadass makes >2gb track loading possible
                    int bufSize = (int)Math.Min(int.MaxValue, trackSizes[i]/4);
                    // along with lower memory usage. which i mean if it works it works alr :sob:
                    
                    FastTrack temp = new FastTrack(
                        new BufferByteReader(midistream, bufSize, trackLocations[i], trackSizes[i])
                    );
                    temp.ParseTrackEvents(thres, tempLists[i]);
                    
                    // update counters
                    Interlocked.Add(ref loadedNotes, temp.loadedNotes);
                    Interlocked.Add(ref totalNotes, temp.totalNotes);
                    Interlocked.Add(ref eventCount, temp.eventAmount);
                    totaltracks++;
                    
                    Starter.form.label10.Text = $"Loaded tracks: {loops} / {trackAmount}";
                    Starter.form.label5.Text = $"Notes: {loadedNotes} / {totalNotes}";
                    temp.Dispose();
                }
            });
            midistream.Close();
            Starter.form.label10.Text = $"Loaded tracks: {totaltracks} / {trackAmount}";
            
            Console.WriteLine("preprocessing stuff for the renderer");
            Renderer.NoteProcessor.InitializeBuckets(maxTick);
            int bucketCount = (maxTick / Renderer.NoteProcessor.BucketSize) + 2;
            Parallel.For(0, trackAmount, (i) =>
            {
                if (tempLists[i] != null && tempLists[i].Count > 0)
                {
                    // Estimate notes for this track (events / 2 for note on/off pairs)
                    int thisTrackNotes = tempLists[i].Count / 2;
                    int notesPerBucket = (thisTrackNotes / bucketCount) + 16;

                    Renderer.NoteProcessor.ProcessTrackForRendering(tempLists[i], i, notesPerBucket);
                }
            });
            Renderer.NoteProcessor.FinalizeBuckets();
            
            ulong totalEvents = 0;
            for (int trk = 0; trk < trackAmount; trk++)
            {
                if (tempLists[trk] != null)
                    totalEvents += (ulong)tempLists[trk].Count;
            }
            
            Console.WriteLine("merging events to one array");
            MIDI.synthEvents = new BigArray<long>(totalEvents);
            unsafe
            {
                MergeAllTracks(tempLists, MIDI.synthEvents);
            }
            
            for (int trk = 0; trk < trackAmount; trk++) 
                tempLists[trk] = null;
            
            MIDI.tempoEvents = [.. MIDI.temppos];
            MIDI.temppos.Clear();
            
            Starter.form.label2.Text = "Status: Loaded";
            Starter.form.button4.Enabled = true;
            Console.WriteLine("MIDI Loaded");
            
            midistream.Close();
        }
        
        public static void Unload()
        {
            totalNotes = 0;
            loadedNotes = 0;
            eventCount = 0;
            maxTick = 0;
            trackAmount = 0;
            trackLocations = new List<long>();
            trackSizes = new List<uint>();
            MIDI.synthEvents.Dispose();
            MIDI.tempoEvents = null!;
            GC.Collect();
        }

        static uint VerifyHeader()
        {
            bool success = FindText("MThd");
            if (success)
            {
                uint size = ReadInt32();
                int fmt = ReadInt16();
                midistream.Seek(midistream.Position + 2, SeekOrigin.Begin);
                int ppq = ReadInt16();
                if (fmt == 2)
                    Crash("MIDI format 2 unsupported");
                if (ppq < 0)
                    Crash("PPQ is negative");
                if (size != 6)
                    Crash($"Incorrect header size of {size}");
                return size;
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
                uint size = ReadInt32();
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
            int trackAmount = trackEvents.Length;
            ulong outPos = 0;

            // Min-heap of (value, track index)
            var heap = new PriorityQueue<(long value, int track), long>();

            // Per-track index
            int[] idx = new int[trackAmount];

            // Initialize heap with the first element of each non-empty list
            for (int t = 0; t < trackAmount; t++)
            {
                var list = trackEvents[t];
                if (list != null && list.Count > 0)
                {
                    heap.Enqueue((list[0], t), list[0]);
                }
            }

            while (heap.TryDequeue(out var entry, out long _))
            {
                long value = entry.value;
                int t = entry.track;

                // Store to output buffer
                output.Pointer[outPos++] = value;

                // Advance index & push next element
                int next = ++idx[t];
                var list = trackEvents[t];
                if (next < list.Count)
                {
                    long val2 = list[next];
                    heap.Enqueue((val2, t), val2);
                }
                else
                {
                    trackEvents[t] = null;
                }
            }
        }

        static uint ReadInt32()
        {
            uint length = 0;
            for (int i = 0; i != 4; i++)
                length = (uint)((length << 8) | (byte)midistream.ReadByte());
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