using System.Collections.ObjectModel;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Protocols;

/// <summary>A completed, device-reported association scan can prove prior children were removed.</summary>
public sealed class ReceiverSnapshotBatch : ReadOnlyCollection<DeviceSnapshot>
{
    public bool AssociationComplete { get; }

    public ReceiverSnapshotBatch(IEnumerable<DeviceSnapshot> snapshots, bool associationComplete)
        : base(snapshots.ToArray()) => AssociationComplete = associationComplete;
}
