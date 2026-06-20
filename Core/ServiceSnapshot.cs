namespace DevSpaceManager.Core;

internal sealed record ServiceSnapshot(
    bool DevSpaceRunning,
    bool TunnelRunning,
    bool LocalHealthOk,
    bool PublicHealthOk,
    string LocalHealthMessage,
    string PublicHealthMessage,
    DateTimeOffset CheckedAt);
