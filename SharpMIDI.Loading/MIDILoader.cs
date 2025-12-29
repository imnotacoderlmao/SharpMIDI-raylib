#pragma warning disable 8602
namespace SharpMIDI
{
    static class MIDILoader
    {
        private static List<long> trackLocations = new List<long>();
        private static List<uint> trackSizes = new List<uint>();
        static ulong[]? tickOffsets;
        static int   tickOffsetCapacity;
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

        public static async Task LoadPath(string path, byte thres, int tracklimit)
        {   
            midistream = File.OpenRead(path);
            VerifyHeader();

            MIDIClock.ppq = ppq;
            Starter.form.label6.Text = $"PPQ: {ppq}";

            Console.WriteLine("Indexing MIDI tracks...");
            trackAmount = 0;
            while (midistream.Position < midistream.Length)
            {
                bool success = IndexTrack();
                if (!success)
                    break;
            }
            
            List<long>[] tempLists = new List<long>[trackAmount];
            for (int i = 0; i < trackAmount; i++)
            {
                tempLists[i] = new List<long>();
            }
            
            midistream.Position++;
            loadedtracks = 1;
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
                loadedtracks++;
                
                temp.Dispose();
            });
            midistream.Close();
            
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
            
            Console.WriteLine("merging events to one array");
            MIDI.synthEvents = new BigArray<long>((ulong)eventCount + 1);
            unsafe
            {
                MergeAllTracks(tempLists, MIDI.synthEvents);
                
                // using dummy events with positions > maxtick for the sake of no mid-playback bounds checking
                MIDI.temppos.Add((long)int.MaxValue << 32);
                MIDI.synthEvents.Pointer[eventCount] = (long)int.MaxValue << 32;
            }
            MIDI.tempoEvents = [.. MIDI.temppos];
            MIDI.temppos.Clear();
            
            Starter.form.label2.Text = "Status: Loaded";
            Starter.form.button4.Enabled = true;
            Console.WriteLine("MIDI Loaded");
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

        public static unsafe void MergeAllTracks(List<long>[] tracks, BigArray<long> output)
        {
            int trackCount = tracks.Length;
            int max = maxTick + 1;

            // Ensure reusable offset buffer
            if (tickOffsets == null || tickOffsetCapacity < max)
            {
                tickOffsets = new ulong[max];
                tickOffsetCapacity = max;
            }
            else
            {
                Array.Clear(tickOffsets, 0, max);
            }

            ulong[] offsets = tickOffsets;

            // count events per tick
            for (int t = 0; t < trackCount; t++)
            {
                var list = tracks[t];
                if (list == null) continue;

                int count = list.Count;
                for (int i = 0; i < count; i++)
                    offsets[(int)(list[i] >> 32)]++;
            }

            // prefix sum to write positions
            ulong sum = 0;
            for (int i = 0; i < max; i++)
            {
                ulong c = offsets[i];
                offsets[i] = sum;
                sum += c;
            }

            // write events directly
            long* dst = output.Pointer;

            for (int t = 0; t < trackCount; t++)
            {
                var list = tracks[t];
                if (list == null) continue;

                int count = list.Count;
                for (int i = 0; i < count; i++)
                {
                    long ev = list[i];
                    int tick = (int)(ev >> 32);
                    ulong pos = offsets[tick]++;
                    dst[pos] = ev;
                }
            }
            tracks = null;
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