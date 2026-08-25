#pragma warning disable 8618
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Silk.NET.OpenGL;

namespace SharpMIDI
{
    public static unsafe class GLNoteRenderer
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RenderNote
        {
            public int StartTick;
            public int EndTick;
            public uint PackedData; // color, key and velocity, though fits on a short its padded since fuck unaligned reads gpu says
        }

        /*[StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct KeyHeader
        {
            public int ActiveAbsId;
            public ushort ActiveCount;
        }*/

        private const string LineVertSrc =
        @"#version 420 core
in int aStartTick;
in int aEndTick;
in uint aPackedData;

uniform vec3 uMetrics;
uniform int uViewStart;
uniform int uViewEnd;
uniform int uCurrentTick;
uniform sampler1D uPalette;

flat out vec4 vColor;
flat out int vIsActive;
flat out float opacity;

void main() {
    int endTick = aEndTick > 0? aEndTick : uViewEnd;
    uint isEnd = uint(gl_VertexID) & 1u;
    uint isTop = uint(gl_VertexID >> 1) & 1u;
    float startX = float(aStartTick - uViewStart) * uMetrics.x - 1.0;
    float endX = float(endTick - uViewStart) * uMetrics.x - 1.0;
    float x = bool(isEnd)? endX : startX;
    float y = uMetrics.y + float(((aPackedData >> 8) & 0xFFu) + isTop) * uMetrics.z;
    float z = float(endTick - aStartTick) / 16777216.0;
    vColor = texelFetch(uPalette, int(aPackedData >> 16), 0);
    vIsActive = (uCurrentTick >= aStartTick && uCurrentTick <= endTick) ? 1 : 0;
    opacity = float(((aPackedData & 0xFFu) + 1) / 128.0);
    gl_Position = vec4(x, y, z, 1.0);
}";

        private const string LineFragSrc =
@"#version 420 core
flat in vec4 vColor;
flat in int vIsActive;
flat in float opacity;
uniform int uGlowEnabled;
uniform int uTransparencyEnabled;
out vec4 fragColor;

void main() {
    float note_opacity = (uTransparencyEnabled == 1) ? opacity : 1.0;
    vec3 color = vColor.rgb;
    color = (uGlowEnabled == 1 && vIsActive == 1)? min(color * 2.0 + 0.1, 1.0) : color;
    fragColor = vec4(color, note_opacity);
}";

        private static GL Gl;
        private const BufferStorageMask storageFlags = BufferStorageMask.MapWriteBit | BufferStorageMask.MapReadBit |
                                                       BufferStorageMask.MapPersistentBit | BufferStorageMask.MapCoherentBit;
        private const MapBufferAccessMask accessFlags = MapBufferAccessMask.WriteBit | MapBufferAccessMask.ReadBit |
                                                        MapBufferAccessMask.PersistentBit | MapBufferAccessMask.CoherentBit;
        private static uint _lineShader;
        private static int _uMetrics, _uViewStart, _uViewEnd;
        private static int _uPalette;
        private static int _uGlowEnabled, _uTransparencyEnabled, _uCurrentTick;
        private static uint _vao, _vboBuffer, _paletteTex;

        private static RenderNote* _ring;

        private static int _ringCap = 1 << 23;
        private static int _deferred_ringCap = -1;
        private static int _mask;

        private static int _head = 1;
        private static int _tail = 1;

        private readonly static byte[] paletteData = new byte[256 * 3];
        private static bool _paletteUploadPending = false;

        //private static KeyHeader* _keyHeaders;
        private static ushort* _activeKeyCount;
        private static byte* _activeKeyColor;
        private static int* _activeKeyID;

        private const int TOTAL_KEYS = 128 * 16;

        private static int _lookaheadTicks = 4000;
        private static float _pixelsPerTick;
        private static int _lastWindowTicks = -1;
        private static int _lastSweepEnd = -1;
        private static bool _isInitialized;

        public static int WindowTicks = 2000;
        public static int RingCap;
        public static int NotesDrawnLastFrame;
        public static bool UseForceCull = false;
        public static bool EnableGlow = true;
        public static bool EnableTransparency = false;

