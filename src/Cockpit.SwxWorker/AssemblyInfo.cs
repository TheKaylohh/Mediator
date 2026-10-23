// net8.0-windows builds get [SupportedOSPlatform("windows")] from the SDK; verify builds (net8.0) declare it
// here so the platform analyzer still treats every API in this worker as Windows-only.
#if !WINDOWS
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
