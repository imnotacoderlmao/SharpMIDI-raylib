#pragma warning disable 8622
using System.Runtime.InteropServices;

namespace SharpMIDI
{
    public partial class Form1 : Form
    {
        private static Thread? worker;
        public static string toMemoryText(long bytes)
        {
            switch (bytes)
            {
                case var expression when bytes < 1000:
                    return bytes + " B";
                case var expression when bytes < 1000000:
                    return bytes/1000 + " KB";
                default:
                    return bytes / 1000000 + " MB";
            }
        }
        public Form1()
        {
            InitializeComponent();
            foreach(string i in WinMM.GetDevices())
            {
                comboBox1.Items.Add(i);
            }
            Renderer.MIDIRenderer.StartRenderer();
            Task.Run(() => UpdateUI());
        }

        public static Task UpdateUI()
        {
            double now, lastnow = 0, delay, fps = 0;
            while (true)
            {
                now = Timer.Seconds();
                delay = now - lastnow;
                fps = (fps * 0.4) + ((MIDIPlayer.totalFrames / delay) * 0.6);
                if (fps > 60) Starter.form.label12.Text = $"FPS \u2248 {Math.Round(fps,5)}";
                else Starter.form.label12.Text = "FPS \u2248 <60";
                Starter.form.label10.Text = $"Loaded tracks: {MIDILoader.loadedtracks} / {MIDILoader.trackAmount}";
                Starter.form.label5.Text = $"Notes: {MIDILoader.loadedNotes} / {MIDILoader.totalNotes}";
                Starter.form.label3.Text = $"Played: {MIDIPlayer.playedEvents} / {MIDILoader.eventCount}";
                Starter.form.label14.Text = $"Tick: {(int)MIDIClock.tick} / {MIDILoader.maxTick}";
                Starter.form.label16.Text = $"Ticks/sec: {Math.Round(MIDIClock.tickscale, 5)}";
                Starter.form.label17.Text = $"BPM: {Math.Round(MIDIClock.bpm, 5)}";
                Starter.form.label7.Text = $"GC Heap: {toMemoryText(GC.GetTotalMemory(false))} (May be inaccurate)";
                MIDIPlayer.totalFrames = 0;
                lastnow = now;
                Thread.Sleep(1000/60);
            }
        }


        void ToggleSynthSettings(bool t)
        {
            comboBox1.Enabled = t;
            radioButton1.Enabled = t;
            radioButton2.Enabled = t;
            radioButton3.Enabled = t;
            button3.Enabled = t;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                button1.Enabled = false;
                openFileDialog.Filter = "MIDI file (*.mid)|*.mid|7-Zip Archive (*.7z)|*.7z|.gz Archive (*.gz)|*.gz|.rar Archive (*.rar)|*.rar|.tar Archive (*.tar)|*.tar|.xz Archive (*.xz)|*.xz|.zip Archive (*.zip)|*.zip";
                openFileDialog.FilterIndex = 1;
                openFileDialog.RestoreDirectory = true;
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    Starter.SubmitMIDIPath(openFileDialog.FileName);
                    button2.Enabled = true;
                }
                else
                {
                    button1.Enabled = true;
                }
            }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            int soundEngine = 1;
            string? winMMdev = (string?)comboBox1.SelectedItem;
            if (radioButton2.Checked) { soundEngine = 2; } else if (radioButton3.Checked) { soundEngine = 3; }
            Console.WriteLine($"Loading sound engine ID {soundEngine}");
            ToggleSynthSettings(false);
            button1.Enabled = Sound.Init(soundEngine, winMMdev!) && !Starter.midiLoaded;
            label13.Visible = !button1.Enabled && !Starter.midiLoaded;
            ToggleSynthSettings(true);
        }

        private void button4_Click(object sender, EventArgs e)
        {
            button2.Enabled = true;
            button4.Enabled = false;
            button6.Enabled = true;
            button5.Enabled = true;
            StartPlaybackThread();
            //await Task.Run(() => MIDIPlayer.StartPlayback());
        }

        private static void StartPlaybackThread()
        {
            worker = new Thread(MIDIPlayer.StartPlayback)
            {
                Priority = ThreadPriority.Highest
            };
            worker.Start();
        }

        private void button7_Click(object sender, EventArgs e)
        {
            AllocConsole();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();

        static bool paused = false;

        private void button6_Click(object sender, EventArgs e)
        {
            if (!paused)
            {
                MIDIClock.Stop();
                button6.Text = "Play";
            } else
            {
                MIDIClock.Resume();
                button6.Text = "Pause";
            }
            paused = !paused;
        }

        private void button5_Click(object sender, EventArgs e) => MIDIPlayer.stopping = true;

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            MIDIClock.skipevents = checkBox1.Checked;
            MIDIClock.throttle = !MIDIClock.skipevents;
            //Console.WriteLine($"skipping is set to {MIDIClock.skipevents}, throttle set to {MIDIClock.throttle}");
        }

        private void button2_Click(object sender, EventArgs e)
        {
            if (Starter.midiLoaded)
            {
                label1.Text = "Selected MIDI: (none)";
                label2.Text = "Status: Not Loaded";
                label5.Text = "Notes: ??? / ???";
                label6.Text = "PPQ: ???";
                label10.Text = "Loaded tracks: 0 / ?????";
                label3.Text = "Played events: 0 / 0";
                label12.Text = "FPS \u2248 N/A";
                label14.Text = "Tick: 0";
                label16.Text = "TPS: N/A";
                label17.Text = "BPM: 120";
                button4.Enabled = false;
                button5.Enabled = false;
                button6.Enabled = false;
                button2.Enabled = false;
                button4.Update();
                button5.Update();
                button6.Update();
                button2.Update();
                Renderer.MIDIRenderer.Cleanup();
                MIDILoader.Unload();
                Starter.midiLoaded = false;
                button1.Enabled = true;
                button2.Enabled = false;
                GC.Collect();
            } else
            {
                button1.Enabled = true;
                button2.Enabled = false;
            }
        }
    }
}
