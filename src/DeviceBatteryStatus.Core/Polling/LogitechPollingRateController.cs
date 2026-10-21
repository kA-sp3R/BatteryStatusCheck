using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;

namespace DeviceBatteryStatus.Core.Polling;

/// <summary>Manual HID++ mouse rate changes; battery providers never call this controller.</summary>
public sealed class LogitechPollingRateController : IHostModePollingController
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(700);
    private int softwareId = RandomNumberGenerator.GetInt32(0, 8);

    public bool CanHandle(HidEndpoint endpoint, PeripheralIdentity device) =>
        endpoint.VendorId == 0x046D && LogitechReceiverCatalog.TryGet(endpoint.ProductId, out _) &&
        endpoint.UsagePage == 0xFF00 && endpoint.Usage is 1 or 2 &&
        endpoint.InputReportLength >= 20 && endpoint.OutputReportLength >= 7 &&
        TryIdentity(endpoint, device, out _);

    public async Task<PollingRateInfo> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
        PeripheralIdentity device, CancellationToken cancellationToken)
    {
        try { return (await ProbeAsync(endpoint, transport, device, cancellationToken)).Info; }
        catch (PollingException exception) { return Unavailable(device, exception.State, exception.Message); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
        { return Unavailable(device, PollingRateState.Failed, exception.Message); }
    }

    public Task<PollingApplyResult> ApplyAsync(HidEndpoint endpoint, IHidTransport transport,
        PeripheralIdentity device, int rateHz, Func<bool> canApply, CancellationToken cancellationToken) =>
        ApplyCoreAsync(endpoint, transport, device, rateHz, canApply, null, cancellationToken);

    public Task<PollingApplyResult> ApplyWithHostModeSwitchAsync(HidEndpoint endpoint, IHidTransport transport,
        PeripheralIdentity device, int rateHz, Func<bool> canApply, Func<bool> canSwitchToHostMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canSwitchToHostMode);
        return ApplyCoreAsync(endpoint, transport, device, rateHz, canApply, canSwitchToHostMode, cancellationToken);
    }

    private async Task<PollingApplyResult> ApplyCoreAsync(HidEndpoint endpoint, IHidTransport transport,
        PeripheralIdentity device, int rateHz, Func<bool> canApply, Func<bool>? canSwitchToHostMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canApply);
        cancellationToken.ThrowIfCancellationRequested();
        bool writeAttempted = false;
        bool modeWriteAttempted = false;
        try
        {
            RequireAuthorization(canApply, cancellationToken);
            if (canSwitchToHostMode is not null) RequireAuthorization(canSwitchToHostMode, cancellationToken);
            var probe = await ProbeAsync(endpoint, transport, device, cancellationToken);
            if (!probe.Info.SupportedRatesHz.Contains(rateHz) ||
                !LogitechPollingProtocol.TryEncodeRate(probe.FeatureId, rateHz, out byte value))
                throw new PollingException(PollingRateState.Unsupported, "The selected mouse did not advertise that report rate.");
            if (probe.Info.State == PollingRateState.RequiresHostMode)
            {
                if (canSwitchToHostMode is null)
                    throw new PollingException(PollingRateState.Unsupported,
                        "The mouse uses onboard profiles. Changing to software mode requires the separate explicit opt-in.");
                await ValidateMouseAsync(transport, probe.Identity, cancellationToken);
                var profile = await ReadProfileModeAsync(transport, probe.Identity.Slot, cancellationToken);
                var modeRates = await ReadCapabilitiesAsync(transport, probe.Identity.Slot,
                    probe.FeatureId, probe.FeatureIndex, cancellationToken);
                if (!modeRates.Contains(rateHz))
                    throw new PollingException(PollingRateState.Unsupported,
                        "The advertised rate options changed before the software-mode change; no setting was written.");
                if (profile.Mode == 1)
                {
                    if (profile.FeatureIndex is null) throw new InvalidDataException("The onboard profile feature is unavailable.");
                    byte function = NextFunction(0x10);
                    byte[] modeRequest = LogitechHidppProtocol.CreateRequest(probe.Identity.Slot,
                        profile.FeatureIndex.Value, function, [0x02]);
                    RequireAuthorization(canApply, cancellationToken);
                    RequireAuthorization(canSwitchToHostMode, cancellationToken);
                    modeWriteAttempted = writeAttempted = true;
                    await transport.WriteAsync(modeRequest, cancellationToken);
                    await ReceiveAsync(transport, probe.Identity.Slot, profile.FeatureIndex.Value, function, cancellationToken);
                    await RequireHostModeAsync(transport, probe.Identity.Slot, cancellationToken);
                }
                // Re-probe the same mouse after changing modes: software-mode
                // capabilities and configuration may differ from the onboard view.
                probe = await ProbeAsync(endpoint, transport, device, cancellationToken);
                if (probe.Info.State != PollingRateState.Supported)
                    throw new PollingException(PollingRateState.Failed, "Software mode was not confirmed after the requested change.");
                if (!probe.Info.SupportedRatesHz.Contains(rateHz) ||
                    !LogitechPollingProtocol.TryEncodeRate(probe.FeatureId, rateHz, out value))
                    throw new PollingException(PollingRateState.Unsupported,
                        "The mouse does not advertise the requested rate after changing to software mode.");
            }
            RequireAuthorization(canApply, cancellationToken);
            if (canSwitchToHostMode is not null) RequireAuthorization(canSwitchToHostMode, cancellationToken);
            if (probe.Info.CurrentRateHz == rateHz)
                return new(true, probe.Info, modeWriteAttempted
                    ? "Software mode was confirmed and the mouse reports the requested rate; no report-rate setting was needed."
                    : "The device already reports the requested rate; no setting was written.");

            // Repeat the identity, profile and capability checks after the user's selection.
            await ValidateMouseAsync(transport, probe.Identity, cancellationToken);
            await RequireHostModeAsync(transport, probe.Identity.Slot, cancellationToken);
            var rates = await ReadCapabilitiesAsync(transport, probe.Identity.Slot,
                probe.FeatureId, probe.FeatureIndex, cancellationToken);
            if (!rates.Contains(rateHz))
                throw new PollingException(PollingRateState.Unsupported, "The receiver's advertised rate options changed; no setting was written.");

            byte setFunction = NextFunction(probe.FeatureId == LogitechPollingProtocol.ExtendedReportRate ? (byte)0x30 : (byte)0x20);
            byte[] request = LogitechHidppProtocol.CreateRequest(probe.Identity.Slot, probe.FeatureIndex, setFunction, [value]);
            RequireAuthorization(canApply, cancellationToken);
            if (canSwitchToHostMode is not null) RequireAuthorization(canSwitchToHostMode, cancellationToken);
            writeAttempted = true;
            await transport.WriteAsync(request, cancellationToken); // Exactly one setting command; never retried.
            await ReceiveAsync(transport, probe.Identity.Slot, probe.FeatureIndex, setFunction, cancellationToken);
            await ValidateMouseAsync(transport, probe.Identity, cancellationToken);
            await RequireHostModeAsync(transport, probe.Identity.Slot, cancellationToken);
            int current = await ReadRateAsync(transport, probe.Identity.Slot, probe.FeatureId,
                probe.FeatureIndex, rates, cancellationToken);
            var info = new PollingRateInfo(device.Id, PollingRateState.Supported, current, rates, RateDetail);
            return new(current == rateHz, info, current == rateHz
                ? "The same mouse reported the requested rate after the write."
                : "The read-back rate differs from the request; no retry or rollback was sent.");
        }
        catch (PollingException exception)
        { return new(false, Unavailable(device, exception.State, exception.Message), ModeFailureDetail(modeWriteAttempted, writeAttempted, exception.Message)); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException ||
            (exception is OperationCanceledException && cancellationToken.IsCancellationRequested && writeAttempted))
        { return new(false, Unavailable(device, PollingRateState.Failed, exception.Message), ModeFailureDetail(modeWriteAttempted, writeAttempted, exception.Message)); }
    }

    private async Task<Probe> ProbeAsync(HidEndpoint endpoint, IHidTransport transport,
        PeripheralIdentity device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanHandle(endpoint, device) || transport.Endpoint != endpoint || !TryIdentity(endpoint, device, out var identity))
            throw new PollingException(PollingRateState.Unsupported, "A supported receiver and an exact peripheral identity are required.");
        await ValidateMouseAsync(transport, identity!, cancellationToken);
        var profile = await ReadProfileModeAsync(transport, identity!.Slot, cancellationToken);
        byte? featureIndex = await FeatureIndexAsync(transport, identity.Slot, LogitechPollingProtocol.ExtendedReportRate, cancellationToken);
        ushort featureId = LogitechPollingProtocol.ExtendedReportRate;
        if (featureIndex is null)
        {
            featureId = LogitechPollingProtocol.AdjustableReportRate;
            featureIndex = await FeatureIndexAsync(transport, identity.Slot, featureId, cancellationToken);
        }
        if (featureIndex is null)
            throw new PollingException(PollingRateState.Unsupported, "This mouse does not advertise HID++ 0x8060 or 0x8061 report-rate control.");
        var rates = await ReadCapabilitiesAsync(transport, identity.Slot, featureId, featureIndex.Value, cancellationToken);
        if (rates.Count == 0) throw new PollingException(PollingRateState.Unsupported, "No supported whole-Hz rate was advertised.");
        if (profile.Mode == 1)
            return new(identity, featureId, featureIndex.Value, new(device.Id,
                PollingRateState.RequiresHostMode, null, rates,
                "The mouse uses onboard profiles. A separate opt-in can switch to software mode on Apply; this may change DPI and button behavior. The software-mode rate is not presented as the active onboard rate."));
        int current = await ReadRateAsync(transport, identity.Slot, featureId, featureIndex.Value, rates, cancellationToken);
        return new(identity, featureId, featureIndex.Value, new(device.Id,
            PollingRateState.Supported, current, rates, RateDetail));
    }

    private async Task ValidateMouseAsync(IHidTransport transport, Identity identity, CancellationToken cancellationToken)
    {
        byte marker = (byte)RandomNumberGenerator.GetInt32(1, 256);
        var ping = await ExchangeAsync(transport, identity.Slot, 0, NextFunction(0x10),
            [0, 0, marker], cancellationToken, pingMarker: marker);
        if (ping.Parameters.Length < 3 || ping.Parameters[0] < 2)
            throw new PollingException(PollingRateState.Unsupported, "HID++ 2 or later is required for report-rate control.");

        if (identity.UnitId is not null)
        {
            byte? ids = await FeatureIndexAsync(transport, identity.Slot, 0x0003, cancellationToken);
            if (ids is null) throw new PollingException(PollingRateState.Unsupported, "The selected mouse's unit identity cannot be verified.");
            var info = await FeatureAsync(transport, identity.Slot, ids.Value, 0, [], cancellationToken);
            if (info.Parameters.Length < 5) throw new InvalidDataException("Truncated HID++ unit identity.");
            if (!string.Equals(Convert.ToHexString(info.Parameters.AsSpan(1, 4)), identity.UnitId, StringComparison.Ordinal))
                throw new PollingException(PollingRateState.Disconnected, "The receiver slot now addresses a different peripheral.");
        }
        else
        {
            LogitechReceiverCatalog.TryGet(transport.Endpoint.ProductId, out var receiver);
            bool bolt = receiver!.Kind == LogitechReceiverKind.Bolt;
            byte pairOffset = bolt ? (byte)(0x50 + identity.Slot) : (byte)(0x20 + identity.Slot - 1);
            var pairing = await ExchangeAsync(transport, 0xFF, 0x83, 0xB5, [pairOffset], cancellationToken, expectedEcho: pairOffset);
            if (pairing.Parameters.Length < 8) throw new InvalidDataException("Truncated HID++ pairing identity.");
            string wirelessId = bolt ? $"{pairing.Parameters[3]:X2}{pairing.Parameters[2]:X2}"
                : Convert.ToHexString(pairing.Parameters.AsSpan(3, 2));
            string reportedSerial;
            if (bolt) reportedSerial = Convert.ToHexString(pairing.Parameters.AsSpan(4, 4));
            else
            {
                byte serialOffset = (byte)(0x30 + identity.Slot - 1);
                var serial = await ExchangeAsync(transport, 0xFF, 0x83, 0xB5, [serialOffset], cancellationToken, expectedEcho: serialOffset);
                if (serial.Parameters.Length < 5) throw new InvalidDataException("Truncated HID++ pairing serial.");
                reportedSerial = Convert.ToHexString(serial.Parameters.AsSpan(1, 4));
            }
            if (!string.Equals(wirelessId, identity.WirelessId, StringComparison.Ordinal) ||
                !string.Equals(reportedSerial, identity.Serial, StringComparison.Ordinal))
                throw new PollingException(PollingRateState.Disconnected, "The mouse paired to this receiver slot changed.");
            if ((pairing.Parameters[bolt ? 1 : 7] & 0x0F) != 0x02)
                throw new PollingException(PollingRateState.Unsupported, "The paired peripheral is not identified as a mouse.");
        }
        byte? nameFeature = await FeatureIndexAsync(transport, identity.Slot, 0x0005, cancellationToken);
        if (nameFeature is null) throw new PollingException(PollingRateState.Unsupported, "The peripheral's device type cannot be verified.");
        var kind = await FeatureAsync(transport, identity.Slot, nameFeature.Value, 0x20, [], cancellationToken);
        if (kind.Parameters.Length == 0 || kind.Parameters[0] != 0x03)
            throw new PollingException(PollingRateState.Unsupported, "Polling-rate changes are limited to explicitly identified mice.");
    }

    private async Task RequireHostModeAsync(IHidTransport transport, byte slot, CancellationToken cancellationToken)
    {
        var profile = await ReadProfileModeAsync(transport, slot, cancellationToken);
        if (profile.Mode != 0x02)
            throw new PollingException(PollingRateState.Unsupported,
                "Software mode is required; no automatic profile-mode or profile-memory change is sent.");
    }

    private async Task<(byte? FeatureIndex, byte Mode)> ReadProfileModeAsync(IHidTransport transport, byte slot,
        CancellationToken cancellationToken)
    {
        byte? profiles = await FeatureIndexAsync(transport, slot, 0x8100, cancellationToken);
        if (profiles is null) return (null, 0x02);
        var mode = await FeatureAsync(transport, slot, profiles.Value, 0x20, [], cancellationToken);
        if (mode.Parameters.Length == 0 || mode.Parameters[0] is not (0x01 or 0x02))
            throw new PollingException(PollingRateState.Unsupported, "The mouse reports an unknown profile mode; no setting is changed.");
        return (profiles, mode.Parameters[0]);
    }

    private async Task<IReadOnlyList<int>> ReadCapabilitiesAsync(IHidTransport transport, byte slot,
        ushort featureId, byte featureIndex, CancellationToken cancellationToken)
    {
        var reply = await FeatureAsync(transport, slot, featureIndex,
            featureId == LogitechPollingProtocol.ExtendedReportRate ? (byte)0x10 : (byte)0, [], cancellationToken);
        return LogitechPollingProtocol.DecodeSupportedRates(featureId, reply.Parameters);
    }

    private async Task<int> ReadRateAsync(IHidTransport transport, byte slot, ushort featureId,
        byte featureIndex, IReadOnlyList<int> supported, CancellationToken cancellationToken)
    {
        var reply = await FeatureAsync(transport, slot, featureIndex,
            featureId == LogitechPollingProtocol.ExtendedReportRate ? (byte)0x20 : (byte)0x10, [], cancellationToken);
        if (reply.Parameters.Length == 0 || !LogitechPollingProtocol.TryDecodeRate(featureId, reply.Parameters[0], out int rate) ||
            !supported.Contains(rate)) throw new InvalidDataException("The mouse reported a rate outside its advertised supported options.");
        return rate;
    }

    private async Task<byte?> FeatureIndexAsync(IHidTransport transport, byte slot, ushort feature,
        CancellationToken cancellationToken)
    {
        var reply = await FeatureAsync(transport, slot, 0, 0, [(byte)(feature >> 8), (byte)feature], cancellationToken);
        if (reply.Parameters.Length < 3) throw new InvalidDataException("Truncated HID++ feature discovery.");
        // IRoot specifies that later versions of the same feature preserve its
        // original functions. Use only those baseline commands and validate all
        // advertised capabilities/replies; hidden and obsolete features remain excluded.
        if (feature == 0x8100 && reply.Parameters[0] != 0 && (reply.Parameters[1] & 0xC0) != 0)
            throw new PollingException(PollingRateState.Unsupported,
                "The profile-mode feature is hidden or obsolete; software mode cannot be verified safely.");
        if (reply.Parameters[0] == 0 || (reply.Parameters[1] & 0xC0) != 0) return null;
        return reply.Parameters[0];
    }

    private Task<LogitechReply> FeatureAsync(IHidTransport transport, byte slot, byte feature, byte function,
        byte[] parameters, CancellationToken cancellationToken) =>
        ExchangeAsync(transport, slot, feature, NextFunction(function), parameters, cancellationToken);

    private async Task<LogitechReply> ExchangeAsync(IHidTransport transport, byte slot, byte feature,
        byte function, byte[] parameters, CancellationToken cancellationToken, byte? expectedEcho = null, byte? pingMarker = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await transport.WriteAsync(LogitechHidppProtocol.CreateRequest(slot, feature, function, parameters), cancellationToken);
        return await ReceiveAsync(transport, slot, feature, function, cancellationToken, expectedEcho, pingMarker);
    }

    private static async Task<LogitechReply> ReceiveAsync(IHidTransport transport, byte slot, byte feature,
        byte function, CancellationToken cancellationToken, byte? expectedEcho = null, byte? pingMarker = null)
    {
        var elapsed = Stopwatch.StartNew();
        for (int packets = 0; packets < 64; packets++)
        {
            var remaining = Timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var report = await transport.ReadAsync(remaining, cancellationToken);
            if (!LogitechHidppProtocol.TryParseReply(report, slot, feature, function, out var reply)) continue;
            if (reply!.ErrorCode is not null)
                throw new PollingException(reply.LegacyError && reply.ErrorCode is 0x04 or 0x08
                    ? PollingRateState.Disconnected : PollingRateState.Failed,
                    $"HID++ {(reply.LegacyError ? "1.0" : "2.0")} returned error 0x{reply.ErrorCode:X2}; no command is retried.");
            if (expectedEcho is not null && (reply.Parameters.Length == 0 || reply.Parameters[0] != expectedEcho)) continue;
            if (pingMarker is not null && (reply.Parameters.Length < 3 || reply.Parameters[2] != pingMarker)) continue;
            return reply;
        }
        throw new TimeoutException("No matching HID++ reply arrived before the deadline; this session must not be reused.");
    }

    private byte NextFunction(byte function) => (byte)((function & 0xF0) | (8 + (Interlocked.Increment(ref softwareId) & 7)));

    private static bool TryIdentity(HidEndpoint endpoint, PeripheralIdentity device, out Identity? identity)
    {
        identity = null;
        string prefix = $"{endpoint.ReceiverId}/logitech/";
        if (device.Vendor != DeviceVendor.Logitech || device.ReceiverId != endpoint.ReceiverId ||
            !device.Id.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string[] parts = device.Id[prefix.Length..].Split('/');
        if (parts.Length != 3 || !byte.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte slot) ||
            slot is < 1 or > 6 || parts[0] != slot.ToString(CultureInfo.InvariantCulture)) return false;
        if (parts[1] == "unit" && IsHex(parts[2], 8)) identity = new(slot, null, null, parts[2]);
        else if (IsHex(parts[1], 4) && IsHex(parts[2], 8) && (device.ModelId is null || device.ModelId == parts[1]))
            identity = new(slot, parts[1], parts[2], null);
        return identity is not null;
    }

    private static bool IsHex(string value, int length) => value.Length == length &&
        value.Any(character => character != '0') && value.Any(character => character != 'F') &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void RequireAuthorization(Func<bool> canApply, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!canApply()) throw new PollingException(PollingRateState.Unsupported, "Polling control is disabled or this Apply request is no longer current; no setting was written.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static PollingRateInfo Unavailable(PeripheralIdentity device, PollingRateState state, string detail) =>
        new(device.Id, state, null, [], detail);
    private const string RateDetail = "Mouse-advertised options through this receiver; receiver/firmware compatibility is unverified. Read-back verifies configuration, not physical USB timing.";
    private static string FailureDetail(bool writeAttempted, string error) => writeAttempted
        ? $"The write may have reached the mouse, but the requested rate was not verified. No retry or rollback was sent. {error}"
        : $"No rate setting was written. {error}";
    private static string ModeFailureDetail(bool modeWriteAttempted, bool writeAttempted, string error) =>
        (modeWriteAttempted ? "The software/onboard profile mode may have changed. " : "") + FailureDetail(writeAttempted, error);
    private sealed record Identity(byte Slot, string? WirelessId, string? Serial, string? UnitId);
    private sealed record Probe(Identity Identity, ushort FeatureId, byte FeatureIndex, PollingRateInfo Info);
    private sealed class PollingException(PollingRateState state, string detail) : Exception(detail)
    { public PollingRateState State { get; } = state; }
}
