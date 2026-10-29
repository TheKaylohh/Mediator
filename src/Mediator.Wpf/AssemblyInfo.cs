// net8.0-windows builds get [SupportedOSPlatform("windows")] from the SDK; the verify build (net8.0, against
// verify/stubs/WpfCompileStub) declares it here, so the platform analyzer treats the overlay as Windows-only in both.
#if !WINDOWS
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