        private static readonly Vector128<byte> StatusShuffleMask = Vector128.Create(
            (byte)0, 3, 6, 9, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        private static readonly Vector128<byte> KeyShuffleMask = Vector128.Create(
            (byte)1, 4, 7, 10, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

        public static void Initialize()
        {
            if (_isInitialized) return;
            Gl = GL.GetApi(NativeGLBindingsContext.GetProcAddress);

            _lineShader = BuildShader(LineVertSrc, LineFragSrc);
            _uMetrics = Gl.GetUniformLocation(_lineShader, "uMetrics");
            _uViewStart = Gl.GetUniformLocation(_lineShader, "uViewStart");
            _uViewEnd = Gl.GetUniformLocation(_lineShader, "uViewEnd");
            _uPalette = Gl.GetUniformLocation(_lineShader, "uPalette");
            _uGlowEnabled = Gl.GetUniformLocation(_lineShader, "uGlowEnabled");
            _uTransparencyEnabled = Gl.GetUniformLocation(_lineShader, "uTransparencyEnabled");
            _uCurrentTick = Gl.GetUniformLocation(_lineShader, "uCurrentTick");

            Gl.UseProgram(_lineShader);
            Gl.Uniform1(_uPalette, 0);
            Gl.UseProgram(0);

            //_keyHeaders = (KeyHeader*)NativeMemory.AllocZeroed(TOTAL_KEYS * (nuint)sizeof(KeyHeader));
            _activeKeyCount = (ushort*)NativeMemory.AllocZeroed(TOTAL_KEYS * (nuint)sizeof(ushort));
            _activeKeyColor = (byte*)NativeMemory.AllocZeroed(TOTAL_KEYS * (nuint)sizeof(byte));
            _activeKeyID = (int*)NativeMemory.AllocZeroed(TOTAL_KEYS * (nuint)sizeof(int));

            _vao = Gl.GenVertexArray();
            _paletteTex = Gl.GenTexture();

            AllocRing(_ringCap);
            _isInitialized = true;
        }

        private static void AllocRing(int cap)
        {
            _mask = cap - 1;

            if (_vboBuffer != 0)
            {
                Gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vboBuffer);
                Gl.UnmapBuffer(BufferTargetARB.ArrayBuffer);
                Gl.DeleteBuffer(_vboBuffer);
            }

            _vboBuffer = Gl.GenBuffer();
            Gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vboBuffer);

            nuint totalBytes = (nuint)(cap * sizeof(RenderNote));
            Gl.BufferStorage(GLEnum.ArrayBuffer, totalBytes, null, (uint)storageFlags);
            _ring = (RenderNote*)Gl.MapBufferRange(BufferTargetARB.ArrayBuffer, 0, totalBytes, (uint)accessFlags);

            // attribute layout matches RenderNote: StartTick(int), EndTick(int), PackedData(uint)
            Gl.BindVertexArray(_vao);
            Gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vboBuffer);

            Gl.EnableVertexAttribArray(0);
            Gl.VertexAttribIPointer(0, 1, VertexAttribIType.Int, (uint)sizeof(RenderNote), (void*)0);
            Gl.VertexAttribDivisor(0, 1);

            Gl.EnableVertexAttribArray(1);
            Gl.VertexAttribIPointer(1, 1, VertexAttribIType.Int, (uint)sizeof(RenderNote), (void*)4);
            Gl.VertexAttribDivisor(1, 1);

            Gl.EnableVertexAttribArray(2);
            Gl.VertexAttribIPointer(2, 1, VertexAttribIType.UnsignedInt, (uint)sizeof(RenderNote), (void*)8);
            Gl.VertexAttribDivisor(2, 1);

            Gl.BindVertexArray(0);
            Gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

