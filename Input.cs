using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace triclapclap.Input
{
    public class RawInputReceiver
    {
        public event Action<Keys, bool> OnKeyStateChanged = delegate { };

        private const int RIDEV_INPUTSINK = 0x00000100;
        private const int WM_INPUT = 0x00FF;
        private const int RIM_TYPEKEYBOARD = 1;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint Type;
            public uint Size;
            public IntPtr Device;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        public void Register(IntPtr windowHandle)
        {
            RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[1];
            devices[0].UsagePage = 0x01;
            devices[0].Usage = 0x06;
            devices[0].Flags = RIDEV_INPUTSINK;
            devices[0].Target = windowHandle;

            RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        public bool ProcessMessage(ref Message m)
        {
            if (m.Msg != WM_INPUT) return false;

            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));

            GetRawInputData(m.LParam, 0x10000003, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return false;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(m.LParam, 0x10000003, buffer, ref size, headerSize) != size) return false;

                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
                if (header.Type == RIM_TYPEKEYBOARD)
                {
                    IntPtr keyboardPtr = new IntPtr(buffer.ToInt64() + headerSize);
                    var kb = Marshal.PtrToStructure<RAWKEYBOARD>(keyboardPtr);

                    Keys key = (Keys)kb.VKey;
                    if (kb.Message == WM_KEYDOWN || kb.Message == WM_SYSKEYDOWN)
                        OnKeyStateChanged(key, true);
                    else if (kb.Message == WM_KEYUP || kb.Message == WM_SYSKEYUP)
                        OnKeyStateChanged(key, false);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return true;
        }
    }
}