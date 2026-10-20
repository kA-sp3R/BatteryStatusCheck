using System.ComponentModel;
using System.Runtime.InteropServices;
using DeviceBatteryStatus.Core.Hardware;
using Microsoft.Win32.SafeHandles;

namespace DeviceBatteryStatus.Windows.Hardware;

public sealed class WindowsHidTransportFactory : IHidTransportFactory
{
    public ValueTask<IHidTransport> OpenAsync(HidEndpoint endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows HID is required.");
        // Razer feature requests and the Alienware vendor collection use FILE_ANY_ACCESS control
        // IOCTLs. Reserved HID collections can deny generic read/write while allowing those requests.
        // HID++ requires interrupt read/write.
        var controlReportsOnly = (endpoint.VendorId == 0x1532 && endpoint.FeatureReportLength > 0)
            || endpoint is { VendorId: 0x413C, ProductId: 0xA529, UsagePage: 0xFF13 };
        var access = controlReportsOnly
            ? 0u : NativeMethods.GenericRead | NativeMethods.GenericWrite;
        var handle = NativeMethods.CreateFileW(endpoint.DevicePath,
            access, 3, IntPtr.Zero, 3,
            NativeMethods.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw WindowsHidTransport.CreateIoException(error,
                "Cannot open the receiver HID interface with the access required by its protocol.");
        }
        try { return ValueTask.FromResult<IHidTransport>(new WindowsHidTransport(endpoint, handle)); }
        catch { handle.Dispose(); throw; }
    }
}

