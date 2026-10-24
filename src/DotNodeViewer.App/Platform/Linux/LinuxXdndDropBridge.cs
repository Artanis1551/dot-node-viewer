using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using DotNodeViewer.App.ViewModels;

namespace DotNodeViewer.App.Platform.Linux;

/// <summary>
/// Provides a native X11 XDND (X Drag-and-Drop) bridge for Linux desktop environments where upstream Avalonia 11
/// does not implement native X11 drop targets (<c>XdndAware</c> / XDND protocol version 5).
/// </summary>
public sealed class LinuxXdndDropBridge : IDisposable
{
    private const string LibX11 = "libX11.so.6";
    private const int ClientMessageEventType = 33;
    private const int SelectionNotifyEventType = 31;
    private const int PropModeReplace = 0;
    private const nint XaAtom = 4;
    private const nint XaWindow = 33;

    private readonly MainWindowViewModel _viewModel;
    private readonly IntPtr _targetWindow;
    private IntPtr _display;
    private IntPtr _proxyWindow;
    private Thread? _eventThread;
    private volatile bool _disposed;

    // Interned X11 Atoms
    private IntPtr _xdndAwareAtom;
    private IntPtr _xdndProxyAtom;
    private IntPtr _xdndEnterAtom;
    private IntPtr _xdndPositionAtom;
    private IntPtr _xdndStatusAtom;
    private IntPtr _xdndLeaveAtom;
    private IntPtr _xdndDropAtom;
    private IntPtr _xdndFinishedAtom;
    private IntPtr _xdndActionCopyAtom;
    private IntPtr _xdndSelectionAtom;
    private IntPtr _textUriListAtom;
    private IntPtr _stopBridgeAtom;

    private IntPtr _currentSource;
    private IntPtr _dropSource;
    private string[]? _cachedFiles;
    private bool _hasRequestedHoverFiles;
    private bool _isDropPending;
    private IntPtr _pendingDropSource;
    private IntPtr _lastPositionSource;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(IntPtr display, ref XErrorEvent errorEvent);

    private static readonly XErrorHandler ErrorHandler = static (IntPtr _, ref XErrorEvent _) => 0;

    private LinuxXdndDropBridge(IntPtr targetWindow, MainWindowViewModel viewModel, IntPtr display, IntPtr proxyWindow)
    {
        _targetWindow = targetWindow;
        _viewModel = viewModel;
        _display = display;
        _proxyWindow = proxyWindow;
    }

