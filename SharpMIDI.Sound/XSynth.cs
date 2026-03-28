using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpMIDI
{
    static unsafe class XSynth
    {
        public enum OMSettingMode
        {
            OM_SET = 0x0,
            OM_GET = 0x1
        }

        public enum OMSetting
        {
            OM_CAPFRAMERATE = 0x10000,
            OM_DEBUGMMODE = 0x10001,
            OM_DISABLEFADEOUT = 0x10002,
            OM_DONTMISSNOTES = 0x10003,

            OM_ENABLESFX = 0x10004,
            OM_FULLVELOCITY = 0x10005,
            OM_IGNOREVELOCITYRANGE = 0x10006,
            OM_IGNOREALLEVENTS = 0x10007,
            OM_IGNORESYSEX = 0x10008,
            OM_IGNORESYSRESET = 0x10009,
            OM_LIMITRANGETO88 = 0x10010,
            OM_MT32MODE = 0x10011,
            OM_MONORENDERING = 0x10012,
            OM_NOTEOFF1 = 0x10013,
            OM_EVENTPROCWITHAUDIO = 0x10014,
            OM_SINCINTER = 0x10015,
            OM_SLEEPSTATES = 0x10016,

            OM_AUDIOBITDEPTH = 0x10017,
            OM_AUDIOFREQ = 0x10018,
            OM_CURRENTENGINE = 0x10019,
            OM_BUFFERLENGTH = 0x10020,
            OM_MAXRENDERINGTIME = 0x10021,
            OM_MINIGNOREVELRANGE = 0x10022,
            OM_MAXIGNOREVELRANGE = 0x10023,
            OM_OUTPUTVOLUME = 0x10024,
            OM_TRANSPOSE = 0x10025,
            OM_MAXVOICES = 0x10026,
            OM_SINCINTERCONV = 0x10027,

            OM_OVERRIDENOTELENGTH = 0x10028,
            OM_NOTELENGTH = 0x10029,
            OM_ENABLEDELAYNOTEOFF = 0x10030,
            OM_DELAYNOTEOFFVAL = 0x10031
        }

        static IntPtr lib;
        public static delegate* unmanaged[SuppressGCTransition]<uint, void> _sendDirectData;
        public static delegate* unmanaged<bool> _isKDMAPIAvailable;
        public static delegate* unmanaged<int> _initializeKDMAPIStream;
        public static delegate* unmanaged<int> _terminateKDMAPIStream;
        public static delegate* unmanaged<void> _resetKDMAPIStream;
        public static delegate* unmanaged<uint, uint, uint, uint> _sendCustomEvent;
        public static delegate* unmanaged<byte*, uint, uint> _sendDirectLongDataLinux;
        public static delegate* unmanaged<MIDIHDR*, uint, uint> _sendDirectLongData;
        public static delegate* unmanaged<MIDIHDR*, uint, uint> _prepareLongData;
        public static delegate* unmanaged<MIDIHDR*, uint, uint> _unprepareLongData;

        public static void Load()
        {
        #if WINDOWS
            lib = NativeLibrary.Load("XSynth.dll");
            Console.WriteLine("loading from XSynth.dll");
            _prepareLongData = (delegate* unmanaged<MIDIHDR*, uint, uint>) NativeLibrary.GetExport(lib, "PrepareLongData");
            _unprepareLongData = (delegate* unmanaged<MIDIHDR*, uint, uint>) NativeLibrary.GetExport(lib, "UnprepareLongData");
            _sendDirectLongData = (delegate* unmanaged<MIDIHDR*, uint, uint>) NativeLibrary.GetExport(lib, "SendDirectLongData");
        #elif LINUX
            lib = NativeLibrary.Load("libXSynth.so"); // if my brain is smart then it should be that if its compiled on linux
            Console.WriteLine("loading from libXSynth.so");
            _sendDirectLongDataLinux = (delegate* unmanaged<byte*, uint, uint>) NativeLibrary.GetExport(lib, "SendDirectLongData");
        #endif
            _sendDirectData = (delegate* unmanaged[SuppressGCTransition]<uint, void>)  NativeLibrary.GetExport(lib, "SendDirectData");
            _isKDMAPIAvailable = (delegate* unmanaged<bool>) NativeLibrary.GetExport(lib, "IsKDMAPIAvailable");
            _initializeKDMAPIStream = (delegate* unmanaged<int>) NativeLibrary.GetExport(lib, "InitializeKDMAPIStream");
            _terminateKDMAPIStream  = (delegate* unmanaged<int>) NativeLibrary.GetExport(lib, "TerminateKDMAPIStream");
            _resetKDMAPIStream = (delegate* unmanaged<void>) NativeLibrary.GetExport(lib, "ResetKDMAPIStream");
        }
    }
}
