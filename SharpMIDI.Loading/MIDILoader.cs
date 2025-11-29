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
        public static void ResetVariables()
        {
            totalNotes = 0;
            loadedNotes = 0;
            tks = 0;
            trackLocations = new List<long>();
            trackSizes = new List<uint>();
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
            MIDIClock.ppq = MIDIPlayer.ppq = ppq;
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
            List<SynthEvent>[] trackEvents = new List<SynthEvent>[tks];
            midi.Position++;
            int loops = 0;
            Parallel.For(0, tks, (i) =>
            {
                if (Interlocked.Increment(ref loops) <= tracklimit)
                {
                    Console.WriteLine("Loading track #" + (i + 1) + " | Size " + trackSizes[i]);
                    int bufSize = (int)Math.Min(2147483647, trackSizes[i]);
                    int estimatedEventsPerTrack = (int)(trackSizes[i] / 4);
                    FastTrack temp = new FastTrack(
                        new BufferByteReader(midi, bufSize, trackLocations[i], trackSizes[i])
                    );
                    temp.ParseTrackEvents(thres);
                    // store each track’s events locally
                    trackEvents[i] = temp.localEvents;
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
            int totalEvents = 0;
            for (int i = 0; i < tks; i++)
            {
                trackEvents[i].TrimExcess();
                if (trackEvents[i] != null)
                    totalEvents += trackEvents[i].Count;
            }
            MIDI.synthEvents = new SynthEvent[totalEvents];
            int offset = 0;
            for (int i = 0; i < tks; i++)
            {
                var list = trackEvents[i];
                if (list == null) continue;
                list.CopyTo(MIDI.synthEvents, offset);
                offset += list.Count;
            }
            Console.WriteLine("sorting events by time");
            Array.Sort(MIDI.synthEvents, (a, b) => a.pos.CompareTo(b.pos));
            MIDI.tempos.Sort((a, b) => a.pos.CompareTo(b.pos));
            MIDI.tempos.TrimExcess();
            Console.WriteLine("Calling MIDIRenderer.EnhanceTracksForRendering()...");
            await Task.Run(() => Renderer.MIDIRenderer.EnhanceTracksForRendering());
            Starter.form.label2.Text = "Status: Loaded";
            GC.Collect();
            Starter.form.button4.Enabled = true;
            Starter.form.button4.Update();
            Console.WriteLine("MIDI Loaded");
            midi.Close();
        }
        
        public static void ClearEntries()
        {
            MIDIPlayer.ppq = 0;
            totalNotes = 0;
            loadedNotes = 0;
            eventCount = 0;
            maxTick = 0;
            MIDI.synthEvents = null!;
            MIDI.tempos.Clear();
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