            _ringCap = cap;
        }

        public static void InitializeForMIDI()
        {
            _isInitialized = false;
            //NativeMemory.Clear(_keyHeaders, TOTAL_KEYS * (nuint)sizeof(KeyHeader));
            NativeMemory.Clear(_activeKeyCount, TOTAL_KEYS * (nuint)sizeof(ushort));
            NativeMemory.Clear(_activeKeyColor, TOTAL_KEYS * (nuint)sizeof(byte));
            NativeMemory.Clear(_activeKeyID, TOTAL_KEYS * (nuint)sizeof(int));
            _head = 1;
            _tail = 1;
            _lastSweepEnd = -1;
            _lastWindowTicks = -1;
            _paletteUploadPending = true;
            _isInitialized = true;
        }

        public static void ResetForUnload()
        {
            _isInitialized = false;
            _head = 1;
            _tail = 1;
            _lastSweepEnd = -1;
            _lastWindowTicks = -1;
            if (_ringCap != 1 << 23)
                _deferred_ringCap = 1 << 23;
        }

        public static void Dispose()
        {
            _isInitialized = false;
            //if (_keyHeaders != null) { NativeMemory.Free(_keyHeaders); _keyHeaders = null; }
            if (_activeKeyCount != null) { NativeMemory.Free(_activeKeyCount); _activeKeyCount = null; }
            if (_activeKeyColor != null) { NativeMemory.Free(_activeKeyColor); _activeKeyColor = null; }
            if (_activeKeyID != null) { NativeMemory.Free(_activeKeyID); _activeKeyID = null; }
            if (_vboBuffer != 0)
            {
                Gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vboBuffer);
                Gl.UnmapBuffer(BufferTargetARB.ArrayBuffer);
                Gl.DeleteBuffer(_vboBuffer);
                _vboBuffer = 0;
                _ring = null;
            }
            if (_paletteTex != 0) { Gl.DeleteTexture(_paletteTex); _paletteTex = 0; }
            if (_vao != 0) { Gl.DeleteVertexArray(_vao); _vao = 0; }
            if (_lineShader != 0) { Gl.DeleteProgram(_lineShader); _lineShader = 0; }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int screenWidth, int screenHeight, int tick, int pad)
        {
            if (!MIDILoader.midiLoaded || !_isInitialized)
                return;

            if (_paletteUploadPending)
            {
                for (int i = 0; i < 256; i++)
                {
                    uint c = (uint)Random.Shared.Next(0x808080, 0x1000000);
                    paletteData[i * 3 + 0] = (byte)((c >> 16) & 0xFF);
                    paletteData[i * 3 + 1] = (byte)((c >> 8) & 0xFF);
                    paletteData[i * 3 + 2] = (byte)(c & 0xFF);
                }
                Gl.BindTexture(TextureTarget.Texture1D, _paletteTex);
                fixed (byte* ptr = paletteData)
                    Gl.TexImage1D(TextureTarget.Texture1D, 0, InternalFormat.Rgb8, 256, 0, PixelFormat.Rgb, PixelType.UnsignedByte, ptr);
                Gl.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                Gl.BindTexture(TextureTarget.Texture1D, 0);
                _paletteUploadPending = false;
            }

            if (_deferred_ringCap > 0)
            {
                ResizeRing(_deferred_ringCap);
                _deferred_ringCap = -1;
            }

            int maxtick = MIDILoader.maxTick - 1;
            int half = WindowTicks >> 1;
            int viewStart = Math.Clamp(tick - half, 0, maxtick);
            int viewEnd = Math.Clamp(tick + half, 0, maxtick);

            if (WindowTicks != _lastWindowTicks)
            {
                _pixelsPerTick = 2.0f / WindowTicks;
                _lastWindowTicks = WindowTicks;
                _lookaheadTicks = Math.Min(WindowTicks / 2, 2000);
            }

            int sweepEnd = Math.Clamp(viewEnd + _lookaheadTicks, 0, maxtick);
            bool incremental = _lastSweepEnd >= 0 && sweepEnd >= _lastSweepEnd && sweepEnd - _lastSweepEnd < WindowTicks;

            if (!incremental)
            {
                _head = 1;
                _tail = 1;
                NativeMemory.Clear(_activeKeyCount, TOTAL_KEYS * (nuint)sizeof(ushort));
                NativeMemory.Clear(_activeKeyColor, TOTAL_KEYS * (nuint)sizeof(byte));
                NativeMemory.Clear(_activeKeyID, TOTAL_KEYS * (nuint)sizeof(int));
                SweepRange(Math.Max(0, viewStart - WindowTicks), sweepEnd);
            }
            else
            {
                SweepRange(_lastSweepEnd + 1, sweepEnd);
            }

            _lastSweepEnd = sweepEnd;
            AdvanceTail(viewStart);
            // i dont know why the renderer in general became way faster. im assuming its probably due to the bulk copy overhead
            // that got removed since it pretty much copies every visible note instead of newly appended ones.
            // oh well! 2x memory saves yet again!!! yay!!!!!!!!!

            Raylib_cs.Rlgl.DrawRenderBatchActive();

            NotesDrawnLastFrame = _head - _tail;
            RingCap = _ringCap;

            if (NotesDrawnLastFrame > 0)
            {
                float yBottom = -1.0f + 2.0f * pad / screenHeight;
                float yTop = 1.0f - 2.0f * pad / screenHeight;
                float yStep = (yTop - yBottom) / 128.0f;

                Gl.Viewport(0, 0, (uint)screenWidth, (uint)screenHeight);
                Gl.Enable(EnableCap.DepthTest);
                Gl.DepthFunc(DepthFunction.Lequal);
                Gl.UseProgram(_lineShader);

                Gl.Uniform3(_uMetrics, _pixelsPerTick, yBottom, yStep);
                Gl.Uniform1(_uViewStart, viewStart);
                Gl.Uniform1(_uViewEnd, viewEnd);
                Gl.Uniform1(_uGlowEnabled, EnableGlow ? 1 : 0);
                Gl.Uniform1(_uTransparencyEnabled, EnableTransparency ? 1 : 0);
                Gl.Uniform1(_uCurrentTick, tick);

                Gl.ActiveTexture(TextureUnit.Texture0);
                Gl.BindTexture(TextureTarget.Texture1D, _paletteTex);

                Gl.BindVertexArray(_vao);

                int startIdx = _tail & _mask;
                if (startIdx + NotesDrawnLastFrame <= _ringCap)
                {
                    Gl.DrawArraysInstancedBaseInstance(PrimitiveType.TriangleStrip, 0, 4,
                        (uint)NotesDrawnLastFrame, (uint)startIdx);
                }
                else
                {
                    int firstChunk = _ringCap - startIdx;
                    int secondChunk = NotesDrawnLastFrame - firstChunk;
                    Gl.DrawArraysInstancedBaseInstance(PrimitiveType.TriangleStrip, 0, 4,
                        (uint)firstChunk, (uint)startIdx);
                    Gl.DrawArraysInstancedBaseInstance(PrimitiveType.TriangleStrip, 0, 4,
                        (uint)secondChunk, 0);
                }

                Gl.BindVertexArray(0);
                Gl.ActiveTexture(TextureUnit.Texture0);
                Gl.BindTexture(TextureTarget.Texture1D, 0);
                Gl.UseProgram(0);
                Gl.Disable(EnableCap.DepthTest);
            }
            int linepos = (int)Math.Min(tick * _pixelsPerTick * screenWidth / 2, screenWidth / 2);
            Raylib_cs.Raylib.DrawLine(linepos, 0, linepos, screenHeight, Raylib_cs.Color.Red);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SweepRange(int fromTick, int toTick)
        {
            TickGroup* group = MIDIEvent.TickGroupArray.Pointer;
            byte* messages = (byte*)SynthEvent.messages.Pointer;
            byte* tracks = SynthEvent.track != null ? SynthEvent.track.Pointer : null;

            int limit = Math.Min(toTick, MIDILoader.maxTick);
            int headLocal = _head;

            long currentOffset = group[fromTick].offset;
            for (int tick = fromTick; tick <= limit; tick++)
            {
                long nextOffset = group[tick + 1].offset;
                while (headLocal - _tail + (nextOffset - currentOffset) >= _mask + 1)
                {
                    _head = headLocal;
                    ResizeRing((_mask + 1) * 2);
                }
                currentOffset = ProcessTickEvents(messages, tracks, _activeKeyColor, _activeKeyCount, _activeKeyID, _ring, _mask, currentOffset, nextOffset, tick, ref headLocal);
            }
            _head = headLocal;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
        private static long ProcessTickEvents(byte* messages, byte* tracks, byte* activekeycolor, ushort* activecount,
            int* activekeyid, RenderNote* ringLocal, int maskLocal, long currentOffset, long nextOffset, int tick, ref int headLocal)
        {
            if (Ssse3.IsSupported)
            {
                // process 4 events at a time while enough remain in this tick
                while (nextOffset - currentOffset >= 4)
                {
                    byte* synthev = messages + (currentOffset * 3);
                    Vector128<byte> raw = Sse3.LoadVector128(synthev);

                    Vector128<byte> statusVec = Ssse3.Shuffle(raw, StatusShuffleMask);
                    Vector128<byte> keyVec = Ssse3.Shuffle(raw, KeyShuffleMask);

                    uint statusPacked = statusVec.AsUInt32().ToScalar(); // byte0=status0, byte1=status1, etc
                    uint keyPacked = keyVec.AsUInt32().ToScalar();

                    // tracks is 1 byte/event so no deinterleave is needed
                    uint trackPacked = tracks != null ? *(uint*)(tracks + currentOffset) : 0;

                    for (int lane = 0; lane < 4; lane++)
                    {
                        byte status = (byte)((statusPacked >> (lane * 8)) & 0xFF);
                        byte key = (byte)((keyPacked >> (lane * 8)) & 0xFF);
                        byte track = (byte)((trackPacked >> (lane * 8)) & 0xFF);

                        ProcessOneEvent(messages, currentOffset + lane, status, key, track,
                            activekeycolor, activecount, activekeyid, ringLocal, maskLocal, tick, ref headLocal);
                    }
                    currentOffset += 4;
                }
            }

            // normal loop for remainder, also non-ssse3 fallback
            while (currentOffset < nextOffset)
            {
                byte* synthev = messages + (currentOffset * 3);
                ProcessOneEvent(messages, currentOffset, *synthev, synthev[1], tracks != null ? tracks[currentOffset] : (byte)0,
                    activekeycolor, activecount, activekeyid, ringLocal, maskLocal, tick, ref headLocal);
                currentOffset++;
            }
            return currentOffset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ProcessOneEvent(byte* messages, long offset, byte status, byte key, byte track,
            byte* activekeycolor, ushort* activecount, int* activekeyid, RenderNote* ringLocal,
            int maskLocal, int tick, ref int headLocal)
        {
            uint noteIdx = (uint)track | (uint)(status & 0x0F);
            int headerIdx = (int)((noteIdx & 0x0Fu) << 7 | key);
            ushort count = activecount[headerIdx];
            byte activecolor = activekeycolor[headerIdx];
            uint statusHigh = (uint)status & 0xF0u;

            if (statusHigh == 0x90)
            {
                if (count != 0 && activecolor != noteIdx)
                {
                    int oldAbsid = activekeyid[headerIdx];
                    if (oldAbsid >= headLocal - (maskLocal + 1))
                        ringLocal[oldAbsid & maskLocal].EndTick = tick;
                    count = 0;
                }
                if (count == 0)
                {
                    activekeyid[headerIdx] = headLocal;
                    byte velocity = messages[offset * 3 + 2]; // only touched on open, stays scalar
                    ringLocal[headLocal & maskLocal] = new RenderNote
                    {
                        StartTick = tick,
                        EndTick = 0,
                        PackedData = (noteIdx << 16) | ((uint)key << 8) | velocity
                    };
                    headLocal++;
                    activekeycolor[headerIdx] = (byte)noteIdx;
                }
                count++;
            }
            else if (statusHigh == 0x80)
            {
                if (count > 0 && activecolor == noteIdx)
                {
                    count--;
                    if (count == 0)
                    {
                        int absid = activekeyid[headerIdx];
                        if (absid >= headLocal - (maskLocal + 1))
                            ringLocal[absid & maskLocal].EndTick = tick;
                    }
                }
            }
            activecount[headerIdx] = count;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AdvanceTail(int viewStart)
        {
            int safeTail = _head - _ringCap;
            if (_tail < safeTail)
                _tail = safeTail;

            int forcecullthresh = Math.Min(_lookaheadTicks * 2, ushort.MaxValue);
            bool forceCull = UseForceCull && NotesDrawnLastFrame > 262144;
            int forceCullBefore = viewStart - forcecullthresh;

            RenderNote* ring = _ring;
            int maskLocal = _mask;
            int headLocal = _head;
            int tailLocal = _tail;

            while (tailLocal < headLocal)
            {
                int physIdx = tailLocal & maskLocal;
                RenderNote note = ring[physIdx];
                bool isopen = note.EndTick == 0;
                int startTick = note.StartTick;

                if ((!isopen && note.EndTick < viewStart) || (forceCull && startTick < forceCullBefore))
                    tailLocal++;
                else
                    break;
            }
            _tail = tailLocal;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ResizeRing(int newCap)
        {
            if (newCap < 0) return;
            int newMask = newCap - 1;

            nuint totalBytes = (nuint)newCap * (nuint)sizeof(RenderNote);
            uint newBuffer = Gl.GenBuffer();
            Gl.BindBuffer(BufferTargetARB.ArrayBuffer, newBuffer);
            Gl.BufferStorage(GLEnum.ArrayBuffer, totalBytes, null, (uint)storageFlags);
            RenderNote* newRing = (RenderNote*)Gl.MapBufferRange(BufferTargetARB.ArrayBuffer, 0, totalBytes, (uint)accessFlags);

            if (_head > _tail)
            {
                int remaining = _head - _tail;
                int absId = _tail;
                while (remaining > 0)
                {
                    int oldIdx = absId & _mask;
                    int newIdx = absId & newMask;
                    int chunk = Math.Min(remaining, Math.Min(_ringCap - oldIdx, newCap - newIdx));
                    Unsafe.CopyBlock(newRing + newIdx, _ring + oldIdx, (uint)(chunk * sizeof(RenderNote)));
                    absId += chunk;
                    remaining -= chunk;
                }
            }

            if (_vboBuffer != 0)
            {
                Gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vboBuffer);
                Gl.UnmapBuffer(BufferTargetARB.ArrayBuffer);
                Gl.DeleteBuffer(_vboBuffer);
            }

            _vboBuffer = newBuffer;
            _ring = newRing;
            _mask = newMask;
            _ringCap = newCap;

            // re-bind attribute pointers to the new buffer
            Gl.BindVertexArray(_vao);
            Gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vboBuffer);
            Gl.VertexAttribIPointer(0, 1, VertexAttribIType.Int, (uint)sizeof(RenderNote), (void*)0);
            Gl.VertexAttribIPointer(1, 1, VertexAttribIType.Int, (uint)sizeof(RenderNote), (void*)4);
            Gl.VertexAttribIPointer(2, 1, VertexAttribIType.UnsignedInt, (uint)sizeof(RenderNote), (void*)8);
            Gl.BindVertexArray(0);
            Gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }

        private static uint BuildShader(string vert, string frag)
        {
            uint vertex = CompileStage(ShaderType.VertexShader, vert);
            uint fragment = CompileStage(ShaderType.FragmentShader, frag);
            uint program = Gl.CreateProgram();
            Gl.AttachShader(program, vertex);
            Gl.AttachShader(program, fragment);
            Gl.LinkProgram(program);
            Gl.GetProgram(program, GLEnum.LinkStatus, out int ok);
            if (ok == 0)
                throw new Exception("Shader link:\n" + Gl.GetProgramInfoLog(program));
            Gl.DeleteShader(vertex);
            Gl.DeleteShader(fragment);
            return program;
        }

        private static uint CompileStage(ShaderType type, string src)
        {
            uint shader = Gl.CreateShader(type);
            Gl.ShaderSource(shader, src);
            Gl.CompileShader(shader);
            Gl.GetShader(shader, GLEnum.CompileStatus, out int ok);
            if (ok == 0)
                throw new Exception($"{type}:\n" + Gl.GetShaderInfoLog(shader));
            return shader;
        }

        public class NativeGLBindingsContext
        {
            private static readonly nint s_glLibrary = LoadGL();
            private static nint LoadGL()
            {
                if (OperatingSystem.IsWindows())
                    return NativeLibrary.Load("opengl32.dll");
                if (OperatingSystem.IsLinux())
                    return NativeLibrary.TryLoad("libGL.so.1", out nint h) ? h : NativeLibrary.Load("libGL.so");
                if (OperatingSystem.IsMacOS())
                    return NativeLibrary.Load("/System/Library/Frameworks/OpenGL.framework/OpenGL");
                throw new PlatformNotSupportedException();
            }
            [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", ExactSpelling = true)]
            private static extern nint WglGetProcAddress(string name);
            public static nint GetProcAddress(string procName)
            {
                if (OperatingSystem.IsWindows())
                {
                    nint addr = WglGetProcAddress(procName);
                    if (addr is 0 or 1 or 2 or 3 or -1)
                        NativeLibrary.TryGetExport(s_glLibrary, procName, out addr);
                    return addr;
                }
                NativeLibrary.TryGetExport(s_glLibrary, procName, out nint unixAddr);
                return unixAddr;
            }
        }
    }
}
