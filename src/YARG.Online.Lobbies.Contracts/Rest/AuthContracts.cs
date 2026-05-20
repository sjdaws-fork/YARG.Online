using System;

namespace YARG.Online.Lobbies.Contracts.Rest;

public sealed record DevAuthRequest(string Name);

public sealed record DevAuthResponse(
    string Token,
    string UserId,
    string DisplayName,
    DateTimeOffset ExpiresAt);
