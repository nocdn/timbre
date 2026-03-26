using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using timbre.Interop;
using timbre.Models;

namespace timbre.Services;

internal sealed class UnicodeTextTypingService : IUnicodeTextTypingService
{
    private const int HotkeyReleaseDelayMilliseconds = 30;
    private const int MaxBatchInputCount = 256;

    public UnicodeTextTypingService()
    {
    }

    public async Task TypeTextAsync(string text, HotkeyBinding? triggeringHotkey = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        DiagnosticsLogger.Info($"Unicode typing fallback started. TextLength={text.Length}, HasTriggeringHotkey={triggeringHotkey is not null}.");

        if (triggeringHotkey is not null)
        {
            SendInputEvents(CreateReleaseHotkeyInputs(triggeringHotkey));
            await Task.Delay(HotkeyReleaseDelayMilliseconds, cancellationToken);
        }

        var normalizedText = NormalizeLineEndings(text);
        var inputBatch = new List<NativeMethods.INPUT>(MaxBatchInputCount);

        foreach (var character in normalizedText)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendCharacterInputs(inputBatch, character);

            if (inputBatch.Count >= MaxBatchInputCount)
            {
                SendInputEvents(inputBatch.ToArray());
                inputBatch.Clear();
            }
        }

        if (inputBatch.Count > 0)
        {
            SendInputEvents(inputBatch.ToArray());
        }

        DiagnosticsLogger.Info($"Unicode typing fallback completed. TextLength={text.Length}, TotalElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace('\r', '\n');
    }

    private static void AppendCharacterInputs(List<NativeMethods.INPUT> inputBatch, char character)
    {
        if (character == '\n')
        {
            inputBatch.Add(CreateVirtualKeyInput((ushort)NativeMethods.VK_RETURN, false));
            inputBatch.Add(CreateVirtualKeyInput((ushort)NativeMethods.VK_RETURN, true));
            return;
        }

        inputBatch.Add(CreateUnicodeInput(character, false));
        inputBatch.Add(CreateUnicodeInput(character, true));
    }

    private static NativeMethods.INPUT CreateUnicodeInput(char character, bool keyUp)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = character,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP : NativeMethods.KEYEVENTF_UNICODE,
                },
            },
        };
    }

    private static NativeMethods.INPUT CreateVirtualKeyInput(ushort virtualKey, bool keyUp)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                },
            },
        };
    }

    private static NativeMethods.INPUT[] CreateReleaseHotkeyInputs(HotkeyBinding hotkey)
    {
        var inputs = new List<NativeMethods.INPUT>
        {
            CreateVirtualKeyInput((ushort)hotkey.KeyCode, true),
        };

        if (hotkey.Shift)
        {
            inputs.Add(CreateVirtualKeyInput((ushort)NativeMethods.VK_LSHIFT, true));
            inputs.Add(CreateVirtualKeyInput((ushort)NativeMethods.VK_RSHIFT, true));
        }

        if (hotkey.Alt)
        {
            inputs.Add(CreateVirtualKeyInput((ushort)NativeMethods.VK_LMENU, true));
            inputs.Add(CreateVirtualKeyInput((ushort)NativeMethods.VK_RMENU, true));
        }

        if (hotkey.Control)
        {
            inputs.Add(CreateVirtualKeyInput((ushort)NativeMethods.VK_LCONTROL, true));
            inputs.Add(CreateVirtualKeyInput((ushort)NativeMethods.VK_RCONTROL, true));
        }

        if (hotkey.Windows)
        {
            inputs.Add(CreateVirtualKeyInput((ushort)NativeMethods.VK_LWIN, true));
            inputs.Add(CreateVirtualKeyInput((ushort)NativeMethods.VK_RWIN, true));
        }

        return inputs.ToArray();
    }

    private static void SendInputEvents(NativeMethods.INPUT[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            var lastError = Marshal.GetLastWin32Error();
            var exception = new Win32Exception(lastError, "Failed to send keyboard input.");
            DiagnosticsLogger.Error($"SendInput failed while typing text. Requested={inputs.Length}, Sent={sent}, LastError={lastError}.", exception);
            throw exception;
        }
    }
}
