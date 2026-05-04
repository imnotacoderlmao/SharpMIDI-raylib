#if WINDOWS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpMIDI
{
    struct MidiOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string szPname;

        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }
    static unsafe class WinMM // dear god 2
    {
        static IntPtr lib;
        [DllImport("winmm.dll")]
        private static extern int midiOutGetNumDevs();
        [DllImport("winmm.dll")]
        private static extern int midiOutGetDevCaps(int uDeviceID, ref MidiOutCaps lpMidiOutCaps, uint cbMidiOutCaps);
        [DllImport("winmm.dll")]
        static extern uint midiOutOpen(out IntPtr lphMidiOut, uint uDeviceID, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);
        [DllImport("winmm.dll")]
        public static extern uint midiOutClose(IntPtr hMidiOut);
        
        public static delegate* unmanaged[SuppressGCTransition]<IntPtr, uint, uint> _midiOutShortMsg;
        public static delegate* unmanaged<IntPtr, MIDIHDR*, uint, uint> _midiOutPrepareHeader;
        public static delegate* unmanaged<IntPtr, MIDIHDR*, uint, uint> _midiOutLongMsg;
        public static delegate* unmanaged<IntPtr, MIDIHDR*, uint, uint> _midiOutUnrepareHeader;

        public static void InitializeFunctionPointer()
        {
            lib = NativeLibrary.Load("winmm.dll");
            _midiOutShortMsg = (delegate* unmanaged[SuppressGCTransition]<IntPtr, uint, uint>) NativeLibrary.GetExport(lib, "midiOutShortMsg");
            _midiOutPrepareHeader = (delegate* unmanaged<IntPtr, MIDIHDR*, uint, uint>) NativeLibrary.GetExport(lib, "midiOutPrepareHeader");
            _midiOutLongMsg = (delegate* unmanaged<IntPtr, MIDIHDR*, uint, uint>) NativeLibrary.GetExport(lib, "midiOutLongMsg");
            _midiOutUnrepareHeader = (delegate* unmanaged<IntPtr, MIDIHDR*, uint, uint>) NativeLibrary.GetExport(lib, "midiOutUnprepareHeader");
        }
        
        public static string lastWinMMDevice = "";
        public static IntPtr? handle;
        public static List<string> winMMDevices = GetDevices();

        public static uint WinMM_SendSysEx(MIDIHDR* message, uint size)
        {
            uint prepare = 0, send = 0, unprepare = 0;
            prepare = _midiOutPrepareHeader((IntPtr)handle, message, size);
            if (prepare == 0)
            {
                send = _midiOutLongMsg((IntPtr)handle, message, size);
                if (send == 0)
                {
                    while (_midiOutUnrepareHeader((IntPtr)handle, message, size) == 65)
                        Thread.Sleep(1);
                    unprepare = 0;
                }
            }
            if (prepare != 0 || send != 0 || unprepare != 0)
            {
                Console.WriteLine($"sysex prepare,send,unprepare returned ({prepare},{send},{unprepare})");
                return prepare + send + unprepare;
            }
            return 0;
        }

        public static List<string> GetDevices()
        {
            List<string> list = new List<string>();
            int devices = midiOutGetNumDevs();
            for (uint i = 0; i < devices; i++)
            {
                MidiOutCaps caps = new MidiOutCaps();
                midiOutGetDevCaps((int)i, ref caps, (uint)Marshal.SizeOf(caps));
                list.Add(caps.szPname);
            }
            return list;
        }

        public static (bool,string,string,IntPtr?,MidiOutCaps?) Setup(string device)
        {
            int devices = midiOutGetNumDevs();
            if (devices == 0)
            {
                return (false, "None", "No WinMM devices were found!", null, null);
            }
            else
            {
                MidiOutCaps myCaps = new MidiOutCaps();
                midiOutGetDevCaps(0, ref myCaps, (UInt32)Marshal.SizeOf(myCaps));
                IntPtr handle;
                for (uint i = 0; i < devices; i++)
                {
                    MidiOutCaps caps = new MidiOutCaps();
                    midiOutGetDevCaps((int)i, ref caps, (UInt32)Marshal.SizeOf(caps));
                    if (device == caps.szPname)
                    {
                        midiOutOpen(out handle, i, (IntPtr)0, (IntPtr)0, (uint)0);
                        return (true, caps.szPname, "WinMM initialized!", handle, caps);
                    }
                }
                return (false, "None", "Could not find the specified WinMM device.", null, null);
            }
        }
    }
}
#endif