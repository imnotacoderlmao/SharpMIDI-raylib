using System.Runtime.CompilerServices;
using System.Windows.Forms.VisualStyles;
using SharpMIDI.Renderer;

namespace SharpMIDI
{
    class MIDIPlayer
    {
        public static MIDITrack[] tracks = Array.Empty<MIDITrack>();
        public static void SubmitTrackCount(int count)
        {
            tracks = new MIDITrack[count];
        }
        static double totalNotes = 0;
        public static double loadedNotes = 0;
        static double eventCount = 0;
        public static double maxTick = 0;
        public static int ppq = 0;
        public static bool paused = false;
        public static bool stopping = false;
        public static double clock = 0;
        private static int totalFrames = 0;
        private static double totalDelay = 0;
        public static void ClearEntries()
        {
            ppq = 0;
            totalNotes = 0;
            loadedNotes = 0;
            eventCount = 0;
            maxTick = 0;
            foreach (MIDITrack i in tracks)
            {
                i.synthEvents.Clear();
            }
            MIDITrack.tempos.Clear();
            Array.Clear(tracks);
            tracks = Array.Empty<MIDITrack>();
            GC.Collect();
        }

        public static float GetMaxTick()
        {
            float max = 0f;
            foreach (var track in tracks)
                max = Math.Max(max, track.maxTick);
            return max;
        }

        public static void SubmitTrackForPlayback(int index, MIDITrack track)
        {
            if (tracks.Length <= index)
            {
                Array.Resize(ref tracks, tracks.Length + 1);
            }
            loadedNotes += track.loadedNotes;
            eventCount += track.eventAmount;
            totalNotes += track.totalNotes;
            if (track.maxTick > maxTick)
            {
                maxTick = track.maxTick;
            }
            Starter.form.label5.Text = "Notes: " + loadedNotes + " / " + totalNotes;
            tracks[index] = track;
        }
        public static double tick()
        {
            TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1);
            double secondsSinceEpoch = t.TotalSeconds;
            return secondsSinceEpoch;
        }
        public static Task UpdateUI()
        {
            while (true)
            {
                Starter.form.label12.Text = "FPS \u2248 " + Math.Round(1 / ((double)(totalDelay / TimeSpan.TicksPerSecond) / (double)totalFrames), 5);
                Starter.form.label7.Text = "Memory Usage: " + Form1.toMemoryText(GC.GetTotalMemory(false)) + " (May be inaccurate)";
                Starter.form.label14.Text = "Tick: " + Math.Round(clock, 0) + " / " + maxTick;
                Starter.form.label16.Text = "TPS: " + Math.Round(1 / MIDIClock.ticklen, 5);
                Starter.form.label17.Text = "BPM: " + Math.Round(MIDIClock.bpm, 5);
                Starter.form.label3.Text = "Played events: " + Sound.totalEvents + " / " + eventCount;
                totalFrames = 0;
                totalDelay = 0;
                Thread.Sleep(33);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe async Task StartPlayback()
        {
            clock = 0;
            stopping = false;
            int tempoProgress = 0;
            System.Diagnostics.Stopwatch? watch = System.Diagnostics.Stopwatch.StartNew();

            MIDIClock.Reset();
            Sound.totalEvents = 0;
            MIDIClock.Start();
            
            var nextEventTick = new int[tracks.Length];
            var trackIndices = new int[tracks.Length];
            var trackCounts = new int[tracks.Length];
            for (int i = 0; i < tracks.Length; i++)
            {
                trackCounts[i] = tracks[i].synthEvents.Count;
                nextEventTick[i] = trackCounts[i] > 0 ? tracks[i].synthEvents[0].pos : int.MaxValue;
            }
            
            while (!stopping)
            {
                clock = MIDIClock.GetTick();
                long watchtime = watch.ElapsedTicks;
                watch.Restart();
                totalDelay += watchtime;
                while (tempoProgress < MIDITrack.tempos.Count)
                {
                    Tempo tev = MIDITrack.tempos[tempoProgress];
                    if (tev.pos > clock) break;
                    tempoProgress++;
                    MIDIClock.SubmitBPM(tev.pos, tev.tempo);
                }
                for (int i = 0; i < tracks.Length; i++)
                {
                    if (nextEventTick[i] > clock) continue;
                    var events = tracks[i].synthEvents;
                    int idx = trackIndices[i];
                    while (idx < trackCounts[i] && events[idx].pos <= clock)
                    {
                        Sound.Submit((uint)events[idx++].val);
                        Sound.totalEvents++;
                    }
                    trackIndices[i] = idx;
                    nextEventTick[i] = idx < trackCounts[i] ? events[idx].pos : int.MaxValue;
                }
                totalFrames++;
                if (clock > maxTick) stopping = true;
            }
            
            Console.WriteLine("Playback finished...");
            MIDIClock.Reset();
            Starter.form.button4.Enabled = true;
            Starter.form.button4.Update();
            Starter.form.button5.Enabled = false;
            Starter.form.button5.Update();
            Starter.form.button6.Enabled = false;
            Starter.form.button6.Update();
            return;
        }
    }
}