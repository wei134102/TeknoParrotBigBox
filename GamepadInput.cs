using System;
using System.Runtime.InteropServices;

namespace TeknoParrotBigBox
{
    /// <summary>
    /// 手柄输入轮询：支持 XInput（Xbox/兼容手柄）和 DirectInput 风格摇杆（winmm joyGetPosEx）。
    /// </summary>
    public static class GamepadInput
    {
        #region XInput (xinput1_4.dll)

        private const string XInputDll = "xinput1_4.dll";

        [DllImport(XInputDll, EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001;
        private const ushort XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
        private const ushort XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
        private const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
        private const ushort XINPUT_GAMEPAD_A = 0x1000;
        private const ushort XINPUT_GAMEPAD_B = 0x2000;
        private const ushort XINPUT_GAMEPAD_X = 0x4000;
        private const ushort XINPUT_GAMEPAD_Y = 0x8000;
        private const ushort XINPUT_GAMEPAD_BACK = 0x0020;
        private const ushort XINPUT_GAMEPAD_START = 0x0010;
        private const int XINPUT_THUMB_DEADZONE = 0x4000; // 约 25%

        private const uint ERROR_DEVICE_NOT_CONNECTED = 0x48F;

        #endregion

        #region WinMM Joystick (DirectInput 风格手柄通常也暴露为 legacy 摇杆)

        private const string WinMm = "winmm.dll";

        [DllImport(WinMm, EntryPoint = "joyGetPosEx")]
        private static extern int JoyGetPosEx(uint uJoyID, ref JOYINFOEX pji);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOYINFOEX
        {
            public uint dwSize;
            public uint dwFlags;
            public uint dwXpos;
            public uint dwYpos;
            public uint dwZpos;
            public uint dwRpos;
            public uint dwUpos;
            public uint dwVpos;
            public uint dwButtons;
            public uint dwButtonNumber;
            public uint dwPOV;
            public uint dwReserved1;
            public uint dwReserved2;
        }

        private const uint JOY_RETURNALL = 0x000000FF;
        private const uint JOYSTICKID1 = 0;
        private const int JOYERR_NOERROR = 0;
        private const int JOYERR_UNPLUGGED = 116;
        private const uint POV_CENTER = 65535;
        private const uint POV_UP = 0;
        private const uint POV_RIGHT = 9000;
        private const uint POV_DOWN = 18000;
        private const uint POV_LEFT = 27000;
        private const uint POV_THRESHOLD = 4500; // 45 度内算该方向

        #endregion

        /// <summary>
        /// 当前帧的手柄“逻辑状态”：方向与确认/返回，用于与上一帧做边沿检测。
        /// </summary>
        public struct GamepadState
        {
            public bool Left;
            public bool Right;
            public bool Up;
            public bool Down;
            public bool A;   // 确认 / 启动
            public bool B;   // 返回 / 退出
            public bool HasInput; // 至少有一个有效输入源
        }

        /// <summary>
        /// 轮询第一个 XInput 手柄和第一个 legacy 摇杆，合并为统一逻辑状态（任一有效即 HasInput）。
        /// </summary>
        public static GamepadState Poll()
        {
            var state = new GamepadState();

            // 1) XInput 控制器 0
            if (XInputGetState(0, out XINPUT_STATE xi) == 0)
            {
                state.HasInput = true;
                ushort b = xi.Gamepad.wButtons;
                state.Up |= (b & XINPUT_GAMEPAD_DPAD_UP) != 0;
                state.Down |= (b & XINPUT_GAMEPAD_DPAD_DOWN) != 0;
                state.Left |= (b & XINPUT_GAMEPAD_DPAD_LEFT) != 0;
                state.Right |= (b & XINPUT_GAMEPAD_DPAD_RIGHT) != 0;
                state.A |= (b & (XINPUT_GAMEPAD_A | XINPUT_GAMEPAD_START)) != 0;
                state.B |= (b & (XINPUT_GAMEPAD_B | XINPUT_GAMEPAD_BACK)) != 0;

                // 左摇杆模拟方向（带死区）
                short lx = xi.Gamepad.sThumbLX, ly = xi.Gamepad.sThumbLY;
                if (lx < -XINPUT_THUMB_DEADZONE) state.Left = true;
                if (lx > XINPUT_THUMB_DEADZONE) state.Right = true;
                if (ly > XINPUT_THUMB_DEADZONE) state.Up = true;   // Y 向上为正
                if (ly < -XINPUT_THUMB_DEADZONE) state.Down = true;
            }

            // 2) Legacy 摇杆 (第一个)
            var ji = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf(typeof(JOYINFOEX)), dwFlags = JOY_RETURNALL };
            if (JoyGetPosEx(JOYSTICKID1, ref ji) == JOYERR_NOERROR)
            {
                state.HasInput = true;
                uint pov = ji.dwPOV;
                if (pov != POV_CENTER)
                {
                    if (pov >= 36000 - POV_THRESHOLD || pov < 0 + POV_THRESHOLD) state.Up = true;
                    else if (pov >= 9000 - POV_THRESHOLD && pov <= 9000 + POV_THRESHOLD) state.Right = true;
                    else if (pov >= 18000 - POV_THRESHOLD && pov <= 18000 + POV_THRESHOLD) state.Down = true;
                    else if (pov >= 27000 - POV_THRESHOLD && pov <= 27000 + POV_THRESHOLD) state.Left = true;
                }
                // 摇杆轴线：假设 dwXpos/dwYpos 为 0-65535，中值约 32768
                uint mx = ji.dwXpos, my = ji.dwYpos;
                const uint dead = 16000;
                if (mx < 32768 - dead) state.Left = true;
                if (mx > 32768 + dead) state.Right = true;
                if (my < 32768 - dead) state.Up = true;
                if (my > 32768 + dead) state.Down = true;
                // 按钮：0=A/确认，1=B/返回
                uint bt = ji.dwButtons;
                if ((bt & 1) != 0) state.A = true;
                if ((bt & 2) != 0) state.B = true;
            }

            return state;
        }
    }
}
