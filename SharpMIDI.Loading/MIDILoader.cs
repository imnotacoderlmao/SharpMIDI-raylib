#pragma warning disable 8602
using System.Runtime.InteropServices;

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
                long[] trackOffsets = new long[trackAmount];
                long[] trackEventCounts = new long[trackAmount];
                long totalEstimated = 0;

                // preallocate cheaply
                for (int i = 0; i < trackAmount && i <= tracklimit; i++)
                {
                    long estimate = trackSizes[i] / 3;
                    trackOffsets[i] = totalEstimated;
                    totalEstimated += estimate;
                }
                MIDI.synthEvents = new BigArray<long>((ulong)totalEstimated + 1024);
                long* eventsPtr = MIDI.synthEvents.Pointer;
                
                midistream.Position++;
                Parallel.For(0, trackAmount, (i) =>
                {
                    if (i > tracklimit) return;
                    Console.WriteLine($"Loading track #{i + 1} | Size {trackSizes[i]}");

                    FastTrack temp = new FastTrack(
                        // using fixed 256kib buffer size made it way faster somehow
                        new BufferByteReader(midistream, 256 * 1024, trackLocations[i], trackSizes[i]) 
                    );

                    // this parses to one big tempbuffer instead now
                    temp.ParseTrackEvents(thres, eventsPtr, trackOffsets[i]);
                    trackEventCounts[i] = temp.GetWrittenCount();

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
                    if (trackEventCounts[i] == 0) return;

                    long trackStart = trackOffsets[i];
                    long trackCount = trackEventCounts[i];

                    int thisTrackNotes = (int)(trackCount / 2);
                    int notesPerBucket = (thisTrackNotes / bucketCount) + 16;
                    Renderer.NoteProcessor.ProcessTrackForRendering(eventsPtr + trackStart, trackCount, i, notesPerBucket);
                });

                Renderer.NoteProcessor.FinalizeBuckets();

                Console.WriteLine("compacting and sorting events");
                long writePos = 0;
                for (int t = 0; t < trackAmount && t <= tracklimit; t++)
                {
                    long trackStart = trackOffsets[t];
                    long count = trackEventCounts[t];
                    
                    if (count > 0 && trackStart != writePos)
                    {
                        // move forward
                        Buffer.MemoryCopy(
                            eventsPtr + trackStart,
                            eventsPtr + writePos,
                            count * sizeof(long),
                            count * sizeof(long)
                        );
                    }
                    writePos += count;
                }
                eventCount = writePos;
                
                // dummy events for no playback bounds checking
                MIDI.temppos.Add((long)int.MaxValue << 32);
                eventsPtr[eventCount] = (long)int.MaxValue << 32;
                
                RadixSort(eventsPtr, eventCount+1);
            }

            MIDI.tempoEvents = [.. MIDI.temppos];
            MIDI.temppos.Clear();

            Starter.form.label2.Text = "Status: Loaded";
            Starter.form.button4.Enabled = true;
            Console.WriteLine("MIDI Loaded");
        }
        
        private static unsafe void RadixSort(long* events, long count)
        {
            const int RADIX_BITS = 16;
            const int RADIX = 1 << RADIX_BITS;
            const int MASK = RADIX - 1;
        
            // this kinda sucks in memory efficiency but its pretty fast
            long* sortBuffer = (long*)NativeMemory.Alloc((nuint)(count * sizeof(long)));
            long* temp = sortBuffer;
        
            // counts: uint is enough unless a single bucket exceeds 4 billion entries
            uint* counts = stackalloc uint[RADIX];
        
            bool swapped = false;
        
            // low 16 bits, then high 16 bits of tick
            for (int pass = 0; pass < 2; pass++)
            {
                int shift = 32 + (pass * RADIX_BITS);
        
                for (int i = 0; i < RADIX; i++)
                    counts[i] = 0;

                for (long i = 0; i < count; i++)
                {
                    uint bucket = (uint)(events[i] >> shift) & MASK;
                    counts[bucket]++;
                }
        
                // prefix sum
                uint sum = 0;
                for (int i = 0; i < RADIX; i++)
                {
                    uint c = counts[i];
                    counts[i] = sum;
                    sum += c;
                }
        
                // redistribute
                for (long i = 0; i < count; i++)
                {
                    long ev = events[i];
                    uint bucket = (uint)(ev >> shift) & MASK;
                    temp[counts[bucket]++] = ev;
                }
        
                // swap buffers
                long* swap = events;
                events = temp;
                temp = swap;
                swapped = !swapped;
            }
        
            // ensure final result is in original buffer
            if (swapped)
            {
                Buffer.MemoryCopy(events, temp, count * sizeof(long), count * sizeof(long));
            }
            NativeMemory.Free(sortBuffer);
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