/// <summary>
/// One serialized provider session. All native transfers are overlapped, cancellable, and time bounded.
/// A timeout closes the session: no subsequent request can consume a late reply from a cancelled transfer.
/// </summary>
internal sealed class WindowsHidTransport : IHidTransport
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private readonly SafeFileHandle _handle;
    private readonly ThreadPoolBoundHandle _boundHandle;
    private readonly object _lifetime = new();
    private readonly TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private int _pendingCount;
    public HidEndpoint Endpoint { get; }

    internal WindowsHidTransport(HidEndpoint endpoint, SafeFileHandle handle)
    {
        Endpoint = endpoint;
        _handle = handle;
        _boundHandle = ThreadPoolBoundHandle.BindHandle(handle);
    }

    public ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken) =>
        new(Start(Pad(request, Endpoint.FeatureReportLength), TransferKind.GetFeature, CommandTimeout, cancellationToken));

    public async ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken) =>
        _ = await Start(Pad(report, Endpoint.FeatureReportLength), TransferKind.SetFeature, CommandTimeout, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) =>
        _ = await Start(Pad(report, Endpoint.OutputReportLength), TransferKind.Write, CommandTimeout, cancellationToken)
            .ConfigureAwait(false);

    public ValueTask<byte[]> GetInputReportAsync(byte[] request, CancellationToken cancellationToken) =>
        new(Start(Pad(request, Endpoint.InputReportLength), TransferKind.GetInputReport, CommandTimeout, cancellationToken));

    public async ValueTask SetOutputReportAsync(byte[] report, CancellationToken cancellationToken) =>
        _ = await Start(Pad(report, Endpoint.OutputReportLength), TransferKind.SetOutputReport, CommandTimeout, cancellationToken)
            .ConfigureAwait(false);

    public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ValidateReportLength(Endpoint.InputReportLength);
        return new(Start(new byte[Endpoint.InputReportLength], TransferKind.Read, timeout, cancellationToken));
    }

    private static byte[] Pad(byte[] report, int reportLength)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidateReportLength(reportLength);
        if (report.Length == 0 || report.Length > reportLength)
            throw new ArgumentException("Report must include its report-ID byte and fit the HID descriptor.", nameof(report));
        var buffer = new byte[reportLength];
        report.CopyTo(buffer, 0);
        return buffer;
    }

    private static void ValidateReportLength(int reportLength)
    {
        if (reportLength is <= 0 or > 16384)
            throw new NotSupportedException("This HID collection does not expose a usable report of the requested type.");
    }

    internal static Exception CreateIoException(int error, string message) => error == 5
        ? new UnauthorizedAccessException(message, new Win32Exception(error))
        : new IOException(message, new Win32Exception(error));

    private unsafe Task<byte[]> Start(byte[] buffer, TransferKind kind, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var transfer = new PendingTransfer(this, buffer, kind, timeout, token);
            _pendingCount++;
            transfer.Submit();
            return transfer.Completion.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try { await _drained.Task.WaitAsync(CommandTimeout).ConfigureAwait(false); }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("The closed HID session did not drain native I/O within two seconds. " +
                "Its native buffers remain retained until completion.", exception);
        }
    }

    private void Close()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            // Closing cancels pending native I/O. Pinned memory is retained until the completion callback.
            _handle.Dispose();
            CompleteDrainIfReady();
        }
    }

    private void TransferCompleted()
    {
        lock (_lifetime)
        {
            _pendingCount--;
            CompleteDrainIfReady();
        }
    }

    private void CompleteDrainIfReady()
    {
        // Called under _lifetime, after all completed transfers have released their native state.
        if (!_disposed || _pendingCount != 0) return;
        _boundHandle.Dispose();
        _drained.TrySetResult(true);
    }

    private enum TransferKind { Read, Write, GetFeature, SetFeature, GetInputReport, SetOutputReport }

    private sealed unsafe class PendingTransfer
    {
        private readonly WindowsHidTransport _owner;
        private readonly byte[] _buffer;
        private readonly TransferKind _kind;
        private readonly CancellationToken _callerToken;
        private readonly CancellationTokenSource _deadline;
        private readonly object _gate = new();
        private GCHandle _pin;
        private NativeOverlapped* _overlapped;
        private CancellationTokenRegistration _registration;
        private bool _completed;
        internal TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PendingTransfer(WindowsHidTransport owner, byte[] buffer, TransferKind kind,
            TimeSpan timeout, CancellationToken callerToken)
        {
            _owner = owner; _buffer = buffer; _kind = kind; _callerToken = callerToken;
            _deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            _deadline.CancelAfter(timeout);
        }

        internal void Submit()
        {
            lock (_gate)
            {
                bool started;
                int error;
                try
                {
                    _pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
                    _overlapped = _owner._boundHandle.AllocateNativeOverlapped(OnNativeCompletion, this, null);
                    var pointer = _pin.AddrOfPinnedObject();
                    var length = checked((uint)_buffer.Length);
                    started = _kind switch
                    {
                        TransferKind.Read => NativeMethods.ReadFile(_owner._handle, pointer, length, IntPtr.Zero, _overlapped),
                        TransferKind.Write => NativeMethods.WriteFile(_owner._handle, pointer, length, IntPtr.Zero, _overlapped),
                        // Both buffers carry the report ID, matching the Windows HID class IOCTL contract.
                        TransferKind.GetFeature => NativeMethods.DeviceIoControl(_owner._handle,
                            NativeMethods.IoctlHidGetFeature, pointer, length, pointer, length, IntPtr.Zero, _overlapped),
                        TransferKind.SetFeature => NativeMethods.DeviceIoControl(_owner._handle,
                            NativeMethods.IoctlHidSetFeature, pointer, length, IntPtr.Zero, 0, IntPtr.Zero, _overlapped),
                        TransferKind.GetInputReport => NativeMethods.DeviceIoControl(_owner._handle,
                            NativeMethods.IoctlHidGetInputReport, pointer, length, pointer, length, IntPtr.Zero, _overlapped),
                        TransferKind.SetOutputReport => NativeMethods.DeviceIoControl(_owner._handle,
                            NativeMethods.IoctlHidSetOutputReport, pointer, length, IntPtr.Zero, 0, IntPtr.Zero, _overlapped),
                        _ => throw new InvalidOperationException()
                    };
                    error = started ? 0 : Marshal.GetLastWin32Error();
                }
                catch (Exception exception)
                {
                    // No native request was submitted, so no completion callback owns these allocations.
                    _completed = true;
                    _deadline.Dispose();
                    Completion.TrySetException(exception);
                    ReleaseNativeState();
                    return;
                }
                if (!started && error != NativeMethods.ErrorIoPending)
                {
                    Finish(unchecked((uint)error), 0);
                    return;
                }
                // Even synchronous success posts a completion packet: only the callback releases native state.
                try { _registration = _deadline.Token.Register(static state => ((PendingTransfer)state!).Cancel(), this); }
                catch (Exception exception)
                {
                    Completion.TrySetException(exception);
                    _owner.Close(); // The native completion callback retains ownership of the allocations.
                }
            }
        }

        private void Cancel()
        {
            lock (_gate)
            {
                if (_completed) return;
                lock (_owner._lifetime)
                {
                    // SafeHandle marshalling must not race DisposeAsync closing the handle.
                    if (!_owner._disposed) NativeMethods.CancelIoEx(_owner._handle, _overlapped);
                    if (_callerToken.IsCancellationRequested) Completion.TrySetCanceled(_callerToken);
                    else Completion.TrySetException(new TimeoutException($"HID {_kind} timed out; this session was closed."));
                    _owner.Close();
                }
            }
        }

        private static void OnNativeCompletion(uint error, uint transferred, NativeOverlapped* overlapped)
        {
            var transfer = (PendingTransfer)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
            lock (transfer._gate) transfer.Finish(error, transferred);
        }

        private void Finish(uint error, uint transferred)
        {
            if (_completed) return;
            _completed = true;
            _registration.Unregister();
            _deadline.Dispose();
            try
            {
                if (error == 0)
                {
                    if (_kind is TransferKind.Read or TransferKind.GetFeature or TransferKind.GetInputReport)
                    {
                        // Windows excludes the synthetic zero report ID from unnumbered control-report byte counts.
                        var count = (long)transferred;
                        if ((_kind is TransferKind.GetFeature or TransferKind.GetInputReport) && _buffer[0] == 0) count++;
                        if (count <= 0 || count > _buffer.Length)
                            Completion.TrySetException(new IOException("The HID driver returned an invalid report length."));
                        else Completion.TrySetResult(_buffer[..(int)count]);
                    }
                    else if (_kind == TransferKind.Write && transferred != _buffer.Length)
                        Completion.TrySetException(new IOException("The HID driver accepted only part of an output report."));
                    else Completion.TrySetResult([]);
                }
                else Completion.TrySetException(CreateIoException(unchecked((int)error), $"HID {_kind} failed."));
            }
            finally { ReleaseNativeState(); }
        }

        private void ReleaseNativeState()
        {
            if (_overlapped != null) _owner._boundHandle.FreeNativeOverlapped(_overlapped);
            if (_pin.IsAllocated) _pin.Free();
            _owner.TransferCompleted();
        }
    }
}
