namespace SharpMIDI
{
    class MIDIPlayer
    {
        public static MIDITrack[] tracks = new MIDITrack[0];
        public static void SubmitTrackCount(int count)
        {
            tracks = new MIDITrack[count];
        }
        static double totalNotes = 0;
        public static double loadedNotes = 0;
        static double eventCount = 0;
        public static double maxTick = 0;
        public static uint ppq = 0;
        public static bool paused = false;
        public static double bpm = 120;
        private static double clock = 0;
        //private static double timeSinceLastPrint = tick();
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
                i.tempos.Clear();
            }
            Array.Clear(tracks);
            tracks = new MIDITrack[0];
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
            Starter.form.label7.Text = "Memory Usage: " + Form1.toMemoryText(GC.GetTotalMemory(false)) + " (May be inaccurate)";
            Starter.form.label7.Update();
            loadedNotes += track.loadedNotes;
            eventCount += track.eventAmount;
            totalNotes += track.totalNotes;
            if (track.maxTick > maxTick)
            {
                maxTick = track.maxTick;
            }
            Starter.form.label5.Text = "Notes: " + loadedNotes + " / " + totalNotes;
            Starter.form.label5.Update();
            tracks[index] = track;
        }
        public static double tick()
        {
            TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1);
            double secondsSinceEpoch = t.TotalSeconds;
            return secondsSinceEpoch;
        }
        public static bool stopping = false;

        public static async Task UpdateUI()
        {
            while (true)
            {
                Starter.form.label12.Text = "FPS \u2248 " + Math.Round(1 / ((double)(totalDelay / TimeSpan.TicksPerSecond) / (double)totalFrames), 5);
                //Starter.form.label12.Update();
                Starter.form.label7.Text = "Memory Usage: " + Form1.toMemoryText(GC.GetTotalMemory(false)) + " (May be inaccurate)";
                //Starter.form.label7.Update();
                Starter.form.label14.Text = "Tick: " + Math.Round(clock, 0) + " / " + maxTick;
                //Starter.form.label14.Update();
                Starter.form.label16.Text = "TPS: " + Math.Round(1 / MIDIClock.ticklen, 5);
                //Starter.form.label16.Update();
                Starter.form.label17.Text = "BPM: " + Math.Round(bpm, 5);
                //Starter.form.label17.Update();
                Starter.form.label3.Text = "Played events: " + Sound.totalEvents + " / " + eventCount;
                //Starter.form.label3.Update();
                totalFrames = 0;
                totalDelay = 0;
                Thread.Sleep(10);
            }
        }

        public static unsafe async Task StartPlayback()
        {
            if (!Renderer.StreamlinedRenderer.run){Renderer.StreamlinedRenderer.StartRenderer();};
            //Sound.Reload();
            stopping = false;
            double recentDelay = 0;
            float[] trackPositions = new float[tracks.Length];
            int[] eventProgress = new int[tracks.Length];
            int[] tempoProgress = new int[tracks.Length];
            System.Diagnostics.Stopwatch? watch = System.Diagnostics.Stopwatch.StartNew();

            MIDIClock.Reset();
            Sound.totalEvents = 0;
            MIDIClock.Start();
            fixed (int* eP = eventProgress)
            {
                fixed (float* tP = trackPositions)
                {
                    while (true)
                    {
                        //UpdateUI(); moved ui updates to form1, no more choppy playback (steamhappy emote here)
                        clock = MIDIClock.GetTick();
                        long watchtime = watch.ElapsedTicks;
                        watch.Stop();
                        watch = System.Diagnostics.Stopwatch.StartNew();
                        double delay = (double)watchtime / TimeSpan.TicksPerSecond;
                        totalDelay += watchtime;
                        recentDelay = watchtime;
                        int evs = 0;
                        int loops = -1;
                        foreach (MIDITrack i in tracks)
                        {
                            loops++;
                            while (true)
                            {
                                unsafe
                                {
                                    if (tempoProgress[loops] < i.tempoAmount)
                                    {
                                        Tempo ev = i.tempos[tempoProgress[loops]];
                                        evs++;
                                        if (ev.pos <= clock)
                                        {
                                            MIDIClock.SubmitBPM(ev.pos, ev.tempo);
                                            bpm = 60000000 / (double)ev.tempo;
                                            tempoProgress[loops]++;
                                        }
                                        else
                                        {
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                            }
                            while (true)
                            {
                                if (eP[loops] < i.eventAmount)
                                {
                                    SynthEvent ev = i.synthEvents[eP[loops]];
                                    evs++;
                                    if (tP[loops] + ev.pos <= clock)
                                    {
                                        eP[loops]++;
                                        tP[loops] += ev.pos;
                                        Sound.Submit((uint)ev.val);
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }

                        totalFrames++;
                        if (evs == 0 || stopping)
                        {
                            if (stopping)
                            Console.WriteLine("Playback finished...");
                            break;
                        }
                    }
                    Starter.form.label14.Text = "Tick: " + Math.Round(clock, 0) + " / " + maxTick;
                    Starter.form.label3.Text = "Played events: " + Sound.totalEvents + " / " + eventCount;
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
    }
}
