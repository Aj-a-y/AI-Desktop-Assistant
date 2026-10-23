using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace DesktopAssistant;

internal sealed record ScreenInspection(string Title, string Content);

internal sealed class ComputerModeService
{
    private const int MaxControls = 100;
    private const int MaxDepth = 5;
    private IReadOnlyList<InstalledApp>? _installedApps;
    private DateTime _installedAppsScannedAt;

    public IntPtr GetForegroundWindowHandle() => NativeMethods.GetForegroundWindow();

    public async Task<ScreenInspection> InspectWindowAsync(IntPtr windowHandle, CancellationToken cancellationToken = default)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("No other active window was found. Open a window, then try inspecting again.");
        }

        return await Task.Run(() => InspectWindow(windowHandle, cancellationToken), cancellationToken);
    }

    public string OpenSafeApplication(string appName)
    {
        var executable = appName switch
        {
            "Notepad" => "notepad.exe",
            "Calculator" => "calc.exe",
            _ => throw new ArgumentOutOfRangeException(nameof(appName), "That application is not in the safe app list.")
        };

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true
        });

        return $"Opened {appName}.";
    }

    public string OpenApplicationByName(string appName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = NormalizeAppName(appName);
        if (key.Length == 0 || key.Length > 80 || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Please give me a simple application name to open.");

        var apps = GetInstalledApps(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Start menu entries may contain several shortcuts for one program (for example,
        // Chrome app shortcuts with different arguments). Treat the executable as the
        // application identity and rank exact names above loose aliases.
        var matches = FindMatchingInstalledApps(key, apps).Take(2).ToArray();
        if (matches.Length == 1)
        {
            StartResolvedInstalledApp(matches[0], cancellationToken);
            return $"Opened {matches[0].Name}.";
        }
        if (matches.Length > 1)
        {
            // Prefer Google's regular Chrome install when the short alias was requested.
            // Beta, Dev, and Canary installs have separate executable paths and remain
            // distinguishable when the user asks for them by their full name.
            if (key is "chrome" or "googlechrome")
            {
                var standardChrome = matches.FirstOrDefault(match => IsStandardGoogleChrome(match));
                if (standardChrome is not null)
                {
                    StartResolvedInstalledApp(standardChrome, cancellationToken);
                    return $"Opened {standardChrome.Name}.";
                }
            }
            throw new InvalidOperationException($"I found more than one installed app matching {appName}: {string.Join(", ", matches.Select(match => match.Name))}. Use its full name.");
        }

        throw new InvalidOperationException($"I couldn't resolve {appName} to an installed app. Try the app's full name as shown in Start.");
    }

    private static void StartResolvedInstalledApp(InstalledApp app, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = app.StartInfo;
        var isPackagedApp = app.LaunchKey.StartsWith("appsfolder:", StringComparison.OrdinalIgnoreCase)
            && string.Equals(startInfo.FileName, "explorer.exe", StringComparison.OrdinalIgnoreCase)
            && startInfo.Arguments.StartsWith("\"shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
            && startInfo.Arguments.EndsWith('"');
        if (!isPackagedApp)
        {
            if (!Path.IsPathRooted(startInfo.FileName)
                || !Path.GetExtension(startInfo.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(startInfo.FileName))
                throw new InvalidOperationException($"The installed application '{app.Name}' no longer resolves to an existing executable. No app was opened.");
        }

        Process.Start(startInfo);
    }

    public async Task<string> SendWhatsAppMessageAsync(
        string recipient,
        string message,
        Func<string, bool> confirmSend,
        CancellationToken cancellationToken = default)
    {
        recipient = recipient.Trim();
        message = message.Trim();
        if (recipient.Length is < 1 or > 100 || message.Length is < 1 or > 2000)
            throw new InvalidOperationException("The contact name or message is empty or too long.");

        // Reuse an already-running WhatsApp window before resolving an install target.
        // Packaged apps can be visible and usable while their AppsFolder registration is
        // unavailable to the shortcut scanner, so requiring a fresh launch here can fail
        // even though the user already has WhatsApp open.
        // Window discovery and application resolution can walk slow Windows state;
        // start those operations on a worker before doing any synchronous work on the
        // caller (normally the WPF dispatcher) thread.
        var window = await Task.Run(() => NativeMethods.FindWindowTitleContaining("WhatsApp"), cancellationToken).ConfigureAwait(false);
        if (window == IntPtr.Zero)
        {
            await Task.Run(() => OpenApplicationByName("WhatsApp", cancellationToken), cancellationToken).ConfigureAwait(false);
            window = await WaitForWindowAsync("WhatsApp", TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
        }
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("WhatsApp did not open or expose a window. Open WhatsApp and sign in, then try again.");

        ActivateTarget(window);
        var root = AutomationElement.FromHandle(window);
        var activeChatName = FindVerifiedActiveChatHeader(root, window, recipient);
        string selectionDescription;
        if (activeChatName is not null)
        {
            recipient = activeChatName;
            selectionDescription = $"Verified already-open WhatsApp chat: {recipient}.";
        }
        else
        {
            PressKey(window, "Ctrl+N");
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            root = AutomationElement.FromHandle(window);
            AutomationElement? searchBox = null;
            try { searchBox = FindContactSearchInput(root, window); }
            catch (InvalidOperationException)
            {
                // Some WhatsApp builds expose no editable controls to UI Automation even
                // though Ctrl+N has focused the new-chat search. Send text to that focused
                // field as a fallback; never press Enter at this stage.
            }
            if (searchBox is not null) SetText(searchBox, recipient, window);
            else TypeText(window, recipient);
            await Task.Delay(700, cancellationToken).ConfigureAwait(false);

            root = AutomationElement.FromHandle(window);
            var normalizedRecipient = NormalizeContactName(recipient);
            var contactMatches = FindAllDescendants(root)
                .Where(element =>
                {
                    try
                    {
                        var type = element.Current.ControlType;
                        var name = element.Current.Name;
                        var normalizedName = NormalizeContactName(name);
                        var isContactRow = type == ControlType.ListItem || type == ControlType.DataItem
                            || type == ControlType.Button;
                        return isContactRow && !element.Current.IsOffscreen && normalizedName.Length > 0
                            && (normalizedName == normalizedRecipient || normalizedName.Contains(normalizedRecipient, StringComparison.Ordinal));
                    }
                    catch (ElementNotAvailableException) { return false; }
                })
                .Select(element => (Element: element, Name: SafeElementName(element)))
                .GroupBy(match => ContactElementIdentity(match.Element), StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(match => ContactElementRank(match.Element)).First())
                .OrderByDescending(match => NormalizeContactName(match.Name) == normalizedRecipient)
                .ThenBy(match => ContactElementTop(match.Element))
                .ToArray();

            var exactContactMatches = contactMatches
                .Where(match => ContactNamesMatchExactly(match.Name, recipient))
                .ToArray();
            if (exactContactMatches.Length > 1)
            {
                var names = string.Join(", ", exactContactMatches.Select(match => match.Name).Distinct(StringComparer.OrdinalIgnoreCase));
                throw new InvalidOperationException($"I found multiple WhatsApp contacts matching '{recipient}': {names}. No message was drafted or sent.");
            }
            if (exactContactMatches.Length == 0)
            {
                var candidates = contactMatches.Select(match => match.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
                var details = candidates.Length > 0 ? $" Possible results: {string.Join(", ", candidates)}." : " WhatsApp did not expose its contact results to Windows.";
                throw new InvalidOperationException($"I couldn't verify an exact WhatsApp contact match for '{recipient}'.{details} Open that chat manually and retry; I can reuse it if its header is visible to Windows. No message was drafted or sent.");
            }

            // Normalize away emoji and diacritics when matching, but require exactly one
            // selectable exact result. Never press Enter on an unverified top result.
            var selectedContact = exactContactMatches[0];
            recipient = selectedContact.Name;
            SelectContact(selectedContact.Element);
            selectionDescription = $"Verified exact WhatsApp contact: {recipient}.";
        }
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        root = AutomationElement.FromHandle(window);
        var previousMessageCount = CountVisibleMessageText(window, message);
        var usedCoordinateComposerFallback = false;
        AutomationElement? messageBox = null;
        try
        {
            messageBox = FindUniqueEdit(root, name => name.Contains("type a message", StringComparison.OrdinalIgnoreCase)
                || name.Equals("message", StringComparison.OrdinalIgnoreCase), "WhatsApp message field");
        }
        catch (InvalidOperationException)
        {
            // If the selected conversation does not expose its composer, WhatsApp
            // usually focuses the message field after a result is opened.
        }
        if (messageBox is not null)
        {
            SetText(messageBox, message, window);
            if (messageBox.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern)
                && !string.Equals(((ValuePattern)valuePattern).Current.Value, message, StringComparison.Ordinal))
                throw new InvalidOperationException("WhatsApp did not retain the complete message in its composer. No message was sent.");
        }
        else
        {
            var focused = AutomationElement.FocusedElement;
            var isMessageComposerFocused = false;
            try
            {
                isMessageComposerFocused = focused is not null && IsWithinWindow(focused, window)
                    && (focused.Current.ControlType == ControlType.Edit || focused.Current.ControlType == ControlType.Document
                        || focused.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                    && !focused.Current.Name.Contains("search", StringComparison.OrdinalIgnoreCase);
            }
            catch (ElementNotAvailableException) { }

            if (isMessageComposerFocused)
            {
                TypeText(window, message);
            }
            else
            {
                // The Microsoft Store WhatsApp client currently hides its composer from
                // UI Automation. Focus the message input in the conversation pane using
                // its stable bottom-row placement, then type only after the click.
                if (!NativeMethods.GetClientRect(window, out var clientBounds))
                    throw new InvalidOperationException("I couldn't measure the WhatsApp window to focus its message box. No message was sent.");
                var point = GetWhatsAppComposerClickPoint(clientBounds.Right, clientBounds.Bottom);
                ClickAt(window, point.X, point.Y);
                TypeText(window, message);
                usedCoordinateComposerFallback = true;
            }
        }

        if (!confirmSend($"Send this WhatsApp message?\n\n{selectionDescription}\n\nTo: {recipient}\n\nMessage:\n{message}\n\nCheck that the open WhatsApp chat is the intended person before sending."))
        {
            if (messageBox is not null) TryClear(messageBox);
            return usedCoordinateComposerFallback
                ? "Cancelled. The WhatsApp message was not sent. Check WhatsApp for an unsent draft before retrying."
                : "Cancelled. The WhatsApp message was not sent. The draft may remain in WhatsApp.";
        }

        if (messageBox is not null)
        {
            try { messageBox.SetFocus(); }
            catch (ElementNotAvailableException) { }
        }
        else if (usedCoordinateComposerFallback)
        {
            // The confirmation dialog takes keyboard focus. Re-focus the WhatsApp
            // composer after the user approves, otherwise Enter can go nowhere (or
            // reach the wrong window) on clients that hide the composer from UIA.
            if (!NativeMethods.GetClientRect(window, out var clientBounds))
                throw new InvalidOperationException("I couldn't re-focus WhatsApp after confirmation. The message was not sent.");
            var point = GetWhatsAppComposerClickPoint(clientBounds.Right, clientBounds.Bottom);
            ClickAt(window, point.X, point.Y);
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }
        PressKey(window, "Enter");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            if (IsWhatsAppSendVerified(previousMessageCount, CountVisibleMessageText(window, message), IsComposerEmpty(window)))
                return $"WhatsApp shows the message in {recipient}'s chat. Check the delivery indicator in WhatsApp for its final status.";
        }
        return $"I couldn't verify that WhatsApp added the message to {recipient}'s chat. Check WhatsApp before asking me to retry, so I don't send a duplicate.";
    }

    private static async Task<IntPtr> WaitForWindowAsync(string titlePart, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = NativeMethods.FindWindowTitleContaining(titlePart);
            if (handle != IntPtr.Zero) return handle;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return IntPtr.Zero;
    }

    private static AutomationElement FindUniqueEdit(AutomationElement root, Func<string, bool> nameMatches, string description)
    {
        var candidates = FindAllDescendants(root).Where(element =>
        {
            try
            {
                return element.Current.ControlType == ControlType.Edit && !element.Current.IsOffscreen
                    && nameMatches(element.Current.Name ?? string.Empty);
            }
            catch (ElementNotAvailableException) { return false; }
        }).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException(candidates.Length == 0
                ? $"I couldn't identify the {description}. WhatsApp may have changed its interface. No message was sent."
                : $"WhatsApp exposed more than one possible {description}. No message was sent.");
        return candidates[0];
    }

    private static string? FindVerifiedActiveChatHeader(AutomationElement root, IntPtr window, string requestedRecipient)
    {
        if (!NativeMethods.GetClientRect(window, out var clientBounds)) return null;
        var clientOrigin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(window, ref clientOrigin)) return null;

        var matches = FindAllDescendants(root)
            .Where(element =>
            {
                try
                {
                    var current = element.Current;
                    if (current.IsOffscreen || current.ControlType != ControlType.Text && current.ControlType != ControlType.Button)
                        return false;
                    var bounds = current.BoundingRectangle;
                    return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
                        && IsActiveWhatsAppChatHeaderCandidate(current.Name, requestedRecipient,
                            bounds.X, bounds.Y, clientOrigin.X, clientOrigin.Y, clientBounds.Right);
                }
                catch (ElementNotAvailableException) { return false; }
            })
            .GroupBy(ContactElementIdentity, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        return matches.Length == 1 ? SafeElementName(matches[0]) : null;
    }

    internal static bool IsActiveWhatsAppChatHeaderCandidate(
        string displayedName,
        string requestedRecipient,
        double elementLeft,
        double elementTop,
        double clientLeft,
        double clientTop,
        int clientWidth) =>
        ContactNamesMatchExactly(requestedRecipient, displayedName)
        && clientWidth >= 400
        && elementLeft >= clientLeft + clientWidth * 0.30
        && elementLeft <= clientLeft + clientWidth
        && elementTop >= clientTop
        && elementTop <= clientTop + 160;

    private static int CountVisibleMessageText(IntPtr window, string message)
    {
        try
        {
            return FindAllDescendants(AutomationElement.FromHandle(window)).Count(element =>
            {
                try
                {
                    var current = element.Current;
                    return !current.IsOffscreen
                        && (current.ControlType == ControlType.Text || current.ControlType == ControlType.ListItem || current.ControlType == ControlType.DataItem)
                        && current.Name.Contains(message, StringComparison.Ordinal);
                }
                catch (ElementNotAvailableException) { return false; }
            });
        }
        catch (ElementNotAvailableException) { return 0; }
    }

    private static bool IsComposerEmpty(IntPtr window)
    {
        try
        {
            var composers = FindAllDescendants(AutomationElement.FromHandle(window)).Where(element =>
            {
                try
                {
                    return !element.Current.IsOffscreen && element.Current.ControlType == ControlType.Edit
                        && (element.Current.Name.Contains("type a message", StringComparison.OrdinalIgnoreCase)
                            || element.Current.Name.Equals("message", StringComparison.OrdinalIgnoreCase));
                }
                catch (ElementNotAvailableException) { return false; }
            }).ToArray();
            return composers.Length == 1 && composers[0].TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                && string.IsNullOrEmpty(((ValuePattern)pattern).Current.Value);
        }
        catch (ElementNotAvailableException) { return false; }
    }

    internal static bool IsWhatsAppSendVerified(int previousMessageCount, int currentMessageCount, bool composerEmpty) =>
        currentMessageCount > previousMessageCount && composerEmpty;

    internal static (int X, int Y) GetWhatsAppComposerClickPoint(int clientWidth, int clientHeight)
    {
        if (clientWidth < 200 || clientHeight < 120)
            throw new InvalidOperationException("The WhatsApp window is too small to focus its message composer safely.");
        return (Math.Clamp((int)(clientWidth * 0.70), 0, clientWidth - 1),
            Math.Clamp(clientHeight - Math.Max(24, (int)(clientHeight * 0.07)), 0, clientHeight - 1));
    }

    private static AutomationElement FindContactSearchInput(AutomationElement root, IntPtr window)
    {
        var visible = FindAllDescendants(root).Where(element =>
        {
            try
            {
                var current = element.Current;
                return !current.IsOffscreen && (current.ControlType == ControlType.Edit
                    || current.ControlType == ControlType.ComboBox
                    || element.TryGetCurrentPattern(ValuePattern.Pattern, out _));
            }
            catch (ElementNotAvailableException) { return false; }
        }).ToArray();

        var namedSearch = visible.Where(element =>
        {
            try
            {
                var name = element.Current.Name;
                return name.Contains("search", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("name or number", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("new chat", StringComparison.OrdinalIgnoreCase);
            }
            catch (ElementNotAvailableException) { return false; }
        }).ToArray();
        if (namedSearch.Length == 1) return namedSearch[0];

        // Some WhatsApp builds give the search input no useful accessible name, but
        // Ctrl+N places keyboard focus in that field. Prefer that focused field.
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is not null && IsWithinWindow(focused, window)
                && (focused.Current.ControlType == ControlType.Edit
                    || focused.Current.ControlType == ControlType.ComboBox
                    || focused.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                && !focused.Current.Name.Contains("message", StringComparison.OrdinalIgnoreCase))
                return focused;
        }
        catch (ElementNotAvailableException) { }

        var plausible = visible.Where(element =>
        {
            try
            {
                var name = element.Current.Name;
                return !name.Contains("message", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("emoji", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("attach", StringComparison.OrdinalIgnoreCase);
            }
            catch (ElementNotAvailableException) { return false; }
        }).ToArray();
        if (plausible.Length == 1) return plausible[0];

        var labels = visible.Select(element =>
        {
            try { return string.IsNullOrWhiteSpace(element.Current.Name) ? "(unnamed)" : element.Current.Name; }
            catch (ElementNotAvailableException) { return "(unavailable)"; }
        }).Distinct(StringComparer.OrdinalIgnoreCase).Take(8);
        throw new InvalidOperationException($"I couldn't identify WhatsApp's contact search field. Visible editable controls: {string.Join(", ", labels)}. No message was sent.");
    }

    private static IEnumerable<AutomationElement> FindAllDescendants(AutomationElement root)
    {
        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        for (var index = 0; index < elements.Count; index++) yield return elements[index];
    }

    private void SetText(AutomationElement element, string text, IntPtr window)
    {
        element.SetFocus();
        ActivateTarget(window);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            ((ValuePattern)pattern).SetValue(text);
            return;
        }

        PressKey(window, "Ctrl+A");
        TypeText(window, text);
    }

    private static void SelectContact(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern))
        {
            ((SelectionItemPattern)selectionPattern).Select();
            return;
        }
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern))
        {
            ((InvokePattern)invokePattern).Invoke();
            return;
        }
        throw new InvalidOperationException("The WhatsApp contact result was not selectable. No message was sent.");
    }

    private static string NormalizeContactName(string name) =>
        new(name.Normalize(NormalizationForm.FormKD)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    internal static bool ContactNamesMatchExactly(string requested, string displayName) =>
        NormalizeContactName(requested) == NormalizeContactName(displayName);

    private static string SafeElementName(AutomationElement element)
    {
        try { return element.Current.Name.Trim(); }
        catch (ElementNotAvailableException) { return string.Empty; }
    }

    private static int ContactElementRank(AutomationElement element)
    {
        try
        {
            return element.Current.ControlType == ControlType.ListItem ? 4
                : element.Current.ControlType == ControlType.DataItem ? 3
                : element.Current.ControlType == ControlType.Button ? 2
                : 1;
        }
        catch (ElementNotAvailableException) { return 0; }
    }

    private static double ContactElementTop(AutomationElement element)
    {
        try { return element.Current.BoundingRectangle.Y; }
        catch (ElementNotAvailableException) { return double.MaxValue; }
    }

    private static string ContactElementIdentity(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            var bounds = current.BoundingRectangle;
            return $"{NormalizeContactName(current.Name)}|{bounds.X:0},{bounds.Y:0},{bounds.Width:0},{bounds.Height:0}";
        }
        catch (ElementNotAvailableException) { return Guid.NewGuid().ToString("N"); }
    }

    private static void TryClear(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                ((ValuePattern)pattern).SetValue(string.Empty);
        }
        catch (ElementNotAvailableException) { }
    }

    private IReadOnlyList<InstalledApp> GetInstalledApps(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_installedApps is not null && DateTime.UtcNow - _installedAppsScannedAt < TimeSpan.FromMinutes(3))
            return _installedApps;
        _installedApps = ScanInstalledApps(cancellationToken);
        _installedAppsScannedAt = DateTime.UtcNow;
        return _installedApps;
    }

    private static IReadOnlyList<InstalledApp> ScanInstalledApps(CancellationToken cancellationToken)
    {
        var apps = new List<InstalledApp>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToArray(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (target, arguments, workingDirectory) = ResolveShortcut(link);
                    var startInfo = CreateShortcutStartInfo(target, arguments, workingDirectory);
                    if (startInfo is not null)
                        apps.Add(new InstalledApp(Path.GetFileNameWithoutExtension(link), startInfo,
                            FileLaunchKey(startInfo.FileName, startInfo.Arguments)));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // Do not launch an unresolved shortcut: only resolved installed targets are eligible.
                }
            }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths is null) continue;
                foreach (var subKeyName in appPaths.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var appKey = appPaths.OpenSubKey(subKeyName);
                        var executable = appKey?.GetValue(null) as string;
                        var path = ExtractExecutablePath(executable);
                        if (path is null || !File.Exists(path)) continue;
                        var name = Path.GetFileNameWithoutExtension(subKeyName);
                        apps.Add(new InstalledApp(name,
                            new ProcessStartInfo { FileName = path, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" },
                            FileLaunchKey(path)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var appKey = uninstall.OpenSubKey(subKeyName);
                        var name = appKey?.GetValue("DisplayName") as string;
                        var rawIcon = appKey?.GetValue("DisplayIcon") as string;
                        var path = ExtractExecutablePath(rawIcon);
                        if (string.IsNullOrWhiteSpace(name) || path is null || !File.Exists(path)
                            || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        apps.Add(new InstalledApp(name,
                            new ProcessStartInfo { FileName = path, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" },
                            FileLaunchKey(path)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        }
        AddAppsFolderApps(apps, cancellationToken);
        return apps;
    }

    private static void AddAppsFolderApps(List<InstalledApp> apps, CancellationToken cancellationToken)
    {
        object? shell = null;
        object? folder = null;
        object? items = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return;
            dynamic shellObject = shell;
            folder = shellObject.NameSpace("shell:AppsFolder");
            if (folder is null) return;
            dynamic appFolder = folder;
            items = appFolder.Items();
            if (items is null) return;
            dynamic appItems = items;
            var count = (int)appItems.Count;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = appItems.Item(i);
                    if (item is null) continue;
                    dynamic appItem = item;
                    var name = (string?)appItem.Name;
                    var appId = (string?)appItem.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId)) continue;
                    var target = appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(appId)
                        ? new ProcessStartInfo { FileName = appId, UseShellExecute = true }
                        : new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"shell:AppsFolder\\{appId}\"", UseShellExecute = true };
                    apps.Add(new InstalledApp(name, target, $"appsfolder:{appId}"));
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { }
                finally { if (item is not null) ReleaseComObject(item); }
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { }
        finally
        {
            if (items is not null) ReleaseComObject(items);
            if (folder is not null) ReleaseComObject(folder);
            if (shell is not null) ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private static (string Target, string Arguments, string WorkingDirectory) ResolveShortcut(string link)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new COMException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            try
            {
                return ((string)shortcut.TargetPath, (string)shortcut.Arguments, (string)shortcut.WorkingDirectory);
            }
            finally { Marshal.ReleaseComObject(shortcut); }
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    private static ProcessStartInfo? CreateShortcutStartInfo(string target, string arguments, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;

        var targetPath = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        if (targetPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return null;
        if (targetPath.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)
            && arguments.Contains("AppsFolder", StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(targetPath))
            return new ProcessStartInfo { FileName = targetPath, Arguments = arguments, WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Path.GetDirectoryName(targetPath) ?? "", UseShellExecute = true };
        return null;
    }

    private static string? ExtractExecutablePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().Trim('"');
        if (value.StartsWith('"') && value.IndexOf('"', 1) is var endQuote && endQuote > 1)
            value = value[1..endQuote];
        else if (value.LastIndexOf(',') is var comma && comma > 0 && int.TryParse(value[(comma + 1)..], out _))
            value = value[..comma].Trim().Trim('"');
        return Environment.ExpandEnvironmentVariables(value);
    }

    private static string FileLaunchKey(string path, string arguments = "") =>
        $"{Path.GetFullPath(path).ToUpperInvariant()}\0{arguments.Trim()}";

    private static string ExecutableLaunchKey(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path.Trim().ToUpperInvariant();
        }
    }

    private static int GetAppNameMatchScore(string requested, InstalledApp app)
    {
        var candidate = NormalizeAppName(app.Name);
        if (requested == candidate) return 100;
        if (candidate.StartsWith(requested, StringComparison.Ordinal)) return 70;

        if (requested is "chrome" or "googlechrome")
        {
            return candidate is "googlechrome" or "chrome" ? 90 : 0;
        }
        if (requested is "edge" or "msedge")
            return candidate is "microsoftedge" or "edge" ? 90 : 0;
        if (requested is "vscode" or "code")
            return candidate is "visualstudiocode" or "code" ? 90 : 0;
        if (requested == "calc")
            return candidate == "calculator" ? 90 : 0;
        return 0;
    }

    internal static IReadOnlyList<InstalledApp> FindMatchingInstalledApps(string requested, IEnumerable<InstalledApp> apps)
    {
        var grouped = apps
            .Select(app => (App: app, Score: GetAppNameMatchScore(requested, app)))
            .Where(match => match.Score > 0)
            .GroupBy(match => ExecutableLaunchKey(match.App.StartInfo.FileName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(match => match.Score).First())
            .ToArray();
        var exact = grouped.Where(match => match.Score == 100).OrderBy(match => match.App.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        IEnumerable<(InstalledApp App, int Score)> selected = exact.Length > 0
            ? exact
            : grouped.OrderByDescending(match => match.Score).ThenBy(match => match.App.Name, StringComparer.OrdinalIgnoreCase);
        return selected.Select(match => match.App).ToArray();
    }

    private static bool IsStandardGoogleChrome(InstalledApp app)
    {
        var path = app.StartInfo.FileName;
        var normalizedPath = path.Replace('/', '\\');
        return Path.GetFileName(path).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.Contains("\\Google\\Chrome\\Application\\", StringComparison.OrdinalIgnoreCase)
            && !normalizedPath.Contains("Chrome Beta", StringComparison.OrdinalIgnoreCase)
            && !normalizedPath.Contains("Chrome Dev", StringComparison.OrdinalIgnoreCase)
            && !normalizedPath.Contains("Chrome SxS", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record InstalledApp(string Name, ProcessStartInfo StartInfo, string LaunchKey);

    private static string NormalizeAppName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public string ClickControl(IntPtr windowHandle, string controlName)
    {
        var element = FindControl(windowHandle, controlName);
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            throw new InvalidOperationException($"'{controlName}' does not expose a clickable action.");
        ((InvokePattern)pattern).Invoke();
        return $"Clicked '{element.Current.Name}'.";
    }

    public string ClickAt(IntPtr windowHandle, int x, int y)
    {
        EnsureTarget(windowHandle);
        if (!NativeMethods.GetClientRect(windowHandle, out var bounds) || x < 0 || y < 0 || x >= bounds.Right || y >= bounds.Bottom)
            throw new InvalidOperationException("That point is outside the active app's client area. Use x,y coordinates measured from its top-left corner.");
        var point = new NativeMethods.Point { X = x, Y = y };
        if (!NativeMethods.ClientToScreen(windowHandle, ref point)) throw new InvalidOperationException("Couldn't map that point to the active app.");
        ActivateTarget(windowHandle);
        NativeMethods.SetCursorPos(point.X, point.Y);
        NativeMethods.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        NativeMethods.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        return $"Clicked at app position {x},{y}.";
    }

    public string TypeText(IntPtr windowHandle, string text)
    {
        EnsureTarget(windowHandle);
        var focused = AutomationElement.FocusedElement;
        if (focused is not null && IsWithinWindow(focused, windowHandle)
            && focused.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
            && !((ValuePattern)pattern).Current.IsReadOnly)
        {
            var value = (ValuePattern)pattern;
            value.SetValue(value.Current.Value + text);
            return $"Typed {text.Length} characters into the focused field.";
        }

        ActivateTarget(windowHandle);
        var inputs = new NativeMethods.Input[text.Length * 2];
        for (var index = 0; index < text.Length; index++)
        {
            inputs[index * 2] = NativeMethods.CreateUnicodeInput(text[index], keyUp: false);
            inputs[index * 2 + 1] = NativeMethods.CreateUnicodeInput(text[index], keyUp: true);
        }
        var accepted = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        if (accepted == 0)
        {
            PasteTextWithClipboard(windowHandle, text);
            return $"Pasted {text.Length} characters into the active app.";
        }
        if (accepted != inputs.Length)
            throw new InvalidOperationException($"Windows accepted {accepted} of {inputs.Length} keyboard events while typing. The active app may have blocked simulated input. No message was sent.");
        return $"Typed {text.Length} characters into the active app.";
    }

    private static void PasteTextWithClipboard(IntPtr windowHandle, string text)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
                throw new InvalidOperationException("Windows rejected simulated typing, and clipboard paste requires the app's UI thread. No message was sent.");
            dispatcher.Invoke(() => PasteTextWithClipboard(windowHandle, text));
            return;
        }

        System.Windows.IDataObject? previousClipboard = null;
        var clipboardRead = false;
        try
        {
            previousClipboard = System.Windows.Clipboard.GetDataObject();
            clipboardRead = true;

            System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
            ActivateTarget(windowHandle);
            if (!NativeMethods.SendVirtualKey(0x11, keyUp: false))
                throw new InvalidOperationException("Windows rejected the paste shortcut. No message was sent.");
            try
            {
                if (!NativeMethods.SendVirtualKey(0x56, keyUp: false)
                    || !NativeMethods.SendVirtualKey(0x56, keyUp: true))
                    throw new InvalidOperationException("Windows rejected the paste shortcut. No message was sent.");
            }
            finally { _ = NativeMethods.SendVirtualKey(0x11, keyUp: true); }
            Thread.Sleep(100);
        }
        finally
        {
            if (clipboardRead)
            {
                try
                {
                    if (previousClipboard is null) System.Windows.Clipboard.Clear();
                    else System.Windows.Clipboard.SetDataObject(previousClipboard, copy: true);
                }
                catch (System.Runtime.InteropServices.ExternalException) { }
            }
        }
    }

    public string Scroll(IntPtr windowHandle, bool down, int notches)
    {
        EnsureTarget(windowHandle);
        var root = AutomationElement.FromHandle(windowHandle);
        var scrollables = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        for (var i = 0; i < scrollables.Count; i++)
        {
            var element = scrollables[i];
            if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)) continue;
            var scroll = (ScrollPattern)pattern;
            var amount = down ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement;
            if (!scroll.Current.VerticallyScrollable) continue;
            for (var n = 0; n < notches; n++) scroll.Scroll(ScrollAmount.NoAmount, amount);
            return $"Scrolled {(down ? "down" : "up")} {notches} step(s).";
        }

        ActivateTarget(windowHandle);
        NativeMethods.ScrollMouseWheel(windowHandle, down ? -120 * notches : 120 * notches);
        return $"Sent a {(down ? "down" : "up")} scroll to the active app.";
    }

    public string PressKey(IntPtr windowHandle, string keyName)
    {
        EnsureTarget(windowHandle);
        var parts = keyName.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 3) throw new InvalidOperationException("Use a key such as Enter or a shortcut such as Ctrl+L.");
        var modifierCodes = new List<ushort>();
        foreach (var part in parts[..^1])
        {
            var code = part switch { "ctrl" or "control" => (ushort)0x11, "alt" => (ushort)0x12, "shift" => (ushort)0x10, "win" => (ushort)0x5B, _ => (ushort)0 };
            if (code == 0) throw new InvalidOperationException($"Unknown shortcut modifier '{part}'.");
            modifierCodes.Add(code);
        }
        var key = parts[^1] switch
        {
            "enter" or "return" => (ushort)0x0D, "tab" => (ushort)0x09, "escape" or "esc" => (ushort)0x1B,
            "backspace" => (ushort)0x08, "delete" or "del" => (ushort)0x2E, "space" => (ushort)0x20,
            "up" => (ushort)0x26, "down" => (ushort)0x28, "left" => (ushort)0x25, "right" => (ushort)0x27,
            "home" => (ushort)0x24, "end" => (ushort)0x23, "pageup" => (ushort)0x21, "pagedown" => (ushort)0x22,
            "a" => (ushort)0x41, "c" => (ushort)0x43, "l" => (ushort)0x4C, "s" => (ushort)0x53,
            "b" => (ushort)0x42, "d" => (ushort)0x44, "e" => (ushort)0x45, "f" => (ushort)0x46, "g" => (ushort)0x47,
            "h" => (ushort)0x48, "i" => (ushort)0x49, "j" => (ushort)0x4A, "k" => (ushort)0x4B,
            "m" => (ushort)0x4D, "n" => (ushort)0x4E, "o" => (ushort)0x4F, "p" => (ushort)0x50,
            "q" => (ushort)0x51, "r" => (ushort)0x52, "t" => (ushort)0x54, "u" => (ushort)0x55,
            "v" => (ushort)0x56, "w" => (ushort)0x57, "x" => (ushort)0x58, "y" => (ushort)0x59, "z" => (ushort)0x5A,
            "0" => (ushort)0x30, "1" => (ushort)0x31, "2" => (ushort)0x32, "3" => (ushort)0x33,
            "4" => (ushort)0x34, "5" => (ushort)0x35, "6" => (ushort)0x36, "7" => (ushort)0x37,
            "8" => (ushort)0x38, "9" => (ushort)0x39,
            "f1" => (ushort)0x70, "f2" => (ushort)0x71, "f3" => (ushort)0x72, "f4" => (ushort)0x73,
            "f5" => (ushort)0x74, "f6" => (ushort)0x75, "f7" => (ushort)0x76, "f8" => (ushort)0x77,
            "f9" => (ushort)0x78, "f10" => (ushort)0x79, "f11" => (ushort)0x7A, "f12" => (ushort)0x7B,
            _ => throw new InvalidOperationException($"The key '{parts[^1]}' is not supported.")
        };
        ActivateTarget(windowHandle);
        try
        {
            foreach (var modifier in modifierCodes)
                if (!NativeMethods.SendVirtualKey(modifier, keyUp: false)) throw new InvalidOperationException("Windows rejected the shortcut modifier input.");
            if (!NativeMethods.SendVirtualKey(key, keyUp: false)) throw new InvalidOperationException("Windows rejected the key press; the WhatsApp action was not confirmed.");
            if (!NativeMethods.SendVirtualKey(key, keyUp: true)) throw new InvalidOperationException("Windows rejected the key release; check WhatsApp before retrying.");
        }
        finally
        {
            foreach (var modifier in modifierCodes.AsEnumerable().Reverse()) _ = NativeMethods.SendVirtualKey(modifier, keyUp: true);
        }
        return $"Pressed {keyName}.";
    }

    public string GetWindowTitleFor(IntPtr windowHandle) => NativeMethods.GetWindowTitle(windowHandle);
    public bool IsValidWindow(IntPtr windowHandle) => windowHandle != IntPtr.Zero && NativeMethods.IsWindow(windowHandle);

    private static AutomationElement FindControl(IntPtr windowHandle, string controlName)
    {
        EnsureTarget(windowHandle);
        var root = AutomationElement.FromHandle(windowHandle);
        var elements = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, controlName, PropertyConditionFlags.IgnoreCase));
        if (elements.Count == 0) throw new InvalidOperationException($"I couldn't find a visible control named '{controlName}'. Inspect the window and use its exact label.");
        if (elements.Count > 1) throw new InvalidOperationException($"More than one control is named '{controlName}'. Use a more specific label.");
        return elements[0];
    }

    private static bool IsWithinWindow(AutomationElement element, IntPtr windowHandle)
    {
        try { return element.Current.NativeWindowHandle == windowHandle.ToInt32(); }
        catch (ElementNotAvailableException) { return false; }
    }

    private static void EnsureTarget(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            throw new InvalidOperationException("No active external window is available. Switch to an app and try again.");
    }

    private static void ActivateTarget(IntPtr windowHandle)
    {
        EnsureTarget(windowHandle);
        _ = NativeMethods.ShowWindowAsync(windowHandle, 9); // SW_RESTORE
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == windowHandle) return;

            var currentThread = NativeMethods.GetCurrentThreadId();
            var targetThread = NativeMethods.GetWindowThreadProcessId(windowHandle, out _);
            var foregroundThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
            var attachedTarget = targetThread != 0 && targetThread != currentThread
                && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
            var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread
                && foregroundThread != targetThread
                && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                _ = NativeMethods.BringWindowToTop(windowHandle);
                _ = NativeMethods.SetForegroundWindow(windowHandle);
                _ = NativeMethods.SetActiveWindow(windowHandle);
                _ = NativeMethods.SetFocus(windowHandle);
            }
            finally
            {
                if (attachedForeground) _ = NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                if (attachedTarget) _ = NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }

            if (NativeMethods.GetForegroundWindow() == windowHandle) return;
            Thread.Sleep(60);
        }
        throw new InvalidOperationException("WhatsApp is open, but Windows did not bring it to the foreground. Bring WhatsApp forward and try again; no message was sent.");
    }

    private static ScreenInspection InspectWindow(IntPtr windowHandle, CancellationToken cancellationToken)
    {
        var title = NativeMethods.GetWindowTitle(windowHandle);
        var report = new StringBuilder();
        report.AppendLine(string.IsNullOrWhiteSpace(title) ? "Active window" : title);
        report.AppendLine("Visible controls:");

        var root = AutomationElement.FromHandle(windowHandle);
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        var firstChild = walker.GetFirstChild(root);
        if (firstChild is not null)
        {
            queue.Enqueue((firstChild, 1));
        }

        var count = 0;
        while (queue.Count > 0 && count < MaxControls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            try
            {
                var current = element.Current;
                if (!current.IsOffscreen)
                {
                    var name = string.IsNullOrWhiteSpace(current.Name) ? "(unlabeled)" : current.Name;
                    var controlType = current.ControlType?.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal)
                                      ?? "Control";
                    report.Append(' ', depth * 2)
                        .Append(controlType)
                        .Append(": ")
                        .AppendLine(name);
                    count++;
                }

                if (depth < MaxDepth)
                {
                    var child = walker.GetFirstChild(element);
                    while (child is not null)
                    {
                        queue.Enqueue((child, depth + 1));
                        child = walker.GetNextSibling(child);
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                // A control can disappear while the UI Automation tree is being read.
            }
        }

        if (count == 0)
        {
            report.AppendLine("No visible accessible controls were exposed by this window.");
        }
        else if (queue.Count > 0)
        {
            report.AppendLine("(Additional controls omitted.)");
        }

        return new ScreenInspection(title, report.ToString().TrimEnd());
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Input { internal uint Type; internal InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyboardInput Keyboard;
            [FieldOffset(0)] internal HardwareInput Hardware;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X;
            internal int Y;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal IntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput { internal ushort VirtualKey; internal ushort ScanCode; internal uint Flags; internal uint Time; internal IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct HardwareInput { internal uint Message; internal ushort ParamL; internal ushort ParamH; }
        private const uint InputKeyboard = 1;
        private const uint KeyUnicode = 0x0004;
        private const uint KeyUp = 0x0002;

        internal static Input CreateUnicodeInput(char character, bool keyUp) => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyUnicode | (keyUp ? KeyUp : 0) } }
        };

        internal static bool SendVirtualKey(ushort key, bool keyUp)
        {
            var input = new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = keyUp ? KeyUp : 0 } } };
            return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
        }

        internal static void ScrollMouseWheel(IntPtr window, int delta)
        {
            if (GetWindowRect(window, out var rect))
            {
                var x = rect.Left + (rect.Right - rect.Left) / 2;
                var y = rect.Top + (rect.Bottom - rect.Top) / 2;
                SetCursorPos(x, y);
                mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { internal int X, Y; }

        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr SetActiveWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachState);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

        private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        internal static IntPtr FindWindowTitleContaining(string text)
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && IsWindow(foreground)
                && GetWindowTitle(foreground).Contains(text, StringComparison.OrdinalIgnoreCase))
                return foreground;

            var match = IntPtr.Zero;
            _ = EnumWindows((windowHandle, _) =>
            {
                if (!IsWindow(windowHandle) || !IsWindowVisible(windowHandle)) return true;
                var title = GetWindowTitle(windowHandle);
                if (title.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    match = windowHandle;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return match;
        }

        internal static string GetWindowTitle(IntPtr windowHandle)
        {
            var buffer = new StringBuilder(512);
            _ = GetWindowText(windowHandle, buffer, buffer.Capacity);
            return buffer.ToString();
        }
    }
}
