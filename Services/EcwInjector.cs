using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace MedScribeOS.Services;

public static class EcwInjector
{
    public record InjectResult(bool Success, string Message);

    // eCW titles its windows inconsistently across different screens - the
    // original desktop client window was "eClinicalWorks Application - ...",
    // but this specific HPI screen's window is titled "eCW (Nainan, Vidhya )"
    // instead. Accepting both patterns instead of one hardcoded prefix is
    // what actually matches reality here.
    private static readonly string[] EcwWindowTitlePrefixes = { "eClinicalWorks", "eCW" };

    // Confirmed via inspector capture: ROS's editor has a single stable
    // AutomationId across visits/patients - safe to target directly.
    private const string RosAutomationId = "web_rosNotesRightGrid";

    // Confirmed via inspector capture: HPI is per-problem, with a DIFFERENT
    // numeric id each time (hpinotessection304366, hpinotessection490537...).
    // There is no fixed id to target, so HPI injection instead requires the
    // provider's cursor to already be in the correct problem's HPI box -
    // we only verify that's true, we never guess which one to fill.
    private const string HpiAutomationIdPrefix = "hpinotessection";

    /// <summary>Injects into whatever field currently has OS focus. Used by live dictation.</summary>
    public static InjectResult TryInject(string text)
    {
        using var automation = new UIA3Automation();

        var focused = TryGetFocusedElement(automation, out var error);
        if (focused == null) return new InjectResult(false, error!);

        if (!IsInsideEcw(automation, focused))
        {
            var description = DescribeElementForDiagnostics(automation, focused);
            return new InjectResult(false,
                $"Focused field is not inside eCW - injection blocked for safety. Currently focused: {description}");
        }

        return PasteIntoCurrentFocus(text);
    }

    /// <summary>
    /// Describes whatever currently has focus (name, class, and its top-level
    /// window) so a "not inside eCW" failure tells you exactly what WAS
    /// focused instead of leaving you to guess at window arrangement.
    /// </summary>
    private static string DescribeElementForDiagnostics(AutomationBase automation, AutomationElement element)
    {
        try
        {
            var name = element.Properties.Name.ValueOrDefault ?? "(no name)";
            var className = element.Properties.ClassName.ValueOrDefault ?? "(no class)";

            var walker = automation.TreeWalkerFactory.GetControlViewWalker();
            var current = element;
            AutomationElement? topLevel = element;
            for (int depth = 0; depth < 25 && current != null; depth++)
            {
                var parent = walker.GetParent(current);
                if (parent == null) break;
                topLevel = current;
                current = parent;
            }
            var windowTitle = topLevel?.Properties.Name.ValueOrDefault ?? "(unknown window)";

            return $"'{name}' (class: {className}) in top-level window '{windowTitle}'";
        }
        catch (Exception ex)
        {
            return $"(couldn't describe focused element: {ex.Message})";
        }
    }

    /// <summary>
    /// Injects into the HPI box that currently has focus - but only if it
    /// actually IS an HPI box (AutomationId starts with "hpinotessection").
    /// Refuses otherwise, since HPI's per-problem ids mean we can never
    /// safely guess which problem's box to fill.
    /// </summary>
    public static InjectResult TryInjectIntoFocusedHpi(string text)
    {
        using var automation = new UIA3Automation();

        var focused = TryGetFocusedElement(automation, out var error);
        if (focused == null) return new InjectResult(false, error!);

        if (!IsInsideHpiSection(automation, focused))
        {
            return new InjectResult(false,
                "Click into the correct problem's HPI box first, then press this button again - " +
                "HPI has a separate box per active problem, so we only fill whichever one has your cursor in it.");
        }

        return PasteIntoCurrentFocus(text);
    }

    /// <summary>
    /// Injects directly into the ROS box by its (stable) AutomationId,
    /// regardless of what currently has focus - clicks into it first to get
    /// a real cursor placed inside the contenteditable region, then pastes.
    /// </summary>
    public static InjectResult TryInjectIntoRos(string text)
    {
        using var automation = new UIA3Automation();

        var ecwWindow = FindEcwTopLevelElement(automation);
        if (ecwWindow == null)
        {
            return new InjectResult(false, "Could not find the eCW window - is it open?");
        }

        AutomationElement? target;
        try
        {
            target = ecwWindow.FindFirstDescendant(cf => cf.ByAutomationId(RosAutomationId));
        }
        catch (Exception ex)
        {
            return new InjectResult(false, $"Error searching for the ROS field: {ex.Message}");
        }

        if (target == null)
        {
            return new InjectResult(false, "Could not find the ROS field - make sure the ROS screen is open for this visit.");
        }

        try
        {
            // A real click, not SetFocus() - contenteditable regions need an
            // actual click to place the text cursor correctly, the same way
            // a human clicking there would.
            target.Click();
        }
        catch (Exception ex)
        {
            return new InjectResult(false, $"Could not click into the ROS field: {ex.Message}");
        }

        return PasteIntoCurrentFocus(text);
    }

