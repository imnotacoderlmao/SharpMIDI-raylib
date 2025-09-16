#pragma warning disable 8602

namespace SharpMIDI
{
    class MIDILoader
    {
        //static int pushback = -1;
        private static List<long> trackLocations = new List<long>();
        private static List<uint> trackSizes = new List<uint>();
        static byte threshold = 0;
        static Stream? midiStream;
        static uint totalSize = 0;
        public static long totalNotes = 0;
        public static long loadedNotes = 0;
        public static int tks = 0;
        public static uint loadedTracks = 0;
        static uint gcRequirement = 134217728;

        static Stream? midi;

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
            loadedTracks = 0;
            totalSize = 0;
            //pushback = -1;
            trackLocations = new List<long>();
            trackSizes = new List<uint>();
        }
        static async Task LoadArchive(int tracklimit)
        {
            await Task.Run(async () =>
            {
                int tk = 0;
                int realtk = 0;
                while (true)
                {
                    bool found = FindText("MTrk");
                    if (found)
                    {
                        tk++;
                        if (MIDITrack.finished)
                            realtk++;
                        Starter.form.label10.Text = "Loaded tracks: " + realtk + " / " + MIDILoader.tks;
                        Starter.form.label10.Update();
                        //await Task.Delay(1);
                        if (tracklimit<=tk){
                            Console.WriteLine("Track limit reached, stopping loading.");
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            });
        }

        public static async void LoadPath(string path, byte thres, int tracklimit)
        {
            threshold = thres;
            midi = File.OpenRead(path);
            (bool, Stream) test = CJCMCG.ArchiveStreamPassthrough(path,midi);
            midi = test.Item2;
            bool find = FindText("MThd");
            if (!find) { Crash("Unexpected EoF searching for MThd"); }
            uint size = ReadInt32();
            uint fmt = ReadInt16();
            uint tracks = ReadInt16();
            tks = (int)tracks;
            int ppq = ReadInt16();
            MIDIClock.ppq = ppq;
            MIDIPlayer.ppq = ppq;
            Starter.form.label6.Text = "PPQ: "+ppq;
            Starter.form.label6.Update();
            Starter.form.label10.Text = "Loaded tracks: 0 / ??? ("+tracks+")";
            Starter.form.label10.Update();
            if (size != 6) { Crash("Incorrect header size of " + size); }
            if (fmt == 2) { Crash("MIDI format 2 unsupported"); }
            if (ppq < 0) { Crash("PPQ is negative"); }
            if (test.Item1)
            {
                await LoadArchive(tracklimit);
            } else
            {
                midiStream = new StreamReader(path).BaseStream;
                tks = 0;
                VerifyHeader();
                Console.WriteLine("Indexing MIDI tracks...");
                while (midiStream.Position < midiStream.Length)
                {
                    bool success = IndexTrack();
                    Starter.form.label10.Text = "Loaded tracks: 0 / "+tks+" (" + tracks + ")";
                    Starter.form.label10.Update();
                    if (!success) { break; }
                }
                MIDIPlayer.SubmitTrackCount(tks);
                Starter.form.label10.Text = "Loaded tracks: 0 / " + tks;
                Starter.form.label10.Update();
                midiStream.Position += 1;
                int loops = 0;
                Parallel.For(0, tks, (i) =>
                {
                    {
                        if (loops <= tracklimit)
                        {
                            int bufSize = 2147483647;
                            if (bufSize > trackSizes[i])
                            {
                                bufSize = (int)trackSizes[i];
                            }
                            FastTrack temp = new FastTrack(new BufferByteReader(midiStream, bufSize, trackLocations[i], trackSizes[i]));
                            Console.WriteLine("Loading track #" + (i + 1) + " | Size " + trackSizes[i]);
                            totalSize += trackSizes[i];
                            temp.ParseTrackEvents(threshold);
                            temp.Dispose();
                            MIDIPlayer.SubmitTrackForPlayback(i, temp.track);
                            loops++;
                            Starter.form.label10.Text = "Loaded tracks: " + loops + " / " + MIDILoader.tks;
                            Starter.form.label10.Update();
                            if (totalSize >= gcRequirement)
                            {
                                totalSize = 0;
                                GC.Collect();
                            }
                        } else
                        {
                            Console.WriteLine("Ignoring track #" + (i + 1) + " | Size " + trackSizes[i]);
                            MIDIPlayer.SubmitTrackForPlayback(i, new MIDITrack());
                        }
                    }
                });
                Starter.form.label10.Text = "Loaded tracks: " + MIDIPlayer.tracks.Length + " / " + MIDILoader.tks;
                Starter.form.label10.Update();
                midiStream.Close();
            }
            Console.WriteLine("Calling MIDIRenderer.EnhanceTracksForRendering()...");
            await Task.Run(() => Renderer.MIDIRenderer.EnhanceTracksForRendering());
            Starter.form.label2.Text = "Status: Loaded";
            Starter.form.label2.Update();
            GC.Collect();
            Starter.form.button4.Enabled = true;
            Starter.form.button4.Update();
            Console.WriteLine("MIDI Loaded");
            midi.Close();
        }

        static uint VerifyHeader()
        {
            bool success = FindText2("MThd");
            if (success)
            {
                uint size = ReadInt32_v2();
                uint fmt = ReadInt16_v2();
                Seek2(2);
                uint ppq = ReadInt16_v2();
                if (size != 6) { Crash("Incorrect header size of " + size); }
                if (fmt == 2) { Crash("MIDI format 2 unsupported"); }
                if (ppq < 0) { Crash("PPQ is negative"); }
                return ppq;
            }
            else
            {
                Crash("Header issue");
                return 0;
            }
        }

        static bool IndexTrack()
        {
            bool success = FindText2("MTrk");
            if (success)
            {
                uint size = ReadInt32_v2();
                trackLocations.Add(midiStream.Position);
                trackSizes.Add(size);
                midiStream.Position += size;
                tks++;
                return true;
            }
            else
            {
                return false;
            }
        }

        static void Seek2(long bytes)
        {
            midiStream.Seek(midiStream.Position + 2, SeekOrigin.Begin);
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

        static uint ReadInt32_v2()
        {
            uint length = 0;
            for (int i = 0; i != 4; i++)
                length = (uint)((length << 8) | (byte)midiStream.ReadByte());
            return length;
        }

        static ushort ReadInt16_v2()
        {
            ushort length = 0;
            for (int i = 0; i != 2; i++)
                length = (ushort)((length << 8) | (byte)midiStream.ReadByte());
            return length;
        }

        static bool FindText2(string text)
        {
            foreach (char l in text)
            {
                int test = midiStream.ReadByte();
                if (test != l)
                {
                    if (test == -1)
                    {
                        return false;
                    }
                    else
                    {
                        MessageBox.Show("Could not locate header '"+text+"', attempting to continue.");
                        //Crash("Header issue searching for " + text + " on char " + l.ToString() + ", found " + test + " at pos " + midiStream.Position);
                    }
                }
            }
            return true;
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