    /// <summary>
    /// Attempts to attach an X11 XDND drop bridge to the specified Avalonia window on Linux systems.
    /// </summary>
    /// <param name="window">The top-level window to attach to.</param>
    /// <param name="viewModel">The view model to notify when drag hover and drop operations occur.</param>
    /// <returns>A disposable bridge instance if successfully attached; otherwise, <see langword="null"/>.</returns>
    public static IDisposable? TryAttach(Window window, MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        try
        {
            IPlatformHandle? platformHandle = window.TryGetPlatformHandle();
            if (platformHandle is null || platformHandle.HandleDescriptor != "XID" || platformHandle.Handle == IntPtr.Zero)
            {
                return null;
            }

            return InitializeBridge(platformHandle.Handle, viewModel);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LinuxXdndDropBridge] Failed to initialize native X11 drop bridge: {ex.Message}");
            return null;
        }
    }

    private static LinuxXdndDropBridge? InitializeBridge(IntPtr targetWindow, MainWindowViewModel viewModel)
    {
        _ = XSetErrorHandler(ErrorHandler);

        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            IntPtr root = XDefaultRootWindow(display);
            IntPtr proxyWindow = XCreateSimpleWindow(display, root, -100, -100, 1, 1, 0, 0, 0);
            if (proxyWindow == IntPtr.Zero)
            {
                XCloseDisplay(display);
                return null;
            }

            LinuxXdndDropBridge bridge = new(targetWindow, viewModel, display, proxyWindow);
            bridge.SetupAtomsAndProperties();
            bridge.StartEventLoop();
            return bridge;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LinuxXdndDropBridge] Initialization failed: {ex.Message}");
            XCloseDisplay(display);
            return null;
        }
    }

    private void SetupAtomsAndProperties()
    {
        _xdndAwareAtom = InternAtom(_display, "XdndAware");
        _xdndProxyAtom = InternAtom(_display, "XdndProxy");
        _xdndEnterAtom = InternAtom(_display, "XdndEnter");
        _xdndPositionAtom = InternAtom(_display, "XdndPosition");
        _xdndStatusAtom = InternAtom(_display, "XdndStatus");
        _xdndLeaveAtom = InternAtom(_display, "XdndLeave");
        _xdndDropAtom = InternAtom(_display, "XdndDrop");
        _xdndFinishedAtom = InternAtom(_display, "XdndFinished");
        _xdndActionCopyAtom = InternAtom(_display, "XdndActionCopy");
        _xdndSelectionAtom = InternAtom(_display, "XdndSelection");
        _textUriListAtom = InternAtom(_display, "text/uri-list");
        _stopBridgeAtom = InternAtom(_display, "_DOT_NODE_VIEWER_STOP_XDND");

        nint version = 5;
        nint proxy = (nint)_proxyWindow;

        // Set XdndAware and XdndProxy on proxy window
        XChangeProperty(_display, _proxyWindow, _xdndAwareAtom, (IntPtr)XaAtom, 32, PropModeReplace, ref version, 1);
        XChangeProperty(_display, _proxyWindow, _xdndProxyAtom, (IntPtr)XaWindow, 32, PropModeReplace, ref proxy, 1);

        // Set XdndProxy and XdndAware on the target Avalonia window
        XChangeProperty(_display, _targetWindow, _xdndProxyAtom, (IntPtr)XaWindow, 32, PropModeReplace, ref proxy, 1);
        XChangeProperty(_display, _targetWindow, _xdndAwareAtom, (IntPtr)XaAtom, 32, PropModeReplace, ref version, 1);

        XFlush(_display);
    }

    private void StartEventLoop()
    {
        _eventThread = new Thread(EventLoop)
        {
            Name = "DotNodeViewer-X11XdndBridge",
            IsBackground = true
        };
        _eventThread.Start();
    }

    private void EventLoop()
    {
        while (!_disposed)
        {
            try
            {
                if (XNextEvent(_display, out XEvent ev) != 0)
                {
                    break;
                }

                if (ev.type == ClientMessageEventType)
                {
                    HandleClientMessage(ref ev.client);
                }
                else if (ev.type == SelectionNotifyEventType)
                {
                    HandleSelectionNotify(ref ev.selection);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LinuxXdndDropBridge] Exception in event loop: {ex}");
            }
        }
    }

    private void HandleClientMessage(ref XClientMessageEvent client)
    {
        IntPtr messageType = client.message_type;

        if (messageType == _stopBridgeAtom)
        {
            return;
        }

        if (messageType == _xdndEnterAtom)
        {
            _currentSource = client.data0;
            _cachedFiles = null;
            _hasRequestedHoverFiles = false;
            _isDropPending = false;
            _pendingDropSource = IntPtr.Zero;
            _lastPositionSource = IntPtr.Zero;

            Dispatcher.UIThread.Post(() => _viewModel.SetDropTargetState(isActive: true, isValid: true));
        }
        else if (messageType == _xdndPositionAtom)
        {
            IntPtr sourceWindow = client.data0;
            _currentSource = sourceWindow;
            _lastPositionSource = sourceWindow;
            IntPtr timestamp = client.data3; // Timestamp is at data.l[3] for XdndPosition

            if (_cachedFiles is not null)
            {
                bool isValid = _cachedFiles.Length > 0 && MainWindowViewModel.CanAcceptFiles(_cachedFiles);

                SendXdndStatus(sourceWindow, isValid);

                Dispatcher.UIThread.Post(() => _viewModel.SetDropTargetState(
                    isActive: true,
                    isValid: isValid,
                    feedbackMessage: isValid ? null : "Unsupported format. Supported: .onnx, .tflite, .pte, .pt, .pt2, .keras, .h5"));
            }
            else
            {
                if (!_hasRequestedHoverFiles)
                {
                    _hasRequestedHoverFiles = true;
                    // Request files for hover inspection so we can validate before drop
                    XConvertSelection(
                        _display,
                        _xdndSelectionAtom,
                        _textUriListAtom,
                        _xdndSelectionAtom,
                        _proxyWindow,
                        timestamp);
                    XFlush(_display);
                }

                SendXdndStatus(sourceWindow, willAccept: true);
                Dispatcher.UIThread.Post(() => _viewModel.SetDropTargetState(isActive: true, isValid: true));
            }
        }
        else if (messageType == _xdndLeaveAtom)
        {
            _currentSource = IntPtr.Zero;
            _cachedFiles = null;
            _hasRequestedHoverFiles = false;
            _isDropPending = false;
            _pendingDropSource = IntPtr.Zero;
            _lastPositionSource = IntPtr.Zero;

            Dispatcher.UIThread.Post(() => _viewModel.SetDropTargetState(isActive: false, isValid: true));
        }
        else if (messageType == _xdndDropAtom)
        {
            IntPtr sourceWindow = client.data0;
            IntPtr timestamp = client.data2; // Timestamp is at data.l[2] for XdndDrop
            _dropSource = sourceWindow;

            if (_cachedFiles is not null)
            {
                bool isValid = _cachedFiles.Length > 0 && MainWindowViewModel.CanAcceptFiles(_cachedFiles);

                SendXdndFinished(sourceWindow, isSuccess: isValid);

                if (isValid)
                {
                    // Immediately dismiss overlay on valid drop!
                    Dispatcher.UIThread.Post(() => _viewModel.SetDropTargetState(isActive: false, isValid: true));
                }

                string[] dropped = _cachedFiles;
                _cachedFiles = null;

                Dispatcher.UIThread.Post(async () =>
                {
                    await _viewModel.HandleDroppedFilesAsync(dropped).ConfigureAwait(true);
                });
            }
            else
            {
                // Files not yet cached, convert on drop
                _isDropPending = true;
                _pendingDropSource = sourceWindow;

                XConvertSelection(
                    _display,
                    _xdndSelectionAtom,
                    _textUriListAtom,
                    _xdndSelectionAtom,
                    _proxyWindow,
                    timestamp);
                XFlush(_display);
            }
        }
    }

    private void HandleSelectionNotify(ref XSelectionEvent selection)
    {
        IntPtr property = selection.property;
        string[] files = property != IntPtr.Zero ? ReadUriListProperty(_display, _proxyWindow, property) : [];
        _cachedFiles = files;
        bool isValid = files.Length > 0 && MainWindowViewModel.CanAcceptFiles(files);

        if (_isDropPending)
        {
            _isDropPending = false;
            IntPtr dropSource = _pendingDropSource != IntPtr.Zero ? _pendingDropSource : _dropSource;
            _pendingDropSource = IntPtr.Zero;
            _dropSource = IntPtr.Zero;

            if (dropSource != IntPtr.Zero)
            {
                SendXdndFinished(dropSource, isSuccess: isValid);
            }

            if (isValid)
            {
                // Immediately dismiss overlay on valid drop!
                Dispatcher.UIThread.Post(() => _viewModel.SetDropTargetState(isActive: false, isValid: true));
            }

            if (files.Length > 0)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await _viewModel.HandleDroppedFilesAsync(files).ConfigureAwait(true);
                });
            }
            else
            {
                Dispatcher.UIThread.Post(() => _viewModel.SetDropTargetState(
                    isActive: true,
                    isValid: false,
                    feedbackMessage: "Unsupported format. Supported: .onnx, .tflite, .pte, .pt, .pt2, .keras, .h5"));
            }
        }
        else
        {
            // Hover inspection completed: update overlay to show whether format is accepted
            Dispatcher.UIThread.Post(() => _viewModel.SetDropTargetState(
                isActive: true,
                isValid: isValid,
                feedbackMessage: isValid ? null : "Unsupported format. Supported: .onnx, .tflite, .pte, .pt, .pt2, .keras, .h5"));

            if (_lastPositionSource != IntPtr.Zero)
            {
                SendXdndStatus(_lastPositionSource, willAccept: isValid);
            }
        }
    }

    private void SendXdndStatus(IntPtr sourceWindow, bool willAccept)
    {
        XEvent reply = default;
        reply.type = ClientMessageEventType;
        reply.client.type = ClientMessageEventType;
        reply.client.window = sourceWindow;
        reply.client.message_type = _xdndStatusAtom;
        reply.client.format = 32;
        reply.client.data0 = _targetWindow; // Window where pointer is located
        reply.client.data1 = willAccept ? (IntPtr)1 : IntPtr.Zero;
        reply.client.data2 = IntPtr.Zero;   // Silent rectangle
        reply.client.data3 = IntPtr.Zero;
        reply.client.data4 = willAccept ? _xdndActionCopyAtom : IntPtr.Zero;

        XSendEvent(_display, sourceWindow, false, 0, ref reply);
        XFlush(_display);
    }

    private void SendXdndFinished(IntPtr sourceWindow, bool isSuccess)
    {
        XEvent finished = default;
        finished.type = ClientMessageEventType;
        finished.client.type = ClientMessageEventType;
        finished.client.window = sourceWindow;
        finished.client.message_type = _xdndFinishedAtom;
        finished.client.format = 32;
        finished.client.data0 = _targetWindow;
        finished.client.data1 = isSuccess ? (IntPtr)1 : IntPtr.Zero;
        finished.client.data2 = isSuccess ? _xdndActionCopyAtom : IntPtr.Zero;

        XSendEvent(_display, sourceWindow, false, 0, ref finished);
        XFlush(_display);
    }

    private static string[] ReadUriListProperty(IntPtr display, IntPtr window, IntPtr property)
    {
        nint offset = 0;
        nint length = 65536; // Read up to 256 KB of URI data
        int result = XGetWindowProperty(
            display,
            window,
            property,
            offset,
            length,
            delete: true,
            req_type: IntPtr.Zero,
            out _,
            out int actualFormat,
            out nint nitems,
            out _,
            out IntPtr prop);

        if (result != 0 || prop == IntPtr.Zero || nitems <= 0)
        {
            return [];
        }

        try
        {
            if (actualFormat != 8)
            {
                return [];
            }

            byte[] bytes = new byte[(int)nitems];
            Marshal.Copy(prop, bytes, 0, (int)nitems);
            string text = Encoding.UTF8.GetString(bytes);
            return ParseUriList(text);
        }
        finally
        {
            XFree(prop);
        }
    }

    /// <summary>
    /// Parses an RFC 2483 compliant <c>text/uri-list</c> payload into local file system paths.
    /// </summary>
    /// <param name="text">The raw string from the X11 selection property.</param>
    /// <returns>An array of extracted local file paths.</returns>
    internal static string[] ParseUriList(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        List<string> result = new(lines.Length);

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            {
                result.Add(uri.LocalPath);
            }
            else if (Path.IsPathRooted(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result.ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_display != IntPtr.Zero && _targetWindow != IntPtr.Zero)
            {
                XDeleteProperty(_display, _targetWindow, _xdndProxyAtom);
                XDeleteProperty(_display, _targetWindow, _xdndAwareAtom);
                XFlush(_display);
            }

            if (_display != IntPtr.Zero && _proxyWindow != IntPtr.Zero)
            {
                XEvent stop = default;
                stop.type = ClientMessageEventType;
                stop.client.type = ClientMessageEventType;
                stop.client.window = _proxyWindow;
                stop.client.message_type = _stopBridgeAtom;
                stop.client.format = 32;

                XSendEvent(_display, _proxyWindow, false, 0, ref stop);
                XFlush(_display);
            }

            _eventThread?.Join(1000);

            if (_display != IntPtr.Zero)
            {
                if (_proxyWindow != IntPtr.Zero)
                {
                    XDestroyWindow(_display, _proxyWindow);
                    _proxyWindow = IntPtr.Zero;
                }

                XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LinuxXdndDropBridge] Exception during disposal: {ex.Message}");
        }
    }

    #region X11 P/Invoke & Structures

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    internal struct XEvent
    {
        [FieldOffset(0)]
        public int type;

        [FieldOffset(0)]
        public XClientMessageEvent client;

        [FieldOffset(0)]
        public XSelectionEvent selection;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XClientMessageEvent
    {
        public int type;
        public nint serial;
        public int send_event;
        public IntPtr display;
        public IntPtr window;
        public IntPtr message_type;
        public int format;
        public IntPtr data0;
        public IntPtr data1;
        public IntPtr data2;
        public IntPtr data3;
        public IntPtr data4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XSelectionEvent
    {
        public int type;
        public nint serial;
        public int send_event;
        public IntPtr display;
        public IntPtr requestor;
        public IntPtr selection;
        public IntPtr target;
        public IntPtr property;
        public IntPtr time;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XErrorEvent
    {
        public int type;
        public IntPtr display;
        public IntPtr resourceid;
        public nint serial;
        public byte error_code;
        public byte request_code;
        public byte minor_code;
    }

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern IntPtr XOpenDisplay(IntPtr display_name);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern void XCloseDisplay(IntPtr display);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern IntPtr XCreateSimpleWindow(
        IntPtr display,
        IntPtr parent,
        int x,
        int y,
        uint width,
        uint height,
        uint border_width,
        nuint border,
        nuint background);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern void XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern IntPtr XInternAtom(IntPtr display, byte[] atom_name, bool only_if_exists);

    private static IntPtr InternAtom(IntPtr display, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name + '\0');
        return XInternAtom(display, bytes, false);
    }

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern void XChangeProperty(
        IntPtr display,
        IntPtr window,
        IntPtr property,
        IntPtr type,
        int format,
        int mode,
        ref nint data,
        int nelements);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern void XDeleteProperty(IntPtr display, IntPtr window, IntPtr property);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern void XSendEvent(
        IntPtr display,
        IntPtr window,
        bool propagate,
        nint event_mask,
        ref XEvent send_event);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern void XConvertSelection(
        IntPtr display,
        IntPtr selection,
        IntPtr target,
        IntPtr property,
        IntPtr requestor,
        IntPtr time);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern int XGetWindowProperty(
        IntPtr display,
        IntPtr window,
        IntPtr property,
        nint long_offset,
        nint long_length,
        bool delete,
        IntPtr req_type,
        out IntPtr actual_type_return,
        out int actual_format_return,
        out nint nitems_return,
        out nint bytes_after_return,
        out IntPtr prop_return);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern void XFree(IntPtr data);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern void XFlush(IntPtr display);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern int XNextEvent(IntPtr display, out XEvent ev);

    [DllImport(LibX11, ExactSpelling = true)]
    private static extern IntPtr XSetErrorHandler(XErrorHandler handler);

    #endregion
}
