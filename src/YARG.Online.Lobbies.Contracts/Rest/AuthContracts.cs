using System;

namespace YARG.Online.Lobbies.Contracts.Rest;

public sealed record DevAuthRequest(string Name, string? ClientVersion = null);

public sealed record DevAuthResponse(
    string Token,
    string UserId,
    string DisplayName,
    DateTimeOffset ExpiresAt);

/// <summary>Returned with HTTP 426 when the client version is below the server minimum.</summary>
public sealed record ClientVersionError(string Error, string MinVersion);
