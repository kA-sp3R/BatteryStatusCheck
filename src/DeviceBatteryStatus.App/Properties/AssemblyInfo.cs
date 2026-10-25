using System.Runtime.CompilerServices;

// The separate CI harness exercises the same internal controls shipped to users.
// Its executable and synthetic fixtures are never part of an application package.
[assembly: InternalsVisibleTo("DeviceBatteryStatus.Smoke")]