    private static AutomationElement? TryGetFocusedElement(UIA3Automation automation, out string? error)
    {
        try
        {
            var focused = automation.FocusedElement();
            if (focused == null)
            {
                error = "No element currently has focus.";
                return null;
            }
            error = null;
            return focused;
        }
        catch (Exception ex)
        {
            error = $"Couldn't read focused element: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Deliberately NOT using ValuePattern.SetValue() anywhere in this class.
    /// eCW's HPI/ROS editors are contenteditable wysihtml5 regions (confirmed:
    /// no ValuePattern present at all) inside an AngularJS app - SetValue()
    /// either has nothing to call, or writes a value without firing the
    /// 'input' event Angular's digest cycle listens for, so the field can
    /// look filled while the underlying model stays empty. Clipboard + Ctrl+V
    /// fires a real synthetic input event, same as a human pasting.
    /// </summary>
    private static InjectResult PasteIntoCurrentFocus(string text)
    {
        var previousClipboard = TryGetClipboardText();
        try
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                return new InjectResult(false,
                    $"Couldn't put the text on the clipboard to paste it: {ex.Message}");
            }

            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);

            // SendInput queues the keystroke and returns immediately - it does
            // NOT wait for the target application to actually process the
            // paste and read from the clipboard. Without this delay, the
            // clipboard gets restored to its OLD content below before eCW's
            // browser-rendered field gets around to reading what we just set,
            // so the OLD clipboard content (e.g. code you'd copied to send
            // elsewhere) gets pasted instead of the intended text. This is
            // very likely why code has been showing up inside eCW fields.
            System.Threading.Thread.Sleep(250);
        }
        finally
        {
            if (previousClipboard != null)
            {
                System.Windows.Clipboard.SetText(previousClipboard);
            }
        }

        return new InjectResult(true, "Injected via clipboard paste.");
    }

    private static bool IsInsideEcw(AutomationBase automation, AutomationElement element)
    {
        try
        {
            var focusedProcessId = element.Properties.ProcessId.ValueOrDefault;
            var ecwProcessId = FindEcwProcessId();
            if (ecwProcessId.HasValue && focusedProcessId == ecwProcessId.Value)
            {
                return true;
            }
        }
        catch
        {
            // fall through to the title-based check below
        }

        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var current = element;
        for (int depth = 0; depth < 25 && current != null; depth++)
        {
            var name = current.Properties.Name.ValueOrDefault ?? string.Empty;
            if (MatchesEcwTitle(name))
            {
                return true;
            }
            current = walker.GetParent(current);
        }

        return false;
    }

    private static bool MatchesEcwTitle(string title) =>
        EcwWindowTitlePrefixes.Any(prefix => title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Walks ancestors looking for a "hpinotessection*" AutomationId, confirming the focused element really is inside an HPI box.</summary>
    private static bool IsInsideHpiSection(AutomationBase automation, AutomationElement element)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var current = element;
        for (int depth = 0; depth < 25 && current != null; depth++)
        {
            var id = current.Properties.AutomationId.ValueOrDefault ?? string.Empty;
            if (id.StartsWith(HpiAutomationIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            current = walker.GetParent(current);
        }
        return false;
    }

    private static AutomationElement? FindEcwTopLevelElement(UIA3Automation automation)
    {
        var pid = FindEcwProcessId();
        if (!pid.HasValue) return null;

        try
        {
            var process = System.Diagnostics.Process.GetProcessById((int)pid.Value);
            var handle = process.MainWindowHandle;
            return handle == IntPtr.Zero ? null : automation.FromHandle(handle);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetClipboardText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
        }
        catch
        {
            return null;
        }
    }

    // --- Win32 fallback for finding eCW's process id by window title ---

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static uint? FindEcwProcessId()
    {
        uint? found = null;

        EnumWindows((hWnd, _) =>
        {
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();

            if (MatchesEcwTitle(title))
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                found = pid;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }
}