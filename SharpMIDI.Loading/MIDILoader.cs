#pragma warning disable 8602
using SharpMIDI.Renderer;

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
            loaded = false;
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
            MIDIRenderer.InitializeForMIDI();
            for (int i = 0; i < tempLists.Length; i++)
                tempLists[i] = null;
            
            MIDI.tempoEvents = [.. MIDI.temppos];
            MIDI.temppos.Clear();
            
            Starter.form.label2.Text = "Status: Loaded";
            
            Starter.form.button4.Enabled = true;
            Console.WriteLine("MIDI Loaded");
            loaded = true;
        }
        
        public static void Unload()
        {
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