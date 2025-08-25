namespace SharpMIDI
{
    internal class Starter
    {
        public static Form1 form = new Form1();
        public static bool midiLoaded = false;
        public static string filename;

        [STAThread]
        static void Main()
        {
            Form.CheckForIllegalCrossThreadCalls = false;
            ApplicationConfiguration.Initialize();
            Renderer.MIDIRenderer.StartRenderer();
            Application.Run(form);
        }
        public static void SubmitMIDIPath(string str)
        {
            Console.WriteLine("Loading MIDI file: " + str);
            midiLoaded = true;
            form.label1.Text = "Selected MIDI: " + Path.GetFileName(str);
            form.label2.Text = "Status: Loading";
            form.label1.Update();
            form.label2.Update();
            filename = Path.GetFileName(str);
            MIDILoader.LoadPath(str, (byte)form.numericUpDown1.Value, (int)form.numericUpDown3.Value);
            return;
        }
    }
}
