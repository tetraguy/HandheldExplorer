using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace HandheldExplorer.Services;

public enum PadButton { Up, Down, Left, Right, A, B, X, Y, LB, RB, LT, RT, View, Menu, LS, RS }

/// <summary>
/// Polls XInput controllers (the built-in pad on ROG Ally, Legion Go, MSI Claw, Steam Deck with
/// drivers, plus any Xbox-style pad) and raises button presses with auto-repeat for navigation.
/// </summary>
public sealed class GamepadService
{
    private const int ButtonCount = 16;
    private const int StickDeadzone = 14000;
    private const int TriggerThreshold = 100;
    private const int FirstRepeatMs = 380;
    private const int RepeatMs = 85;

    private readonly DispatcherTimer _timer;
    private readonly long[] _nextRepeat = new long[ButtonCount];
    private readonly long[] _nextProbe = new long[4];
    private readonly bool[] _slotConnected = new bool[4];
    private uint _previous;

    public event Action<PadButton>? Pressed;
    public event Action<bool>? ConnectionChanged;
    public bool IsConnected { get; private set; }

    public GamepadService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Poll()
    {
        long now = Environment.TickCount64;
        uint mask = 0;
        bool any = false;

        for (uint i = 0; i < 4; i++)
        {
            // Probing empty slots is slow, so only retry them every couple of seconds.
            if (!_slotConnected[i] && now < _nextProbe[i]) continue;

            uint result;
            XINPUT_STATE state;
            try { result = XInputGetState(i, out state); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _timer.Stop(); // XInput not available on this machine
                return;
            }

            if (result == 0)
            {
                _slotConnected[i] = true;
                any = true;
                mask |= Translate(state.Gamepad);
            }
            else
            {
                _slotConnected[i] = false;
                _nextProbe[i] = now + 2000;
            }
        }

        if (any != IsConnected)
        {
            IsConnected = any;
            ConnectionChanged?.Invoke(any);
        }

        uint previous = _previous;
        _previous = mask;

        for (int b = 0; b < ButtonCount; b++)
        {
            uint bit = 1u << b;
            bool down = (mask & bit) != 0;
            bool wasDown = (previous & bit) != 0;

            if (down && !wasDown)
            {
                _nextRepeat[b] = now + FirstRepeatMs;
                Pressed?.Invoke((PadButton)b);
            }
            else if (down && Repeats((PadButton)b) && now >= _nextRepeat[b])
            {
                _nextRepeat[b] = now + RepeatMs;
                Pressed?.Invoke((PadButton)b);
            }
        }
    }

    private static bool Repeats(PadButton b) =>
        b is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right or PadButton.LT or PadButton.RT;

    private static uint Bit(PadButton b) => 1u << (int)b;

    private static uint Translate(XINPUT_GAMEPAD pad)
    {
        uint mask = 0;
        ushort w = pad.wButtons;
        if ((w & 0x0001) != 0) mask |= Bit(PadButton.Up);
        if ((w & 0x0002) != 0) mask |= Bit(PadButton.Down);
        if ((w & 0x0004) != 0) mask |= Bit(PadButton.Left);
        if ((w & 0x0008) != 0) mask |= Bit(PadButton.Right);
        if ((w & 0x0010) != 0) mask |= Bit(PadButton.Menu);
        if ((w & 0x0020) != 0) mask |= Bit(PadButton.View);
        if ((w & 0x0040) != 0) mask |= Bit(PadButton.LS);
        if ((w & 0x0080) != 0) mask |= Bit(PadButton.RS);
        if ((w & 0x0100) != 0) mask |= Bit(PadButton.LB);
        if ((w & 0x0200) != 0) mask |= Bit(PadButton.RB);
        if ((w & 0x1000) != 0) mask |= Bit(PadButton.A);
        if ((w & 0x2000) != 0) mask |= Bit(PadButton.B);
        if ((w & 0x4000) != 0) mask |= Bit(PadButton.X);
        if ((w & 0x8000) != 0) mask |= Bit(PadButton.Y);
        if (pad.bLeftTrigger > TriggerThreshold) mask |= Bit(PadButton.LT);
        if (pad.bRightTrigger > TriggerThreshold) mask |= Bit(PadButton.RT);

        // Left stick acts as a D-pad along its dominant axis.
        int x = pad.sThumbLX, y = pad.sThumbLY;
        if (Math.Abs(x) > StickDeadzone || Math.Abs(y) > StickDeadzone)
        {
            if (Math.Abs(x) > Math.Abs(y)) mask |= Bit(x > 0 ? PadButton.Right : PadButton.Left);
            else mask |= Bit(y > 0 ? PadButton.Up : PadButton.Down);
        }
        return mask;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint index, out XINPUT_STATE state);
}
