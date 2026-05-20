using AutoMapper;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Online.Lobbies.Domain;

namespace YARG.Online.Lobbies.Mapping;

public sealed class LobbyProfile : Profile
{
    public LobbyProfile()
    {
        CreateMap<Lobby, LobbyDto>();
        CreateMap<QueuedSong, QueuedSongDto>();
    }
}
