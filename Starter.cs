namespace SharpMIDI
{
    internal static class Starter
    {
        public static Form1 form = new Form1();
        public static bool midiLoaded = false;
        public static string? filename;

        [STAThread]
        static void Main()
        {
            Form.CheckForIllegalCrossThreadCalls = false;
            ApplicationConfiguration.Initialize();
            Application.Run(form);
        }
        public static async Task SubmitMIDIPath(string path)
        {
            Console.WriteLine("Loading MIDI file: " + path);
            midiLoaded = true;
            form.label1.Text = "Selected MIDI: " + Path.GetFileName(path);
            form.label2.Text = "Status: Loading";
            filename = Path.GetFileName(path);
            byte veltreshold = (byte)form.numericUpDown1.Value;
            ushort tracklimit = (ushort)form.numericUpDown3.Value;
            await Task.Run(()=>MIDILoader.LoadPath(path, veltreshold, tracklimit));
        }
    }
}
