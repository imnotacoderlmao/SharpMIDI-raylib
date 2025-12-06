#pragma warning disable 8602
namespace SharpMIDI
{
    static class MIDILoader
    {
        private static List<long> trackLocations = new List<long>();
        private static List<uint> trackSizes = new List<uint>();
        static Stream? midi;
        public static long totalNotes = 0;
        public static long loadedNotes = 0;
        public static long eventCount = 0;
        public static int maxTick = 0;
        public static int tks = 0;

        static void Crash(string test)
        {
            MessageBox.Show(test);
            throw new Exception();
        }

        public static async void LoadPath(string path, byte thres, int tracklimit)
        {
            midi = File.OpenRead(path);
            bool find = FindText("MThd");
            if (!find) { Crash("Unexpected EoF searching for MThd"); }
            uint size = ReadInt32();
            int fmt = ReadInt16();
            int tracks = ReadInt16();
            int ppq = ReadInt16();
            int totaltracks = 0;
            tks = tracks;
            if (size != 6) { Crash("Incorrect header size of " + size); }
            if (fmt == 2) { Crash("MIDI format 2 unsupported"); }
            if (ppq < 0) { Crash("PPQ is negative"); }
            MIDIClock.ppq = ppq;
            Starter.form.label6.Text = "PPQ: "+ppq;
            Starter.form.label10.Text = "Loaded tracks: 0 / ??? ("+tracks+")";
            midi = new StreamReader(path).BaseStream;
            tks = 0;
            VerifyHeader();
            Console.WriteLine("Indexing MIDI tracks...");
            while (midi.Position < midi.Length)
            {
                bool success = IndexTrack();
                Starter.form.label10.Text = "Loaded tracks: 0 / "+tks+" (" + tracks + ")";
                if (!success) { break; }
            }
            Starter.form.label10.Text = "Loaded tracks: 0 / " + tks;
            List<long>[] tempLists = new List<long>[tks];
            for(int i = 0; i < tks; i++)
            {
                tempLists[i] = new List<long>();
            }
            midi.Position++;
            int loops = 0;
            Parallel.For(0, tks, (i) =>
            {
                if (Interlocked.Increment(ref loops) <= tracklimit)
                {
                    Console.WriteLine("Loading track #" + (i + 1) + " | Size " + trackSizes[i]);
                    int bufSize = (int)Math.Min(2147483647, trackSizes[i]/4);
                    FastTrack temp = new FastTrack(
                        new BufferByteReader(midi, bufSize, trackLocations[i], trackSizes[i])
                    );
                    temp.ParseTrackEvents(thres, tempLists[i]);
                    // update counters
                    Interlocked.Add(ref loadedNotes, temp.loadedNotes);
                    Interlocked.Add(ref totalNotes, temp.totalNotes);
                    Interlocked.Add(ref eventCount, temp.eventAmount);
                    totaltracks++;
                    Starter.form.label10.Text = "Loaded tracks: " + loops + " / " + tks;
                    Starter.form.label5.Text = "Notes: " + loadedNotes + " / " + totalNotes;
                    temp.Dispose();
                }
            });
            midi.Close();
            Starter.form.label10.Text = "Loaded tracks: " + totaltracks + " / " + MIDILoader.tks;
            ulong totalEvents = 0;
            for (int i = 0; i < tks; i++)
            {
                if (tempLists[i] != null)
                    totalEvents += (ulong)tempLists[i].Count;
            }
            Console.WriteLine("merging events to one array");
            MIDI.synthEvents = new BigArray<long>(totalEvents);
            unsafe
            {
                // this might be slower than traditional Array.Sort() but at least it works when events are over 2 billion
                MergeAllTracks(tempLists, MIDI.synthEvents);
            }
            for (int t = 0; t < tks; t++) tempLists[t] = null;
            MIDI.tempoEvents = [.. MIDI.temppos]; // idk wtf this does ngl
            Array.Sort(MIDI.tempoEvents);
            MIDI.temppos.Clear();
            Console.WriteLine("preprocessing stuff for the renderer");
            await Task.Run(() => Renderer.MIDIRenderer.EnhanceTracksForRendering());
            Starter.form.label2.Text = "Status: Loaded";
            Starter.form.button4.Enabled = true;
            Starter.form.button4.Update();
            Console.WriteLine("MIDI Loaded");
            midi.Close();
        }
        
        public static void Unload()
        {
            totalNotes = 0;
            loadedNotes = 0;
            eventCount = 0;
            maxTick = 0;
            tks = 0;
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
                midi.Seek(midi.Position + 2, SeekOrigin.Begin);
                int ppq = ReadInt16();
                if (fmt == 2) { Crash("MIDI format 2 unsupported"); }
                if (ppq < 0) { Crash("PPQ is negative"); }
                if (size != 6) { Crash("Incorrect header size of " + size); }
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
                trackLocations.Add(midi.Position);
                trackSizes.Add(size);
                midi.Position += size;
                tks++;
                return true;
            }
            else
            {
                return false;
            }
        }

        public static unsafe void MergeAllTracks(List<long>[] trackEvents, BigArray<long> output)
        {
            int tks = trackEvents.Length;
            ulong outPos = 0;

            // Min-heap of (value, track index)
            var heap = new PriorityQueue<(long value, int track), long>();

            // Per-track index
            int[] idx = new int[tks];

            // Initialize heap with the first element of each non-empty list
            for (int t = 0; t < tks; t++)
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
            }
        }

        static uint ReadInt32()
        {
            uint length = 0;
            for (int i = 0; i != 4; i++)
                length = (uint)((length << 8) | (byte)midi.ReadByte());
            return length;
        }

        static ushort ReadInt16()
        {
            ushort length = 0;
            for (int i = 0; i != 2; i++)
                length = (ushort)((length << 8) | (byte)midi.ReadByte());
            return length;
        }

        static bool FindText(string text)
        {
            foreach (char l in text)
            {
                int test = midi.ReadByte();
                if (test != l)
                {
                    if(test == -1){
                        return false;
                    } else {
                        Crash("Header issue searching for " + text);
                    }
                }
            }
            return true;
        }
    }
}